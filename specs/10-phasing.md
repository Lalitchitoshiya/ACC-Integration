# 10 — Phasing

Status: Draft v1
Depends on: [00-overview.md](00-overview.md)

Six phases, single-tool-first: prove core version control + workflow on InfoWorks WS Pro alone before adding tools, breadth, or visualization depth.

## Phase 1 — Foundation (ACC wiring + upload + history)

Tool scope: **InfoWorks WS Pro only**

Modules: [01-upload.md](01-upload.md), [04-version-history.md](04-version-history.md) (upload + list + latest-approved retrieval, though "approved" is meaningless until Phase 2's review workflow exists — treat "latest" as "latest uploaded" in this phase), [08-architecture.md](08-architecture.md), [09-data-model.md](09-data-model.md), [11-api-contracts.md](11-api-contracts.md), [12-permissions-errors.md](12-permissions-errors.md), [13-metadata-schema.md](13-metadata-schema.md)

- APS OAuth2 auth wired end-to-end
- Connector service + DB stood up (Project/User/Model/Version tables)
- InfoWorks WS Pro plugin: upload to ACC, list versions, download any version
- Proves the most basic pain point from the problem statement: "which file is the latest, where is it" — before touching collaboration mechanics

## Phase 2 — Collaboration Controls (checkout + review + audit)

Tool scope: InfoWorks WS Pro only

Modules: [02-checkout.md](02-checkout.md), [03-review-workflow.md](03-review-workflow.md), audit logging only from [07-notifications-audit.md](07-notifications-audit.md) (FR8.2/FR8.3)

- Soft-lock checkout to reduce concurrent-edit collisions
- Draft → In Review → Approved/Rejected workflow; `currentApprovedVersionId` becomes meaningful
- Audit log capturing every state change (upload/checkout/review) — cheap to add here since it's a row write alongside actions already built, and it's what actually delivers "no visibility into who changed what" from the problem statement
- **This is the MVP** — Phases 1+2 together are the smallest slice that demonstrates the core value proposition end-to-end on one tool

## Phase 3 — Visibility & Comparison

Tool scope: InfoWorks WS Pro only

Modules: notifications/subscriptions from [07-notifications-audit.md](07-notifications-audit.md) (FR8.1), [05-comparison.md](05-comparison.md)

- Push notifications/subscriptions — deferred this far because checking the dashboard is an acceptable substitute until team size/volume makes polling annoying
- Metadata-level version comparison — deferred behind Phase 2 because it needs a stable review workflow (comparing "candidate vs. current approved" is the primary use case) to be meaningful

## Phase 4 — Multi-Tool Expansion

Tool scope: add InfoWater Pro, InfoDrainage, InfoWorks ICM (Civil 3D if still in scope)

- New desktop plugins per tool, reusing the Phase 1-3 connector API unchanged (plugin/connector boundary is the extension point, per [08-architecture.md](08-architecture.md))
- Per-tool metadata parsers (node/link/catchment extraction) added to support [01-upload.md](01-upload.md) FR1.4 and [05-comparison.md](05-comparison.md) for each new format
- No new workflow concepts — this phase is breadth, not new mechanics

## Phase 5 — Cross-Tool Exchange

Modules: [06-cross-tool-exchange.md](06-cross-tool-exchange.md), ArcGIS Utility Network field mapping

- Neutral-format export (EPANET INP, SWMM INP/EXP) between tools
- GIS field-mapping to ArcGIS Utility Network
- Explicitly the highest-risk, most-aspirational phase — see the Tool Interop Capability Matrix in [06-cross-tool-exchange.md](06-cross-tool-exchange.md); do not commit scope here until export coverage is validated per-tool

## Phase 6 — CAD Visualization (two tracks: DXF + IFC)

Modules: [14-cad-visualization.md](14-cad-visualization.md)

- **Track A — DXF** *(implemented)*: network exported as a DXF companion file, Model Derivative translation (DXF → SVF2), embedded Autodesk Viewer in the dashboard. Real attribute values shown as drawn TEXT labels — the format's ceiling, since DXF is a drawing format and custom data (XDATA) was empirically confirmed not to survive translation.
- **Track B — IFC** *(approved, spike-gated)*: network exported as semantic BIM objects (`IfcPipeSegment` etc.) with property sets, so the Viewer's properties panel shows **named values exactly matching InfoWorks WS Pro's property panel** — the proper fix for property fidelity, not a labeling workaround. A mandatory minimal spike (FR14.8) verifies custom Pset visibility before the full converter is built.
- DWG remains rejected (needs Autodesk's licensed RealDWG SDK) — see the format decision table in [14-cad-visualization.md](14-cad-visualization.md).
- Independent of Phase 4/5 — doesn't require additional tools or cross-tool exchange, just richer visualization paths for whatever's already in ACC.

## What's explicitly out of Phase 1–2 (MVP)

- Any tool other than InfoWorks WS Pro
- Notifications and comparison (Phase 3)
- Cross-tool exchange (Phase 5) — inherently meaningless with one tool anyway
- Interactive CAD visualization (Phase 6) — the PNG companion image (built in Phase 1/2 timeframe) covers the MVP's visualization need; DXF/Viewer integration is a deliberate later enhancement, not required for the core collaboration workflow

## Related

- Architecture these phases build against: [08-architecture.md](08-architecture.md)
- Acceptance criteria per module live in each module spec, not here — this doc sequences, it doesn't define done-ness.
