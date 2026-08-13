# 01 — Upload

Status: Draft v1
Depends on: [00-overview.md](00-overview.md)

## Purpose

Let a Modeler push a water network model from a native desktop tool into ACC as a new, non-destructive version, with enough captured metadata that other users can understand what changed without opening the file.

## Requirements

- **FR1.1**: User can upload a model file (or export package) to a specific ACC project/folder from a desktop plugin or a web UI.
- **FR1.2**: Upload creates a new **version** of the ACC item, never overwrites an existing file object.
- **FR1.3**: Upload requires: source tool + tool version, a change description (free text, required, min 10 chars), and auto-captured author + timestamp.
- **FR1.4**: System computes and stores model metadata on upload via a parser appropriate to the source format — full schema (network element counts by type, attribute summaries, extent, named-element index) defined in [13-metadata-schema.md](13-metadata-schema.md).
- **FR1.5**: Upload target model must already exist in the connector's data model (created by an Admin or first-upload-creates-model flow); orphan uploads are rejected with a clear error, not silently placed in a default folder.
- **FR1.6**: New version's initial `reviewStatus` = `Draft`.

## Flow

1. User selects "Upload to ACC" from desktop plugin (or drags file into web UI).
2. Plugin/UI collects: change description, confirms target Model.
3. Connector API authenticates via APS OAuth2 (user's token), uploads file bytes to ACC via Data Management API (creates new version of the target Item).
4. Connector runs metadata extraction (format-specific parser) against the uploaded file.
5. Connector writes a `Version` record: `accItemVersionUrn`, `uploadedBy`, `uploadedAt`, `changeDescription`, `metadata`, `reviewStatus = Draft`.
6. Connector emits an upload event (consumed by [07-notifications-audit.md](07-notifications-audit.md)).

## Edge Cases

- Upload while another user holds checkout: allowed, but flagged with a warning (see [02-checkout.md](02-checkout.md)) — checkout is advisory, not a hard block.
- Upload of a file that fails metadata parsing (corrupt/unsupported format): version is still created in ACC (don't block storage), but `metadata` is null and a `parseError` field is set; surfaced in UI.
- Duplicate upload (same file hash as current latest version): allowed, but UI warns "identical to current version" before submission is confirmed.

## Acceptance Criteria

1. Uploading a model from InfoWorks WS Pro creates a new version visible in ACC Docs' native version history for that item.
2. The connector's own `Version` record and ACC's version are correctly linked (via `accItemVersionUrn`) and never diverge.
3. Change description is mandatory — upload attempt with empty/short description is rejected client-side before hitting the API.
4. Node/link counts computed on upload match a manual count on a known test model within tolerance (exact for well-formed files).

## Open Questions

- Max concurrent uploads per user/session — needed for large ICM model batch scenarios?
- Should upload support resumable/chunked transfer for the 2GB+ case (Data Management API resumable upload)?
