# 14 — CAD Visualization (Phase 6: DXF + IFC → Autodesk Viewer)

Status: Draft v2 (Phase 6 — Track A: DXF **implemented**; Track B: IFC **approved, pending spike**)
Depends on: [00-overview.md](00-overview.md), [13-metadata-schema.md](13-metadata-schema.md)

## Purpose

Enable real, interactive visualization of hydraulic models inside Autodesk's actual Viewer (the same technology behind ACC's native DWG/RVT/IFC preview) — going beyond the static PNG companion image ([09-data-model.md](09-data-model.md) `PreviewImageUrn`) to genuine CAD-format pipelines that Autodesk's Model Derivative service can translate into SVF and render.

Phase 6 has **two tracks** sharing one pipeline (companion file → translation job → embedded Viewer):

- **Track A — DXF** *(implemented)*: fast schematic 2D visualization. Shape and topology are correct; real attribute values are shown as drawn TEXT labels — a workaround, because the format cannot carry structured properties (see Empirical Findings).
- **Track B — IFC** *(this revision)*: exact property fidelity. Pipes/nodes become semantic BIM objects whose **named properties in the Viewer's panel match InfoWorks WS Pro's property panel exactly** — the real fix for the class of problem Track A can only label around.

## Format Decision (revised)

| Format | Verdict | Why |
|---|---|---|
| **DXF** | ✅ Track A — implemented | Open, publicly documented format; written in-house (~110 lines), accepted by Model Derivative. Limitation confirmed empirically: it is a *drawing* format — the Viewer only extracts auto-computed geometry (schematic line length, symbol radius), never our data. |
| **IFC** | ✅ Track B — approved (was: rejected) | Originally deferred as heavier than a 2D graph needs. **Revisited and approved** after Track A's property-fidelity limits were confirmed empirically: IFC is a *data* format — `IfcPipeSegment` etc. are semantic objects with property sets that Model Derivative extracts as named Viewer properties ("IFC Attributes" / Pset groups, per Autodesk's IFC v4 pipeline). The extra schema weight is the price of exact property match, which is now a stated requirement. |
| DWG | ❌ Still rejected | Proprietary binary; writing one requires the licensed RealDWG SDK or a commercial library. |

## Empirical Findings (from Track A — these motivate Track B)

Recorded because they were verified against live Autodesk services, not assumed:

1. **Auto-computed properties are schematic, not real.** The Viewer shows a LINE's drawing-space length (e.g. `20`) — EPANET/WS Pro plan coordinates are layout positions, not to scale, so this number is meaningless (real pipe: `1609.34 m`). It cannot be suppressed or overridden.
2. **XDATA does not survive translation.** Extended entity data attached to DXF entities is silently dropped — the DXF translator only extracts `General` / `3D Visualization` / `Geometry` categories. Confirmed by querying the properties endpoint of a real translated file.
3. **TEXT entities do survive** (they are geometry) — hence Track A's visible labels (`11 L=1609.3m D=355.6mm`), which match WS Pro exactly after unit conversion.
4. **Unit conversion is mandatory for INP sources.** EPANET's `[OPTIONS] Units` implicitly selects US Customary (length ft, diameter in) vs SI; values must be converted (ft→m, in→mm) before display or they silently disagree with WS Pro. Implemented in `NetworkGraphExtractor`.

## Requirements — Track A: DXF (implemented)

- **FR14.1**: On upload, generate a DXF (R12) representing the network — nodes as CIRCLE entities layered by type (junction/tank/reservoir), links as LINE entities between endpoint coordinates.
- **FR14.2**: Upload the DXF as a companion file alongside the version (same pattern as the PNG companion).
- **FR14.3**: Trigger a Model Derivative translation job for the uploaded DXF (submit job → poll status → completion).
- **FR14.4**: Store translation status and derivative URN on the Version record.
- **FR14.5**: Dashboard embeds the Autodesk Viewer (APS Viewer JS SDK) for any version with a completed translation, via a short-lived viewer token endpoint.
- **FR14.6**: Generation/translation failure never blocks the real upload — surfaced as a status, not an error.
- **FR14.7**: Layers/colors mirror the PNG/dashboard color convention (junction/tank/reservoir/pipe).
- **FR14.7b** *(added during implementation)*: real attribute values (link id, Asset ID, length, diameter, material; node id, elevation) drawn as TEXT label entities — unit-converted per Finding 4 — since structured properties are impossible in this format (Findings 1–2).

