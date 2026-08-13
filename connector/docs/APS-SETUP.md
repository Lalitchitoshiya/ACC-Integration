# APS App Setup (for real ACC integration)

Prerequisite for switching `Acc:UseMock` to `false`. You said you have an Autodesk account but no APS app yet — steps:

1. **Create the app**: go to [aps.autodesk.com](https://aps.autodesk.com) → sign in → Applications → Create Application.
   - Type: "Traditional Web App" (we use 3-legged OAuth2 with a server-side callback).
   - Callback URL: `http://localhost:5000/api/auth/callback` (must match `Aps:CallbackUrl` in appsettings; add the production URL later).
   - APIs: enable **Data Management API** (minimum for Phase 1).
2. **Copy credentials** into user-secrets (never commit them):
   ```powershell
   dotnet user-secrets set "Aps:ClientId" "<client id>" --project src/Connector.Api
   dotnet user-secrets set "Aps:ClientSecret" "<client secret>" --project src/Connector.Api
   ```
3. **Provision ACC access**: an ACC account admin must add the app's Client ID under
   ACC Account Admin → Custom Integrations, for the hub/project used as the pilot.
   Without this step, 3-legged tokens authenticate but Data Management calls to the hub 404.
4. **Scopes** needed for Phase 1: `data:read data:write data:create account:read`.
5. Implement `ApsAccClient` (implementation order documented in the class) using the APS .NET SDK packages:
   `Autodesk.Authentication`, `Autodesk.DataManagement`, `Autodesk.Oss`.

Open item: which ACC hub/project hosts the pilot — flagged in [specs/00-overview.md](../../specs/00-overview.md) open questions.
