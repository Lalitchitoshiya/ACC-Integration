# 12 — Permissions & Error Handling

Status: Draft v1
Depends on: [00-overview.md](00-overview.md), [09-data-model.md](09-data-model.md)

## Purpose

Every module spec says "role X can do Y" in prose. This is the one place that turns those scattered statements into a single enforceable matrix and a consistent error contract, so role checks aren't reimplemented (and drift) per endpoint.

## Role → Action Matrix

Roles are scoped per-project via `ProjectMembership` (see [09-data-model.md](09-data-model.md)) — a user has no permissions outside a project they're a member of.

| Action | Modeler | Reviewer | Viewer | Admin |
|---|---|---|---|---|
| Upload version ([01](01-upload.md)) | ✅ | ✅ | ❌ | ✅ |
| Checkout / release own checkout ([02](02-checkout.md)) | ✅ | ✅ | ❌ | ✅ |
| Override another user's checkout ([02](02-checkout.md)) | ✅ (logged) | ✅ (logged) | ❌ | ✅ |
| Release *another user's* checkout without override semantics | ❌ | ❌ | ❌ | ✅ |
| Submit version for review ([03](03-review-workflow.md)) | ✅ | ✅ | ❌ | ✅ |
| Approve / reject version ([03](03-review-workflow.md)) | ❌ | ✅ | ❌ | ✅ |
| View version history / download ([04](04-version-history.md)) | ✅ | ✅ | ✅ | ✅ |
| Compare versions ([05](05-comparison.md)) | ✅ | ✅ | ✅ | ✅ |
| Trigger cross-tool export ([06](06-cross-tool-exchange.md)) | ✅ | ✅ | ❌ | ✅ |
| Edit field-mapping config ([06](06-cross-tool-exchange.md)) | ❌ | ❌ | ❌ | ✅ |
| Subscribe / view audit log ([07](07-notifications-audit.md)) | ✅ | ✅ | ✅ | ✅ |
| Export audit CSV ([07](07-notifications-audit.md)) | ❌ | ❌ | ❌ | ✅ (PM access TBD, see Open Questions) |
| Create Model, manage project roles ([09](09-data-model.md)) | ❌ | ❌ | ❌ | ✅ |

Enforcement is **API-level, not UI-level** — every endpoint in [11-api-contracts.md](11-api-contracts.md) checks `ProjectMembership.role` server-side before executing; hiding a button client-side is a UX nicety, never the security boundary (this is explicit in FR5's acceptance criteria and applies uniformly here).

## Error Response Contract

All API errors (see [11-api-contracts.md](11-api-contracts.md)) follow one shape:

```json
{
  "error": "machine_readable_code",
  "message": "human readable description",
  "details": { }
}
```

| HTTP status | `error` code | Used for |
|---|---|---|
| 400 | `validation_failed` | Missing/invalid required field (e.g., `comment_required` on reject, short `changeDescription`) |
| 401 | `unauthenticated` | Missing/expired APS token |
| 403 | `forbidden` | Authenticated, but role doesn't permit the action (per matrix above) |
| 404 | `not_found` | Referenced Model/Version/Project doesn't exist |
| 409 | `conflict` | State conflicts: checkout held, version already reviewed, model already exists |
| 422 | `unprocessable` | Well-formed request, but the action is invalid for current state (e.g., approving a `Draft` version directly, skipping `InReview`) |
| 502 | `upstream_error` | ACC/APS API call failed — connector must not silently swallow this as if the local operation succeeded |
| 500 | `internal_error` | Unhandled connector fault |

## Cross-Module Rules

- **No partial success on multi-step operations.** E.g., upload (FR1.2) writes the ACC version *and* the connector's `Version` record — if the connector-side write fails after the ACC upload succeeds, the endpoint must retry the local write or surface a `502`/reconciliation-needed state, never return `201` with a missing local record (this is what FR4's "no drift" acceptance criterion in [04-version-history.md](04-version-history.md) depends on).
- **Soft-lock actions never return `403`** — checkout conflicts are `409` (a state conflict a client can act on: show holder, offer override), not a permissions failure, per [02-checkout.md](02-checkout.md).
- **Self-review is not a permissions error.** Per [03-review-workflow.md](03-review-workflow.md), a Reviewer approving their own upload is allowed in v1 — this is a `200` with `selfApproved: true` on the resulting event, not a `403`. Don't conflate "flagged for audit" with "forbidden."
- **Rate limiting** (per [07-notifications-audit.md](07-notifications-audit.md)) applies to notification dispatch only, never to the underlying write/audit-log path — a write must never fail or degrade because of notification throttling.

## Acceptance Criteria

1. Every mutating endpoint in [11-api-contracts.md](11-api-contracts.md) has an automated test asserting the correct `403` for each role that should be denied.
2. Error responses across all endpoints conform to the single JSON shape above — no endpoint-specific ad hoc error formats.
3. A simulated ACC upstream failure during upload does not produce a `Version` record with no corresponding ACC file, or vice versa (no drift).

## Open Questions

- Does audit CSV export need a PM-specific role distinct from Admin, or is Admin-only acceptable for the pilot (currently assumed Admin-only above)?
- Should `403` responses distinguish "wrong role" from "not a project member at all," or is a uniform `forbidden` sufficient for v1 (avoids leaking project membership info to non-members)?
