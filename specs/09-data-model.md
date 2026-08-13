# 09 — Data Model

Status: Draft v1
Depends on: [00-overview.md](00-overview.md), [08-architecture.md](08-architecture.md)

Owned by the Connector service (its own DB — ACC is the file/version store, but has no concept of hydraulic-model metadata, checkout state, or review workflow, so the connector maintains this alongside).

```
Project
 - id, name, accHubUrn, accProjectUrn, retentionPolicy

User
 - id, accUserId, name, email, active (bool)

ProjectMembership
 - projectId, userId, role (Modeler|Reviewer|Viewer|Admin)
 (a user's role is scoped per-project — no global role; see 12-permissions-errors.md)

Model
 - id, projectId, name, toolType (enum), accFolderUrn, currentApprovedVersionId

Version
 - id, modelId, accItemVersionUrn, uploadedBy (userId), uploadedAt, changeDescription,
   reviewStatus (Draft|InReview|Approved|Rejected),
   metadata (JSON, schema in 13-metadata-schema.md; nullable on parse failure),
   parseError (nullable)

ReviewEvent
 - id, versionId, actor (userId), action, comment, timestamp

CheckoutState
 - modelId (unique), checkedOutBy (userId), checkedOutAt, expiresAt

FieldMapping (config, not tied to a Project)
 - id, sourceTool, targetSchema, sourceField, targetField, transform

AuditEvent (append-only; see 07-notifications-audit.md)
 - id, projectId, eventType, actorSnapshot { userId, name, email at time of action },
   modelId (nullable), versionId (nullable), payload (event-specific fields), timestamp
```

## Entity Notes

- **Project**: top-level scope; maps 1:1 to an ACC project (`accProjectUrn`). All Models, memberships, and audit events are scoped under a Project.
- **User / ProjectMembership**: identity is sourced from Autodesk (via APS OAuth2 profile, `accUserId`), but role assignment is local to the connector and scoped per-project — the same person can be a Reviewer on one project and a Viewer on another. See [12-permissions-errors.md](12-permissions-errors.md) for how roles gate actions.
- **Model.accFolderUrn**: links the connector's Model record to its location in ACC Docs; a Model is scoped to one ACC folder/item lineage.
- **Model.currentApprovedVersionId**: denormalized pointer, updated only by the [03-review-workflow.md](03-review-workflow.md) approve transition — this is what "download latest approved" ([04-version-history.md](04-version-history.md)) resolves against.
- **Version.accItemVersionUrn**: links to the specific ACC item version created on upload ([01-upload.md](01-upload.md)); must never diverge from ACC's own version record.
- **Version.metadata**: populated by format-specific parsers at upload time; nullable if parsing failed (`parseError` set instead) — consumed by [05-comparison.md](05-comparison.md). Full schema defined in [13-metadata-schema.md](13-metadata-schema.md).
- **ReviewEvent**: append-only log of state transitions on a Version; feeds [07-notifications-audit.md](07-notifications-audit.md).
- **CheckoutState**: one active record per Model at most; see [02-checkout.md](02-checkout.md) for lifecycle.
- **FieldMapping**: config, not user data — externalized so schema mappings for [06-cross-tool-exchange.md](06-cross-tool-exchange.md) can change without a deploy.
- **AuditEvent**: the concrete table backing FR8.2/FR8.3 — `actorSnapshot` is a denormalized copy (not a live FK) so historical records survive user deletion, per [07-notifications-audit.md](07-notifications-audit.md) edge cases.

## Related

- Consumed/mutated by: [01-upload.md](01-upload.md), [02-checkout.md](02-checkout.md), [03-review-workflow.md](03-review-workflow.md), [04-version-history.md](04-version-history.md), [05-comparison.md](05-comparison.md), [06-cross-tool-exchange.md](06-cross-tool-exchange.md), [07-notifications-audit.md](07-notifications-audit.md)
