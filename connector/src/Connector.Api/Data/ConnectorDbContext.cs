using Connector.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Connector.Api.Data;

public class ConnectorDbContext(DbContextOptions<ConnectorDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ProjectMembership> ProjectMemberships => Set<ProjectMembership>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();
    public DbSet<ReviewEvent> ReviewEvents => Set<ReviewEvent>();
    public DbSet<CheckoutState> CheckoutStates => Set<CheckoutState>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProjectMembership>().HasKey(m => new { m.ProjectId, m.UserId });

        b.Entity<User>().HasIndex(u => u.AccUserId).IsUnique();

        b.Entity<Model>()
            .HasIndex(m => new { m.ProjectId, m.Name }).IsUnique();

        b.Entity<ModelVersion>()
            .HasIndex(v => new { v.ModelId, v.VersionNumber }).IsUnique();
        b.Entity<ModelVersion>()
            .HasIndex(v => v.AccItemVersionUrn).IsUnique();
        b.Entity<ModelVersion>()
            .Property(v => v.MetadataJson).HasColumnType("jsonb");
        // Optimistic concurrency for review-state races (spec 03 edge cases) — Postgres xmin.
        b.Entity<ModelVersion>()
            .Property<uint>("xmin").IsRowVersion();

        b.Entity<CheckoutState>().HasKey(c => c.ModelId);
        b.Entity<CheckoutState>()
            .Property<uint>("xmin").IsRowVersion();

        b.Entity<AuditEvent>()
            .Property(a => a.ActorSnapshotJson).HasColumnType("jsonb");
        b.Entity<AuditEvent>()
            .Property(a => a.PayloadJson).HasColumnType("jsonb");
        b.Entity<AuditEvent>()
            .HasIndex(a => new { a.ProjectId, a.Timestamp });
    }
}