## Requirements — Track B: IFC (new)

### The gate: spike before build

- **FR14.8 (SPIKE — mandatory first step)**: Before building the full converter, produce a **minimal hand-assembled IFC4 file** (2–3 `IfcPipeSegment`s with one standard Pset and one custom Pset), upload → translate → **query the properties endpoint and open the Viewer** to verify: (a) translation succeeds, (b) standard attributes appear as named properties, (c) **custom Pset values appear** (the one unverified risk — same category of surprise as XDATA was). Full Track B work proceeds only if (a)–(c) pass; if custom Psets fail, fall back to encoding values in standard attributes/`Description` fields and record the finding here.

  **✅ SPIKE PASSED (2026-08-27)** — all three conditions verified against the live properties endpoint. `Pset_ACCWaterHydraulics` appears as its own named property group with all values (Length "1609.340 m", ElementId, AssetId, Material "Ductile Iron"); `Pset_PipeSegmentTypeCommon.NominalDiameter` also surfaces. Three findings that bind the converter design:
  1. **`conversionMethod: "v4"` is mandatory in the translation job** (`output.formats[].advanced`). The default routed to the legacy Navisworks IFC loader, which produced an *empty* model (zero objects/properties) from the same valid file — with overall status "success" and only a `Navisworks-EmptyFile` warning. The modern pipeline (`IFC Loader: 4`) processed everything. `ModelDerivativeClient` MUST pass this option for IFC jobs.
  2. **Per-property units matter**: a diameter written as `355.6` with the project length unit METRE displays as "355.600 m" — semantically wrong (real value is mm). The converter must either declare a millimetre unit on diameter properties (`IfcPropertySingleValue.Unit` → conversion-based mm unit) or write all length-measures in metres.
  3. Boilerplate that made the file load where the first attempt produced an empty model: full `IfcOwnerHistory`, an `IfcBuilding` level in the spatial tree, `IfcGeometricRepresentationSubContext` ('Body'), and plain `IfcExtrudedAreaSolid` cylinders ('SweptSolid') instead of `IfcSweptDiskSolid`.

### The converter

- **FR14.9**: `IfcWriter` generates a valid **IFC4** file (STEP physical file format) with the minimal required hierarchy: one `IfcProject` (with units + representation context) → `IfcSite` → elements attached via `IfcRelContainedInSpatialStructure` / `IfcRelAggregates`.
- **FR14.10 — element mapping**:

  | Network element | IFC entity |
  |---|---|
  | pipe | `IfcPipeSegment` |
  | pump | `IfcPump` |
  | valve (incl. PST/float/NRV) | `IfcValve` |
  | tank | `IfcTank` |
  | junction | `IfcFlowFitting` |
  | reservoir (fixed head) | `IfcTank` with distinguishing predefined type/Pset value *(open question below)* |

- **FR14.11 — properties (the whole point)**: every element carries its real attributes via `IfcRelDefinesByProperties`:
  - Standard properties where IFC defines them (e.g. `Pset_PipeSegmentTypeCommon.NominalDiameter`).
  - A custom Pset (working name `Pset_ACCWaterHydraulics`) for the rest: Length, Diameter, Material, ElementId, AssetId, Elevation, SourceTool. Values reuse the already-unit-converted `NetworkGraph` data (metres / mm), declared consistently with FR14.12.
  - Acceptance is **value equality with WS Pro's property panel** (e.g. `1609.34 m`, `355.6 mm`) — not approximate, not re-derived from geometry.
- **FR14.12 — units**: explicit `IfcSIUnit` declarations (METRE for length; derived mm where applicable). Never rely on defaults — Autodesk's own IFC FAQ flags unit handling as a common failure mode.
- **FR14.13 — geometry**: v1 uses simple, honest solids: pipes as `IfcSweptDiskSolid` along the schematic axis (radius from **real** diameter, uniformly scaled to stay visible against schematic coordinates), nodes as small primitive solids. Node `Elevation` MAY drive the Z coordinate to give a true 3D profile — decide during the spike; a flat Z=0 model is an acceptable v1 fallback.
- **FR14.14 — pipeline parity**: the IFC is a second companion file with its own translation job and status, tracked in parallel with Track A's (see Data Model). Failure of either track never blocks the upload or the other track.
- **FR14.15 — dashboard**: when both derivatives exist, the 3D View button prefers **IFC** (property-correct) and labels the DXF variant as "schematic" — or offers both; final UX decided at implementation.

