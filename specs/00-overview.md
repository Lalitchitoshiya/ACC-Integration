# 00 — Overview

Status: Draft v1
Last updated: 2026-08-11

See also: [01-upload.md](01-upload.md) · [02-checkout.md](02-checkout.md) · [03-review-workflow.md](03-review-workflow.md) · [04-version-history.md](04-version-history.md) · [05-comparison.md](05-comparison.md) · [06-cross-tool-exchange.md](06-cross-tool-exchange.md) · [07-notifications-audit.md](07-notifications-audit.md) · [08-architecture.md](08-architecture.md) · [09-data-model.md](09-data-model.md) · [10-phasing.md](10-phasing.md)

## Problem Statement

Water infrastructure teams use InfoWorks WS Pro, InfoWorks ICM, InfoDrainage, InfoWater Pro, Civil 3D, and ArcGIS Utility Network to build and maintain hydraulic network models. These models live on local machines or shared drives. This causes:

- Multiple divergent copies of the same model
- No reliable way to identify the latest/authoritative version
- Manual sharing via email/network folders
- Silent overwrites of other users' changes
- No audit trail of who changed what, when
- No centralized version history or approval gate
- Poor interoperability between hydraulic, GIS, and BIM teams
- No integration with Autodesk Construction Cloud, where the rest of the project's BIM/civil data already lives

## Desired Outcome

Deliver a cloud collaboration layer, built on ACC, that lets distributed teams treat ACC Docs as the single source of truth for hydraulic network models, while continuing to work in their native desktop tools. See individual module specs for detailed requirements; summary of capabilities:

1. Upload a model from native tools to ACC ([01](01-upload.md))
2. Discover/download the latest approved version ([03](03-review-workflow.md), [04](04-version-history.md))
3. Checkout, edit locally, check back in as a new version without silent overwrites ([02](02-checkout.md))
4. Full version history with author/timestamp/description/status ([04](04-version-history.md))
5. Review/approve/reject workflow before a version becomes authoritative ([03](03-review-workflow.md))
6. Metadata-level comparison between versions ([05](05-comparison.md))
7. Limited cross-tool data exchange via neutral formats ([06](06-cross-tool-exchange.md))
8. Shared visibility for hydraulic, GIS, and BIM teams regardless of tool ([07](07-notifications-audit.md))

## Non-Goals (v1)

- Full semantic/schema-level interoperability across every tool pair is **not** in scope for v1 — see [06-cross-tool-exchange.md](06-cross-tool-exchange.md) for the defined subset.
- No real-time multi-user simultaneous editing inside a single model file — collaboration model is checkout/checkin, not live co-editing.
- No cloud-hosted hydraulic simulation/compute — this is a data/version/workflow layer, not a simulation engine replacement.

## User Roles

| Role | Permissions |
|---|---|
| Modeler | Upload new versions, check out/in, view history |
| Reviewer/Approver | All Modeler permissions + approve/reject submitted versions, comment |
| Viewer (GIS/BIM/PM) | Download latest approved version, view history and metadata, no upload |
| Admin | Manage project folder structure, roles, retention policy |

## Related Specs

- Architecture, components, non-functional requirements: [08-architecture.md](08-architecture.md)
- Data model: [09-data-model.md](09-data-model.md)
- Phasing / rollout sequence: [10-phasing.md](10-phasing.md)
- API contracts (endpoints, request/response shapes): [11-api-contracts.md](11-api-contracts.md)
- Permissions matrix & error handling conventions: [12-permissions-errors.md](12-permissions-errors.md)
- Model metadata schema (hydraulic network structure captured per version): [13-metadata-schema.md](13-metadata-schema.md)
- CAD visualization (Phase 6, two tracks — DXF schematic + IFC property-exact → Autodesk Viewer): [14-cad-visualization.md](14-cad-visualization.md)

## Open Questions

- Which ACC project(s)/hub will host the pilot — existing customer project or a new sandbox?
- Confirmed list of APIs available per tool version in use (license tier may gate SDK/API access, especially InfoDrainage and InfoWater Pro).
- Retention/versioning limits — does ACC's version history need supplementing with our own archive for compliance?
- Who holds Reviewer/Approver role in the pilot — is this a named person per project or a role-based queue?
