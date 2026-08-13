# 04 — Version History & Latest-Approved Retrieval

Status: Draft v1
Depends on: [00-overview.md](00-overview.md), [01-upload.md](01-upload.md), [03-review-workflow.md](03-review-workflow.md)

## Purpose

Give every user — regardless of tool or role — a single reliable answer to "what's the current model, and how did we get here."

## Requirements

- **FR3.1**: User can fetch the latest version whose `reviewStatus = Approved` for a given model.
- **FR3.2**: If no approved version exists, user is shown the latest version regardless of status, clearly labeled "Unapproved / Draft" — never silently served as if it were approved.
- **FR4.1**: Every model shows a chronological list of versions with author, timestamp, change description, review status, and file size.
- **FR4.2**: User can download any historical version, not just latest.

## Flow

1. Dashboard/plugin requests version list for a Model.
2. Connector returns versions ordered newest-first, each with: version number, `uploadedBy`, `uploadedAt`, `changeDescription`, `reviewStatus`, file size, metadata summary (node/link counts).
3. "Get latest approved" endpoint separately resolves `Model.currentApprovedVersionId`; if null, returns the flagged fallback per FR3.2.
4. Download of any specific version proxies through to ACC's Data Management API for that item version's storage location (signed URL).

## Edge Cases

- Model has versions but all are `Rejected` (no `Draft`/`In Review`/`Approved` remaining active): "latest" fallback still shows the most recent version chronologically, labeled with its actual status (`Rejected`), not hidden.
- Very long history (100+ versions): paginate; default view shows most recent 20 with load-more.
- ACC-side version exists but connector's `Version` record is missing (e.g., someone uploaded directly in ACC Docs bypassing the plugin): flagged in UI as "Unmanaged version — uploaded outside connector, no metadata/review status available."

## Acceptance Criteria

1. Version list matches ACC's own version count for the underlying item (no drift).
2. "Latest approved" call returns null/flagged response (not an error, not silent wrong data) when no approved version exists.
3. Any historical version is downloadable, and downloading version N does not affect `currentApprovedVersionId` or checkout state.
4. Direct-to-ACC uploads (bypassing the connector) are detected and clearly flagged rather than silently absorbed into history with fabricated metadata.

## Open Questions

- Do we reconcile/backfill connector metadata for "unmanaged" ACC-native uploads, or leave them permanently flagged as out-of-band?
- Retention: does version history need pruning/archival policy, or is ACC's native retention sufficient for all versions indefinitely?
