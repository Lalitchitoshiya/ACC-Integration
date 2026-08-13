# 06 — Cross-Tool Data Exchange

Status: Draft v1 (Phase 5, limited scope)
Depends on: [00-overview.md](00-overview.md), [01-upload.md](01-upload.md)

## Purpose

Unblock GIS and hydraulic teams from manual re-entry by exchanging a defined common subset of network data between tools, via neutral interchange formats — without promising full semantic interoperability across every tool pair, which is explicitly out of scope (see [00-overview.md](00-overview.md#non-goals-v1)).

## Requirements

- **FR7.1**: System supports exporting a defined common subset (nodes, links/pipes, key hydraulic attributes: diameter, material, elevation, invert level) to a neutral interchange format.
- **FR7.2**: Neutral format candidates: EPANET `.inp` for water distribution (InfoWater Pro, InfoWorks WS Pro); SWMM `.inp` or ICM `.exp` for drainage/wastewater (InfoWorks ICM, InfoDrainage).
- **FR7.3**: GIS consumption path: exported model attributes mapped to ArcGIS Utility Network schema via a documented field-mapping table (delivered as config, not hardcoded).

## Tool Interop Capability Matrix

| Tool | Export API available | Neutral format | Notes |
|---|---|---|---|
| InfoWorks WS Pro | Open Data Import/Export API | EPANET INP | Good fit for FR7 |
| InfoWorks ICM | Open Data Import/Export API, `.exp` | SWMM INP / EXP | Drainage/wastewater subset |
| InfoDrainage | SDK export | SWMM-compatible subset | Confirm attribute coverage before committing to FR7 scope |
| InfoWater Pro | SDK/COM, EPANET-based core | EPANET INP | Likely best interop partner with WS Pro |
| Civil 3D | .NET API, LandXML | LandXML / IFC | Mainly for pipe network geometry, not hydraulic attributes |
| ArcGIS Utility Network | ArcGIS REST API, geodatabase | UN data model | Target schema for FR7.3 mapping |

**Action item before implementation:** validate actual field-level export coverage of InfoDrainage and InfoWorks ICM against SWMM INP — this determines how much of FR7 is real vs. aspirational for v1.

## Flow

1. User (or automated trigger on Approve, see [03-review-workflow.md](03-review-workflow.md)) requests export of a model version to a target neutral format.
2. Connector invokes the format-specific converter (source parser → common intermediate schema → target format writer).
3. Converted file is stored alongside the source version in ACC (as a derived/companion file, clearly labeled, not a new authoritative version) or made available for direct download by the target tool's plugin.
4. For GIS: connector applies `FieldMapping` config (sourceTool → ArcGIS UN schema) and pushes attributes via ArcGIS REST API, or produces a mapped export file if push isn't available.

## Edge Cases

- Source model has attributes with no mapping target defined: excluded from export, logged as "N unmapped fields" rather than dropped silently.
- Conversion fails partway (e.g., unsupported element type encountered): fail the whole export with a clear error — no partial/corrupt output files.
- Round-trip fidelity is not guaranteed: exporting A → neutral format → re-importing is explicitly out of scope for v1; this is one-directional publish, not sync.

## Acceptance Criteria

1. At least one export path (InfoWorks WS Pro → EPANET INP) is demonstrated end-to-end with a real test model, and the resulting INP file opens correctly in EPANET-compatible tooling.
2. Field-mapping config is externalized (not hardcoded) and can be updated without a code deploy.
3. Unmapped fields are reported, never silently discarded without a trace.

## Open Questions

- Is this one-directional export sufficient for the pilot, or do stakeholders expect bidirectional sync (explicitly flagged as much larger scope)?
- Who owns/maintains the field-mapping config over time as tool schemas evolve?
