# 11 — API Contracts

Status: Draft v1
Depends on: [00-overview.md](00-overview.md), [08-architecture.md](08-architecture.md), [09-data-model.md](09-data-model.md), [12-permissions-errors.md](12-permissions-errors.md)

## Purpose

Concrete request/response shapes for the Connector API, so desktop plugins and the web dashboard are built against one contract instead of each module's prose flow being reinterpreted differently. All endpoints are versioned under `/api/v1`, authenticated via APS OAuth2 bearer token, and scoped to a Project (`projectId` in the path) unless noted.

Every response follows the envelope and error format defined in [12-permissions-errors.md](12-permissions-errors.md) — not repeated per-endpoint below.

## Models

- `POST /api/v1/projects/{projectId}/models` — create a Model (Admin only). Body: `{ name, toolType, accFolderUrn }`. → `201 { model }`
- `GET /api/v1/projects/{projectId}/models` — list Models in a project. → `200 { models[] }`
- `GET /api/v1/models/{modelId}` — get one Model, including `currentApprovedVersionId` and current `CheckoutState`. → `200 { model }`

## Upload — [01-upload.md](01-upload.md)

- `POST /api/v1/models/{modelId}/versions` — multipart upload. Fields: `file`, `changeDescription` (min 10 chars, required), `sourceTool`, `sourceToolVersion`.
  → `201 { version: { id, accItemVersionUrn, reviewStatus: "Draft", metadata | parseError } }`
  → `409` if `modelId` doesn't exist (no silent default-folder placement, per FR1.5).

## Checkout — [02-checkout.md](02-checkout.md)

- `POST /api/v1/models/{modelId}/checkout` — Body: `{ override?: boolean }`.
  → `200 { checkoutState }` on success.
  → `409 { error: "checked_out", holder: { userId, name }, checkedOutAt }` if held and `override` not set.
  → `200` with `checkout.override` audit event emitted if `override: true` explicitly passed.
- `DELETE /api/v1/models/{modelId}/checkout` — release. Owner or Admin only. → `204`.
- `GET /api/v1/models/{modelId}/checkout` — current state (or `null`). → `200 { checkoutState | null }`.

## Review Workflow — [03-review-workflow.md](03-review-workflow.md)

- `POST /api/v1/versions/{versionId}/submit` — `Draft → InReview`. Modeler/Admin. → `200 { version }`
- `POST /api/v1/versions/{versionId}/approve` — `InReview → Approved`. Reviewer/Admin only. Updates `Model.currentApprovedVersionId`. → `200 { version }`
  → `409 { error: "already_reviewed", by }` if another reviewer already transitioned it.
- `POST /api/v1/versions/{versionId}/reject` — Body: `{ comment }` (required). Reviewer/Admin only. → `200 { version }`
  → `400 { error: "comment_required" }` if missing.
- `GET /api/v1/versions/{versionId}/review-events` — full transition history for a version. → `200 { reviewEvents[] }`

## Version History — [04-version-history.md](04-version-history.md)

- `GET /api/v1/models/{modelId}/versions?page=&pageSize=` — newest-first, paginated (default pageSize 20). → `200 { versions[], nextPage }`
- `GET /api/v1/models/{modelId}/versions/latest-approved` — → `200 { version }` or `200 { version: null, fallback: { version, reviewStatus } }` per FR3.2 (never a bare 404 — the fallback payload is the point).
- `GET /api/v1/versions/{versionId}/download` — → `302` redirect to a signed ACC download URL (short-lived).

## Comparison — [05-comparison.md](05-comparison.md)

- `GET /api/v1/models/{modelId}/compare?versionA={id}&versionB={id}` →
  `200 { comparable: boolean, deltas: { nodeCount, linkCount, catchmentCount, extent }, elementDiff?: [...], warnings: [ "versionB: metadata unavailable" ] }`
  `comparable: false` (with reason) when source tools/formats aren't compatible, per FR6 edge cases — not an error, a valid response shape.

## Cross-Tool Exchange — [06-cross-tool-exchange.md](06-cross-tool-exchange.md)

- `POST /api/v1/versions/{versionId}/export` — Body: `{ targetFormat: "epanet-inp" | "swmm-inp" | "arcgis-un" }`.
  → `202 { exportJobId }` (async — conversion may take time on large models).
- `GET /api/v1/export-jobs/{exportJobId}` — → `200 { status: "pending"|"completed"|"failed", resultUrl?, unmappedFieldCount?, error? }`
- `GET /api/v1/field-mappings?sourceTool=&targetSchema=` — read current mapping config. Admin-editable via same resource (`PUT`), not covered further here — config management UI is a Phase 3 detail.

## Notifications & Audit — [07-notifications-audit.md](07-notifications-audit.md)

- `POST /api/v1/models/{modelId}/subscriptions` — subscribe current user. → `204`
- `DELETE /api/v1/models/{modelId}/subscriptions` — unsubscribe. → `204`
- `GET /api/v1/projects/{projectId}/audit-events?from=&to=&eventType=&modelId=` — → `200 { events[] }`
- `GET /api/v1/projects/{projectId}/audit-events/export?format=csv&...` — → `200` streamed CSV; itself emits `audit.exported`.

## Conventions

- All timestamps: ISO 8601 UTC.
- All list endpoints: paginated with `page`/`pageSize`, default `pageSize=20`, max `100`.
- IDs are connector-internal UUIDs; ACC-native identifiers (`accItemVersionUrn`, `accFolderUrn`) are always namespaced/labeled as such, never conflated with connector IDs — this distinction matters because [04-version-history.md](04-version-history.md) has to detect drift between the two.
- Mutating endpoints (`POST`/`DELETE`) are idempotent where the underlying action is idempotent (e.g., unsubscribe); upload and export explicitly are **not** idempotent — retrying a failed upload creates a new version, per FR1.2's "never overwrite" rule.

## Open Questions

- Webhook vs. polling for ACC-side events (e.g., detecting an "unmanaged" direct-to-ACC upload per [04](04-version-history.md))? APS supports webhooks — worth using instead of polling for that reconciliation.
- Should export ([06](06-cross-tool-exchange.md)) support a synchronous path for small models, or is async-only acceptable for v1 simplicity?
