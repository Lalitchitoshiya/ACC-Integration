# 07 — Notifications & Audit

Status: Draft v1
Depends on: [00-overview.md](00-overview.md), [01-upload.md](01-upload.md), [02-checkout.md](02-checkout.md), [03-review-workflow.md](03-review-workflow.md)

**Phasing note** (see [10-phasing.md](10-phasing.md)): this module splits across phases. **Audit logging (FR8.2, FR8.3, the event table, CSV export) is Phase 2** — it's cheap (a row write alongside checkout/review actions already being built there) and is what actually delivers the "who changed what, when" outcome from the problem statement. **Notifications/subscriptions (FR8.1)** are **Phase 3** — with a single tool and small pilot team, checking the dashboard directly is an acceptable substitute for push alerts, and a notification service is real infrastructure not worth building before the core workflow is proven.

## Purpose

Give hydraulic, GIS, and BIM teams shared visibility into model activity regardless of which desktop tool they use, and provide a defensible record of who did what, when.

## Requirements

- **FR8.1** *(Phase 3)*: Users can subscribe to a model and get notified on new version upload and review state change.
- **FR8.2** *(Phase 2)*: Full audit log (who did what, when) exportable as CSV per project.
- **FR8.3** *(Phase 2, from FR5.4/FR2.2)*: Every state-changing action across upload, checkout, and review is logged with actor, timestamp, action, and before/after state where applicable.

## Event Types Logged

| Event | Source spec | Fields |
|---|---|---|
| `version.uploaded` | [01](01-upload.md) | model, version, uploadedBy, changeDescription |
| `checkout.acquired` | [02](02-checkout.md) | model, user, expiresAt |
| `checkout.released` | [02](02-checkout.md) | model, user, releasedBy (self/admin/auto-expiry) |
| `checkout.override` | [02](02-checkout.md) | model, previousHolder, newHolder |
| `review.submitted` | [03](03-review-workflow.md) | model, version, submittedBy |
| `review.approved` | [03](03-review-workflow.md) | model, version, reviewer, selfApproved (bool) |
| `review.rejected` | [03](03-review-workflow.md) | model, version, reviewer, comment |
| `export.completed` | [06](06-cross-tool-exchange.md) | model, version, targetFormat, unmappedFieldCount |

## Flow — Notifications

1. User subscribes to a Model (per-model, not project-wide, to avoid noise).
2. On any logged event affecting a subscribed model, connector's notification service sends an in-app + email notification to subscribers (excluding the actor themselves).
3. Reviewers are auto-subscribed to models they have Reviewer role on, for `review.submitted` events specifically.

## Flow — Audit Export

1. Admin/PM requests audit export for a project (optionally filtered by date range, model, or event type).
2. Connector queries the append-only event log, generates CSV: timestamp, event type, actor, model, version, details.
3. Export itself is logged as an event (`audit.exported`) for traceability of who pulled the record.

## Edge Cases

- High-frequency events (e.g., repeated checkout override attempts): rate-limit notifications per subscriber per hour to avoid spam, but never rate-limit the underlying audit log write.
- Deleted/deactivated user still appears in historical audit records: actor identity is preserved as a snapshot (name + id at time of action), not a live foreign-key that breaks on user deletion.

## Acceptance Criteria

1. Every event type in the table above is captured in the audit log with no gaps, verified against a scripted test sequence (upload → checkout → review → export).
2. Subscribed user receives a notification within an acceptable delay (define SLA, e.g., < 60s) of a relevant event.
3. CSV export contains a complete, correctly ordered record for a test project and matches the event count in the underlying log.
4. Audit log is append-only — no update/delete path exists in the API for existing entries.

## Open Questions

- Notification channels beyond in-app + email (Slack/Teams integration) — needed for pilot, or later?
- Retention period for audit log — indefinite, or does compliance dictate a specific window?
