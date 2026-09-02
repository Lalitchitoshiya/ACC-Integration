# Spec: Cloud-Based Water Network Model Collaboration & Data Exchange via Autodesk Construction Cloud (ACC)

Status: Draft v1
Owner: TBD
Last updated: 2026-08-11

This spec has been split into per-module documents under [specs/](specs/00-overview.md) for spec-driven development. Start with the overview, then the module you're implementing:

- [specs/00-overview.md](specs/00-overview.md) — problem statement, desired outcome, non-goals, roles
- [specs/01-upload.md](specs/01-upload.md) — upload a model version to ACC
- [specs/02-checkout.md](specs/02-checkout.md) — checkout / soft lock to avoid concurrent-edit collisions
- [specs/03-review-workflow.md](specs/03-review-workflow.md) — draft → in review → approved/rejected
- [specs/04-version-history.md](specs/04-version-history.md) — version list, latest-approved retrieval
- [specs/05-comparison.md](specs/05-comparison.md) — metadata-level diff between versions (Phase 2)
- [specs/06-cross-tool-exchange.md](specs/06-cross-tool-exchange.md) — neutral-format export across tools (Phase 3)
- [specs/07-notifications-audit.md](specs/07-notifications-audit.md) — event log, notifications, audit export
- [specs/08-architecture.md](specs/08-architecture.md) — components, integration layer, non-functional requirements
- [specs/09-data-model.md](specs/09-data-model.md) — connector service data model
- [specs/10-phasing.md](specs/10-phasing.md) — rollout sequence (Phase 1/2/3)
- [specs/11-api-contracts.md](specs/11-api-contracts.md) — concrete endpoints, request/response shapes
- [specs/12-permissions-errors.md](specs/12-permissions-errors.md) — role → action matrix, error response contract
- [specs/13-metadata-schema.md](specs/13-metadata-schema.md) — hydraulic model metadata schema (WS Pro, Phase 1)
- [specs/14-cad-visualization.md](specs/14-cad-visualization.md) — CAD visualization, two tracks: DXF (schematic, implemented) + IFC (exact property fidelity) → Autodesk Viewer (Phase 6)

Each module doc contains its own requirements (FR-numbered, unchanged from the original monolithic spec), flow, edge cases, acceptance criteria, and open questions.