## Architecture / Flow (both tracks)

```
Upload completes (CSV/INP) → NetworkGraphExtractor (shared: geometry + real attributes, unit-converted)
        │
        ├─→ PNG companion (unchanged)
        ├─→ Track A: DxfWriter → .dxf  ─┐
        └─→ Track B: IfcWriter → .ifc  ─┤→ upload companion to ACC → Model Derivative job
                                         │→ poll manifest → store URN + status per track
                                         └→ Dashboard: 3D View via embedded APS Viewer
```

## Data Model Additions

Implemented for Track A; Track B adds a parallel set:

```
-- Track A (existing)
CadPreviewUrn       string?   -- ACC URN of the DXF companion
DerivativeUrn       string?   -- its translation output URN
TranslationStatus   enum?     -- Pending | Success | Failed
TranslationError    string?

-- Track B (new)
IfcPreviewUrn          string?
IfcDerivativeUrn       string?
IfcTranslationStatus   enum?    -- same enum
IfcTranslationError    string?
```

## New Infrastructure (Track B increments)

1. **`IfcWriter`** — the substantial piece: STEP serialization, entity graph with the mandatory relationships, Psets. Written in-house like `DxfWriter` (no external IFC library dependency planned for v1; revisit if hand-writing proves error-prone during the spike).
2. **Reuse as-is**: `ModelDerivativeClient` (IFC is just another input format for the same job/manifest endpoints), viewer token endpoint, on-demand status polling, embedded Viewer.
3. Dashboard: second status chip + button variant per FR14.15.

## Edge Cases

- Very large networks — IFC files are far more verbose than DXF (~10× typical); expect slower translation on ky10-class networks; same size-warning consideration as Track A.
- Translation job stuck/timeout — same max-poll policy as Track A.
- IFC strictness — an invalid entity graph fails translation outright (unlike DXF's tolerance). The spike (FR14.8) plus a syntax sanity check on a reference viewer/validator before upload mitigates blind-debugging cycles.
- Schematic coordinates + real diameters — a 355 mm pipe drawn on a coordinate span of ~13 units would be invisible or monstrous; FR14.13's uniform visual scale factor addresses this; the *property* values are never scaled.
- Elements missing attributes (e.g. INP has no material) — omit the property rather than writing empty/zero values, consistent with the metadata schema's missing-data philosophy.

## Acceptance Criteria

Track A (met): valid DXF from real networks (Net1 INP, Net3 WS Pro CSV); translation Success; labels match WS Pro values exactly after unit conversion; failures never block uploads.

Track B:
1. **Spike gate (FR14.8) passes** — documented evidence (properties endpoint output) that custom Pset values surface in the Viewer.
2. Net1 (INP) and Net3 (WS Pro CSV) both convert, translate to Success, and load in the embedded Viewer.
3. Clicking a pipe in the Viewer shows named properties whose values **equal WS Pro's panel exactly** — the `1609.34 m` / `355.6 mm` test case is the canonical check.
4. A deliberately broken IFC (simulated failure) yields status Failed with the error captured — upload and Track A unaffected.

## Open Questions

- Reservoir mapping: `IfcTank` with a predefined type vs `IfcDistributionChamberElement` — decide during the spike based on how each renders/labels in the Viewer.
- Use node elevation as Z for a true 3D profile in v1, or stay flat? (FR14.13 — spike decides.)
- Once IFC proves out, does Track A (DXF) remain worth generating per upload, or become opt-in? Two companions + two translations per upload doubles the pipeline cost.
- IFC4 confirmed as the target (Autodesk's modern pipeline); is an IFC2x3 fallback ever needed for older consumers? Assumed no for v1.

## Resolved (Track A implementation, 2026-08-25/27)

- Polling strategy → on-demand polling from the dashboard (`/translation-status`), no background worker.
- Node symbols → plain circles with layer colors, sufficient for schematic purposes.
- Pipe labels → yes, TEXT entities; became FR14.7b and the bridge motivating Track B.
