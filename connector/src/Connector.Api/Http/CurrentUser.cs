using Connector.Api.Data;
using Connector.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Connector.Api.Http;

/// <summary>
/// Resolves the acting user and their per-project role (specs/12-permissions-errors.md).
///
/// Phase 1 dev auth: the caller identifies via the X-Dev-User header (email of a seeded
/// User row). This is a development stand-in only — replaced by APS OAuth2 3-legged token
/// validation when ApsAccClient goes live, without changing any endpoint code (they only
/// depend on this service's interface).
/// </summary>
public class CurrentUserService(ConnectorDbContext db, IHttpContextAccessor http)
{
    public async Task<User?> GetUserAsync(CancellationToken ct)
    {
        var email = http.HttpContext?.Request.Headers["X-Dev-User"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(email)) return null;
        return await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.Active, ct);
    }

    public async Task<ProjectRole?> GetRoleAsync(Guid userId, Guid projectId, CancellationToken ct)
    {
        var m = await db.ProjectMemberships
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ProjectId == projectId, ct);
        return m?.Role;
    }
}
