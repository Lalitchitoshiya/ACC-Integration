using Connector.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Connector.Api.Data;

/// <summary>Development-only seed: one project, four users (one per role). Not run in production.</summary>
public static class DevSeed
{
    public static async Task EnsureSeededAsync(ConnectorDbContext db)
    {
        if (await db.Projects.AnyAsync()) return;

        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "Demo Water Network",
            AccHubUrn = "mock:hub:demo", AccProjectUrn = "mock:project:demo"
        };
        var users = new (string Name, string Email, ProjectRole Role)[]
        {
            ("Asha Modeler", "modeler@demo.local", ProjectRole.Modeler),
            ("Ravi Reviewer", "reviewer@demo.local", ProjectRole.Reviewer),
            ("Vik Viewer", "viewer@demo.local", ProjectRole.Viewer),
            ("Ada Admin", "admin@demo.local", ProjectRole.Admin),
        };

        db.Projects.Add(project);
        foreach (var (name, email, role) in users)
        {
            var user = new User
            {
                Id = Guid.NewGuid(), AccUserId = $"mock:user:{email}", Name = name, Email = email
            };
            db.Users.Add(user);
            db.ProjectMemberships.Add(new ProjectMembership
            {
                ProjectId = project.Id, UserId = user.Id, Role = role
            });
        }
        await db.SaveChangesAsync();
    }
}
