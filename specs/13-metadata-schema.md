# 13 — Model Metadata Schema

Status: Draft v1
Depends on: [00-overview.md](00-overview.md), [01-upload.md](01-upload.md), [09-data-model.md](09-data-model.md), [05-comparison.md](05-comparison.md)

## Purpose

Replace the placeholder `metadata { nodeCount, linkCount, catchmentCount, extent }` in [09-data-model.md](09-data-model.md) with a real, hydraulic-domain-shaped schema. This is what [01-upload.md](01-upload.md) FR1.4 actually extracts and stores per Version, and what [05-comparison.md](05-comparison.md) diffs. Scoped to **Phase 1: InfoWorks WS Pro only** — a water distribution network model, not drainage/wastewater. Phase 4 (InfoWater Pro, InfoDrainage, ICM) will need their own schema variants; see Open Questions.

## Why a generic schema doesn't work

A hydraulic model isn't a flat file — it's a network graph with domain-specific element types and attributes that only make sense in context:

- A **node** in a water distribution model is one of: Junction, Reservoir, or Tank — each with different valid attributes (a Tank has capacity/level; a Junction doesn't).
- A **link** is one of: Pipe, Pump, or Valve — each with different hydraulic behavior (a Pipe has diameter/roughness/material; a Pump has a curve reference).
- Counts alone (`nodeCount: 4200`) tell a reviewer almost nothing useful — "4200 nodes" doesn't say whether the network grew, whether a critical valve was removed, or whether demand values changed. The schema needs to capture enough structure for FR6 (comparison) to be meaningful, without requiring full geometry (that stays in the native file).

## Schema

Captured once per Version at upload time (per [01-upload.md](01-upload.md) FR1.4), stored as `Version.metadata` (JSON):

```json
{
  "schemaVersion": "1.0",
  "sourceTool": "InfoWorksWSPro",
  "sourceToolVersion": "string",
  "modelType": "waterDistribution",
  "units": { "length": "m", "flow": "l/s", "pressure": "m" },
  "extent": {
    "crs": "EPSG:code",
    "minX": 0, "minY": 0, "maxX": 0, "maxY": 0
  },
  "network": {
    "nodes": {
      "total": 0,
      "byType": { "junction": 0, "reservoir": 0, "tank": 0 }
    },
    "links": {
      "total": 0,
      "byType": { "pipe": 0, "pump": 0, "valve": 0 }
    },
    "totalPipeLength": 0
  },
  "attributeSummary": {
    "pipe": {
      "diameterRange": { "min": 0, "max": 0 },
      "materials": { "PVC": 0, "DI": 0, "HDPE": 0, "unspecified": 0 },
      "missingMaterialCount": 0,
      "missingDiameterCount": 0
    },
    "junction": {
      "elevationRange": { "min": 0, "max": 0 },
      "totalBaseDemand": 0
    },
    "tank": {
      "count": 0,
      "totalCapacity": 0
    },
    "pump": { "count": 0 },
    "valve": {
      "count": 0,
      "byType": { "PRV": 0, "PSV": 0, "FCV": 0, "other": 0 }
    }
  },
  "namedElementIndex": {
    "note": "Phase 1: element IDs + type only, no full attribute set — enables added/removed detection in FR6 without full geometry diffing",
    "nodes": [ { "id": "string", "type": "junction|reservoir|tank" } ],
    "links": [ { "id": "string", "type": "pipe|pump|valve" } ]
  },
  "parseWarnings": []
}
```

## Field Notes

- **`schemaVersion`**: this schema itself is versioned independently of `Version` records — see Open Questions on migration.
- **`modelType`**: fixed to `"waterDistribution"` for WS Pro in Phase 1. Drainage/wastewater tools (Phase 4) will use a different `modelType` (`"drainage"` or `"wastewater"`) with a materially different `network`/`attributeSummary` shape (catchments, conduits, outfalls instead of junctions/pumps/valves) — not a variant of this schema, a sibling one.
- **`units`**: WS Pro models can be authored in different unit systems; capturing this prevents silent misinterpretation when comparing two versions or exporting to EPANET INP ([06-cross-tool-exchange.md](06-cross-tool-exchange.md)), which has its own unit conventions.
- **`network.byType` counts**: this is what makes count-level comparison in FR6.1 actually useful — "junctions +12, tanks +0, pumps -1" is a meaningful reviewer signal; a flat node count isn't.
- **`attributeSummary`**: aggregate stats (ranges, missing-value counts), not per-element values — this is the "coarse list" referenced in FR6.1/FR6.2 without committing to full attribute-level diffing in Phase 1.
- **`missingMaterialCount` / `missingDiameterCount`**: deliberately surfaced — incomplete pipe attribution is a common real-world data-quality issue in these models and a reviewer will want to see it trending, not just discover it later.
- **`namedElementIndex`**: minimal id+type list, not full attributes. This is what lets [05-comparison.md](05-comparison.md) report "added: J-4821, removed: P-1103" (added/removed elements) in Phase 3 without doing full geometric diffing, which stays out of scope per FR6.2.
- **`parseWarnings`**: non-fatal issues during extraction (e.g., an element with an unrecognized type code) — distinct from `Version.parseError`, which is reserved for total extraction failure.

## Extraction Source (WS Pro specifics)

- Extracted via InfoWorks Open Data Import/Export API against the uploaded export package — not by parsing the native `.wsz`/`.gdb` container directly, since the Import/Export API is the documented, stable interface.
- Extraction runs synchronously during upload for models under a size threshold (TBD, see Open Questions), async otherwise, mirroring the export job pattern in [11-api-contracts.md](11-api-contracts.md).
- On any extraction failure that prevents producing even the summary shape, `Version.metadata` is null and `Version.parseError` is set (per [09-data-model.md](09-data-model.md)) — the upload itself still succeeds, per FR1's "storage isn't blocked by metadata failure" principle.

## Acceptance Criteria

1. Uploading a known WS Pro test model produces a `metadata` object matching hand-verified counts for `network.nodes.byType`, `network.links.byType`, and `attributeSummary.pipe.materials`.
2. `namedElementIndex` for a test model round-trips correctly — every element ID present in the source model appears exactly once, with the correct type.
3. A model with intentionally missing pipe materials/diameters produces the correct `missingMaterialCount`/`missingDiameterCount`.
4. Extraction failure (corrupt export package) results in `metadata: null`, `parseError` set, and the version still uploads successfully (upload not blocked).

## Resolved (2026-08-13 spike, WS Pro 2026.3.1 / EPANET Net3)

- WS Pro table/field names validated against a real export via the Ruby-script table walk. Key mapping (WS Pro → this schema): `wn_node`→junction, `wn_reservoir`→**tank**, `wn_fixed_head`→**reservoir**, `wn_pipe`/`wn_pump`/`wn_valve`/`wn_float_valve`/`wn_non_return_valve`/`wn_pst`→links. Pipe attrs: `length`, `diameter`, `material`; node attrs: `x`, `y`, `z`/`ground_level`. Implemented in `connector/src/Connector.Api/Metadata/WsProMetadataExtractor.cs`.
- Verified against Net3: 92 junctions / 2 reservoirs / 3 tanks / 117 pipes / 2 pumps extracted correctly; missing-material detection works (117/117 — INP carries no material data).

## Open Questions
- Size threshold for sync vs. async metadata extraction — depends on how large real pilot models are.
- Schema versioning/migration strategy: if `schemaVersion` bumps, do older `Version.metadata` rows get backfilled, or does comparison ([05](05-comparison.md)) just handle cross-schema-version comparisons as "not directly comparable"?
- Phase 4 sibling schemas (drainage/wastewater, GIS) — should they share a common envelope (`schemaVersion`, `sourceTool`, `modelType`, `extent`, `units`) with a divergent `network`/`attributeSummary` body, or be fully independent documents? Recommend the shared-envelope approach for consistency with [11-api-contracts.md](11-api-contracts.md) response shapes, but not yet decided.
