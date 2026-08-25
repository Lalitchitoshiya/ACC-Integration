using System.ComponentModel.DataAnnotations;

namespace Connector.Api.Domain;

// Entities per specs/09-data-model.md. Phase 1 scope: Project, User, ProjectMembership,
// Model, ModelVersion, AuditEvent. CheckoutState/ReviewEvent tables are included in the
// schema now (cheap) but have no endpoints until Phase 2.

public enum ProjectRole { Modeler, Reviewer, Viewer, Admin }

public enum ToolType { InfoWorksWSPro } // Phase 4 adds InfoWaterPro, InfoDrainage, InfoWorksICM, Civil3D

public enum ReviewStatus { Draft, InReview, Approved, Rejected }

public class Project
{
    public Guid Id { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(200)] public required string AccHubUrn { get; set; }
    [MaxLength(200)] public required string AccProjectUrn { get; set; }
    public List<Model> Models { get; set; } = [];
    public List<ProjectMembership> Memberships { get; set; } = [];
}

public class User
{
    public Guid Id { get; set; }
    [MaxLength(200)] public required string AccUserId { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(320)] public required string Email { get; set; }
    public bool Active { get; set; } = true;
}

public class ProjectMembership
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public ProjectRole Role { get; set; }
    public Project? Project { get; set; }
    public User? User { get; set; }
}

public class Model
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    public ToolType ToolType { get; set; }
    [MaxLength(400)] public required string AccFolderUrn { get; set; }
    public Guid? CurrentApprovedVersionId { get; set; }
    public Project? Project { get; set; }
    public List<ModelVersion> Versions { get; set; } = [];
    public CheckoutState? CheckoutState { get; set; }
}

public class ModelVersion
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    // Sequential per model, mirrors ACC's version numbering for drift detection (spec 04).
    public int VersionNumber { get; set; }
    [MaxLength(400)] public required string AccItemVersionUrn { get; set; }
    public Guid UploadedById { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    [MaxLength(4000)] public required string ChangeDescription { get; set; }
    [MaxLength(100)] public required string SourceTool { get; set; }
    [MaxLength(100)] public required string SourceToolVersion { get; set; }
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Draft;
    public long FileSizeBytes { get; set; }
    // Set when a download attempt finds the file gone from ACC (deleted/moved outside
    // the connector). Never auto-cleared and never causes the row to be deleted — audit
    // history (specs/07) must survive external deletion; this only flags it honestly.
    public bool AccFileMissing { get; set; }
    public DateTimeOffset? AccMissingDetectedAt { get; set; }
    // Companion PNG map image, uploaded alongside the version so ACC's native file
    // preview shows a visual — ACC doesn't preview CSV/INP or accept SVG uploads at all.
    [MaxLength(400)] public string? PreviewImageUrn { get; set; }
    // JSONB column; schema defined in specs/13-metadata-schema.md. Null when parsing failed.
    public string? MetadataJson { get; set; }
    [MaxLength(2000)] public string? ParseError { get; set; }
    public Model? Model { get; set; }
    public User? UploadedBy { get; set; }
    public List<ReviewEvent> ReviewEvents { get; set; } = [];
}

public class ReviewEvent
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public Guid ActorId { get; set; }
    [MaxLength(50)] public required string Action { get; set; } // submitted | approved | rejected
    [MaxLength(4000)] public string? Comment { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public ModelVersion? Version { get; set; }
}

public class CheckoutState
{
    public Guid ModelId { get; set; } // PK — at most one active checkout per model (spec 02)
    public Guid CheckedOutById { get; set; }
    public DateTimeOffset CheckedOutAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Model? Model { get; set; }
    public User? CheckedOutBy { get; set; }
}

public class AuditEvent
{
    public long Id { get; set; } // bigserial — append-only, ordered
    public Guid ProjectId { get; set; }
    [MaxLength(100)] public required string EventType { get; set; }
    // Denormalized snapshot so history survives user deletion (spec 07 edge cases).
    public required string ActorSnapshotJson { get; set; }
    public Guid? ModelId { get; set; }
    public Guid? VersionId { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
