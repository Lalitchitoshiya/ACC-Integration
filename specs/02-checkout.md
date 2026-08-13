# 02 — Checkout / Soft Lock

Status: Draft v1
Depends on: [00-overview.md](00-overview.md)

## Purpose

Reduce the chance of two people editing the same model in parallel and silently clobbering each other's work — without pretending to offer a hard lock the underlying platform can't actually enforce.

## Requirements

- **FR2.1**: User can "check out" a model, which sets a soft-lock (`checkedOutBy`, `checkedOutAt`) visible to all users of that model.
- **FR2.2**: A second user attempting to check out a locked model receives a warning showing who holds it and since when, but is **not hard-blocked** — this is a UX guardrail, not a security boundary, since the ACC Data Management API has no native file-locking primitive.
- **FR2.3**: Checkout auto-expires after a configurable idle period (default 24h) and is manually releasable by the checkout owner or an Admin.
- **FR2.4**: Checking in (uploading a new version, see [01-upload.md](01-upload.md)) by the checkout owner automatically releases the checkout.
- **FR2.5**: Checkout state is visible on the model's dashboard entry and surfaced inside the desktop plugin before a user starts editing.

## Flow

1. User clicks "Check Out" in plugin or dashboard.
2. Connector checks current `CheckoutState` for the model:
   - If free or expired → sets `checkedOutBy = user`, `checkedOutAt = now`, `expiresAt = now + 24h`.
   - If held by another active user → returns warning payload (holder identity, since when); user may proceed anyway (explicit override), which is logged.
3. On check-in (new upload by the checkout owner) or manual release, `CheckoutState` is cleared.
4. Background job sweeps expired checkouts every N minutes and clears them, logging an "auto-released" event.

## Edge Cases

- User overrides someone else's active checkout: allowed, but requires an explicit confirmation step and is logged as a distinct audit event (`checkout.override`) so it's visible in [07-notifications-audit.md](07-notifications-audit.md).
- Checkout owner uploads from a different tool/machine than they checked out from: allowed — checkout is per-user, not per-device.
- Two near-simultaneous checkout requests: connector resolves via DB-level optimistic locking on `CheckoutState`; first write wins, second gets the "already held" response.

## Acceptance Criteria

1. User A checks out a model; User B sees A's name and checkout time in both dashboard and plugin before attempting to edit.
2. Checkout auto-expires after the configured window and is reflected as "available" without manual intervention.
3. User A checking in a new version automatically clears their own checkout.
4. Override checkout by User B while A still holds it is possible but produces a distinct, queryable audit event.

## Open Questions

- Should checkout override require Reviewer/Admin approval rather than being self-service for any Modeler?
- Configurable expiry per project vs. global default — is per-project needed for v1?
