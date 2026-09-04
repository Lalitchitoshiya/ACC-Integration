using System.Net.Http.Headers;
using System.Text.Json;

namespace Connector.Api.Acc;

/// <summary>
/// APS OAuth2 token acquisition. Phase 1: 2-legged (client_credentials) for
/// service-level Data Management access via ACC Custom Integrations. 3-legged
/// (user-context) flow is added when dev header auth is replaced — see
/// specs/08-architecture.md NFRs: the service stores tokens only, never credentials
/// (credentials live in user-secrets / environment, not in the DB or appsettings).
/// </summary>
public class ApsTokenService(IConfiguration config, IHttpClientFactory httpFactory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    // 3-legged (user-context) token state. Dev-grade single-user store: the pilot has one
    // Autodesk login authorizing the connector. The refresh token is persisted to a local
    // gitignored file so restarts don't force re-login. Multi-user token storage moves to
    // the DB when dev header auth is replaced by real per-user login.
    private string? _userToken;
    private string? _userRefreshToken;
    private DateTimeOffset _userExpiresAt = DateTimeOffset.MinValue;
    private bool _refreshLoaded;

    private string RefreshTokenPath => Path.Combine(AppContext.BaseDirectory, ".aps-refresh-token");

    private string? UserRefreshToken
    {
        get
        {
            if (!_refreshLoaded)
            {
                _refreshLoaded = true;
                if (_userRefreshToken is null && File.Exists(RefreshTokenPath))
                    _userRefreshToken = File.ReadAllText(RefreshTokenPath).Trim();
            }
            return _userRefreshToken;
        }
        set
        {
            _userRefreshToken = value;
            if (value is not null) File.WriteAllText(RefreshTokenPath, value);
        }
    }

    public bool UserAuthorized => UserRefreshToken is not null;

    /// <summary>
    /// The one token rule for talking to ACC: the user's 3-legged token when someone has
    /// authorized, otherwise the app's 2-legged token.
    ///
    /// This must be the single place the rule lives. The uploader and the translation
    /// poller each carried their own copy of it, while the Viewer token endpoint always
    /// returned the 2-legged token — and derivatives of files in an ACC project's WIP
    /// bucket are only readable in user context. The result was a poller marking a
    /// manifest Success seconds before the Viewer got a 401 on the identical URL, with
    /// nothing in either log to connect the two.
    ///
    /// ExpiresIn is the real remaining lifetime of whichever token was chosen, so the
    /// Viewer's refresh callback fires at the right moment rather than on a guess.
    /// </summary>
    public async Task<(string Token, int ExpiresInSeconds, bool UserContext)> GetAccessTokenAsync(CancellationToken ct)
    {
        if (UserAuthorized)
        {
            try
            {
                var user = await GetThreeLeggedTokenAsync(ct);
                return (user, Remaining(_userExpiresAt), true);
            }
            catch (InvalidOperationException) when (UserAuthorizationExpired)
            {
                // Dead refresh token, already forgotten. Fall through to the app token so
                // uploads keep working; callers that need user context check the flag.
            }
        }
        var app = await GetTwoLeggedTokenAsync(ct);
        return (app, Remaining(_expiresAt), false);

        static int Remaining(DateTimeOffset expiresAt) =>
            (int)Math.Max(60, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
    }

    public string BuildAuthorizeUrl()
    {
        var clientId = config["Aps:ClientId"] ?? throw new InvalidOperationException("Aps:ClientId not configured.");
        var callback = config["Aps:CallbackUrl"] ?? throw new InvalidOperationException("Aps:CallbackUrl not configured.");
        // viewables:read lets the embedded Viewer fetch derivative geometry. Scopes are
        // fixed at authorization time — a refresh grant cannot add one — so a user who
        // authorized before this line existed must visit /api/auth/login once more.
        var scopes = Uri.EscapeDataString(config["Aps:UserScopes"] ?? "data:read data:write data:create viewables:read");
        return "https://developer.api.autodesk.com/authentication/v2/authorize" +
               $"?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(callback)}&scope={scopes}";
    }

    public async Task ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var json = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = config["Aps:CallbackUrl"]!
        }, ct);
        StoreUserToken(json);
        UserAuthorizationExpired = false;
    }

    public async Task<string> GetThreeLeggedTokenAsync(CancellationToken ct)
    {
        if (_userToken is not null && DateTimeOffset.UtcNow < _userExpiresAt - TimeSpan.FromMinutes(2))
            return _userToken;
        if (UserRefreshToken is null)
            throw new InvalidOperationException("No user authorization yet — visit /api/auth/login first.");

        JsonDocument json;
        try
        {
            json = await TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = UserRefreshToken
            }, ct);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("invalid_grant"))
        {
            // APS rotates refresh tokens on every use, so one consumed by another process
            // (a second connector instance, a parallel dashboard) is dead for this one, and
            // they also expire outright. Left in place, a dead token keeps UserAuthorized
            // true and turns every ACC call — upload, poll, viewer — into a 500 with the
            // cause buried in a stack trace. Forget it so callers fall back to the app
            // token and can tell the user precisely what to do.
            ForgetUserToken();
            UserAuthorizationExpired = true;
            throw new InvalidOperationException(
                "ACC user sign-in has expired — visit /api/auth/login to sign in again.", ex);
        }
        StoreUserToken(json);
        return _userToken!;
    }

    /// <summary>True once a stored user authorization has been found dead; cleared by a
    /// fresh sign-in. Lets the API distinguish "never signed in" from "needs to sign in
    /// again", which call for different messages.</summary>
    public bool UserAuthorizationExpired { get; private set; }

    private void ForgetUserToken()
    {
        _userToken = null;
        _userRefreshToken = null;
        _refreshLoaded = true; // do not re-read the dead token from disk on next access
        try { if (File.Exists(RefreshTokenPath)) File.Delete(RefreshTokenPath); }
        catch (IOException) { /* best effort — memory state already cleared */ }
    }

    private void StoreUserToken(JsonDocument json)
    {
        _userToken = json.RootElement.GetProperty("access_token").GetString()!;
        _userExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
        if (json.RootElement.TryGetProperty("refresh_token", out var rt))
            UserRefreshToken = rt.GetString();
        json.Dispose();
    }

    private async Task<JsonDocument> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var clientId = config["Aps:ClientId"]!;
        var clientSecret = config["Aps:ClientSecret"]!;
        var http = httpFactory.CreateClient("aps");
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://developer.api.autodesk.com/authentication/v2/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        req.Content = new FormUrlEncodedContent(form);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"APS token request failed ({(int)res.StatusCode}): {body}");
        return JsonDocument.Parse(body);
    }

    public bool CredentialsConfigured =>
        !string.IsNullOrEmpty(config["Aps:ClientId"]) && !string.IsNullOrEmpty(config["Aps:ClientSecret"]);

    public async Task<string> GetTwoLeggedTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromMinutes(2))
            return _cachedToken;

        await Gate.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromMinutes(2))
                return _cachedToken;

            var clientId = config["Aps:ClientId"]
                ?? throw new InvalidOperationException("Aps:ClientId not configured — see docs/APS-SETUP.md.");
            var clientSecret = config["Aps:ClientSecret"]
                ?? throw new InvalidOperationException("Aps:ClientSecret not configured — see docs/APS-SETUP.md.");

            var http = httpFactory.CreateClient("aps");
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://developer.api.autodesk.com/authentication/v2/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                // account:read intentionally excluded — needs ACC Account Admin API access
                // that personal-hub APS apps may lack (AUTH-001); Phase 1 only needs data scopes.
                //
                // viewables:read is what the embedded Viewer needs to fetch a derivative's
                // manifest and geometry assets. Without it Document.load fails with a bare
                // 401 and renders an empty canvas — the data scopes are enough to upload a
                // file and submit a translation, but not to read the result back.
                ["scope"] = config["Aps:Scopes"]
                    ?? "data:read data:write data:create bucket:create bucket:read viewables:read"
            });

            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"APS token request failed ({(int)res.StatusCode}): {body}");

            using var json = JsonDocument.Parse(body);
            _cachedToken = json.RootElement.GetProperty("access_token").GetString()!;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
            return _cachedToken;
        }
        finally
        {
            Gate.Release();
        }
    }
}
