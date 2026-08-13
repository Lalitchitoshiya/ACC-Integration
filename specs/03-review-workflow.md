# 03 — Review & Approval Workflow

Status: Draft v1
Depends on: [00-overview.md](00-overview.md), [01-upload.md](01-upload.md)

## Purpose

Decouple "latest uploaded" from "latest authoritative." A version only becomes the one that Viewers/Modelers pull as default once a Reviewer has explicitly approved it.

## Requirements

- **FR5.1**: States: `Draft → In Review → Approved` or `Draft → In Review → Rejected → Draft`.
- **FR5.2**: Only users with Reviewer/Approver role can transition `In Review → Approved` or `In Review → Rejected`.
- **FR5.3**: Rejection requires a comment (free text, required).
- **FR5.4**: State transitions are logged with actor + timestamp (audit trail, see [07-notifications-audit.md](07-notifications-audit.md)).
- **FR5.5**: A Modeler (or Admin) explicitly submits a `Draft` version `In Review` — review is not automatic on upload.
- **FR5.6**: On `Approved`, the `Model.currentApprovedVersionId` is updated; this is the version returned by "download latest approved" ([04-version-history.md](04-version-history.md)).
- **FR5.7**: Approving a version does not retroactively invalidate a later `Draft`/`In Review` version — only the most recently *approved* version is authoritative at any time.

## Flow

1. Modeler uploads a version (`Draft`, per [01-upload.md](01-upload.md)).
2. Modeler submits it for review → status = `In Review`; reviewers subscribed to the model are notified.
3. Reviewer opens the version (downloads it, or uses [05-comparison.md](05-comparison.md) against current approved), then:
   - **Approve** → status = `Approved`, `Model.currentApprovedVersionId` updated, uploader notified.
   - **Reject** → status = `Rejected`, comment required, uploader notified; Modeler may revise and re-upload as a new `Draft` version (rejected version itself is not editable in place).

## Edge Cases

- Two reviewers act on the same `In Review` version near-simultaneously: first transition wins (optimistic lock on `Version.reviewStatus`); second reviewer's action fails with a "already reviewed by X" message.
- Model has no `Approved` version yet (first-ever submission still in review): "download latest approved" falls back to explicit "no approved version" response, not silently to latest draft (see FR3.2 in [04-version-history.md](04-version-history.md)).
- Reviewer and uploader are the same person: allowed for v1 (self-review) but flagged distinctly in the audit log (`review.selfApproved = true`) so it's visible/reportable later — restricting this is a policy decision, not enforced in v1.

## Acceptance Criteria

1. A version in `Draft` cannot be approved directly — must pass through `In Review`.
2. Non-Reviewer users cannot call approve/reject (API-level role check, not just UI hiding).
3. Rejecting without a comment is rejected by the API.
4. After approval, a Viewer's "download latest" call returns exactly this version, not a later unapproved draft.
5. Full transition history (who, when, what comment) is visible per version.

## Open Questions

- Should self-review be disallowed outright in a future version, or is it acceptable given small pilot teams?
- Multi-reviewer / quorum approval — needed for regulated environments, or is single-reviewer sufficient for v1?
