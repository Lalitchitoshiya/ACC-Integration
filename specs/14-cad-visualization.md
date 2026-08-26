# 14 — CAD Visualization (DXF Export → Autodesk Viewer)

Status: Draft v1 (Phase 6)
Depends on: [00-overview.md](00-overview.md), [13-metadata-schema.md](13-metadata-schema.md)

## Purpose

Enable real, interactive visualization of hydraulic models inside Autodesk's actual Viewer (the same technology behind ACC's native DWG/RVT/IFC preview) — going beyond the static PNG companion image ([09-data-model.md](09-data-model.md) `PreviewImageUrn`) to a genuine CAD-format pipeline that Autodesk's Model Derivative service can translate into SVF and render.

## Format Decision: DXF, not DWG or IFC

This is the key design decision, and it drives everything else in this spec:

| Format | Verdict | Why |
|---|---|---|
| **DXF** | ✅ **Chosen** | Open, publicly documented ASCII/binary exchange format — Autodesk publishes the full spec. We can write it directly, no SDK, no licensing. Model Derivative accepts it as a translation input. |
| DWG | ❌ Rejected for v1 | Proprietary binary format. Writing a valid one essentially requires Autodesk's **RealDWG SDK** (licensed, non-trivial integration) or a third-party commercial library — real cost and complexity beyond this project's scope. |
| IFC | ❌ Rejected for v1 | Open format, but its STEP/EXPRESS-based schema is built for full 3D BIM semantics (walls, stories, systems) — far heavier than a 2D network graph needs. Revisit only if 3D/elevation-aware visualization becomes a real requirement. |

**DXF is the pragmatic target**: buildable entirely in-house, accepted by Model Derivative, and sufficient to represent a 2D network (points, lines, layers, labels).

## Requirements

- **FR14.1**: On upload, generate a DXF file representing the network — nodes as point/circle entities (layered by type: junction/tank/reservoir), links as LWPOLYLINE entities between their endpoint coordinates.
- **FR14.2**: Upload the DXF as a companion file alongside the version (same pattern as the PNG companion, [09-data-model.md](09-data-model.md)).
- **FR14.3**: Trigger a Model Derivative translation job for the uploaded DXF (submit job → poll status → completion).
- **FR14.4**: Store the resulting translation status and derivative URN on the Version record once translation completes.
- **FR14.5**: Dashboard embeds the Autodesk Viewer (APS Viewer JS SDK) for any version with a completed translation, using a short-lived, viewer-scoped access token (distinct from the Data Management token already in use).
- **FR14.6**: Translation failure never blocks the real upload (same best-effort principle as the PNG feature, [01-upload.md](01-upload.md) FR1.4 edge case) — surfaced as a status ("not available") rather than an error.
- **FR14.7**: Layers/colors in the DXF should mirror the existing PNG/dashboard color convention (junction/tank/reservoir/pipe) for visual consistency across all three visualization paths.

## Architecture / Flow

```
Upload completes (CSV/INP) → NetworkGraphExtractor (existing, reused)
        │
        ├─→ PNG companion (existing, unchanged)
        │
        └─→ NEW: DxfWriter.Render(graph) → .dxf bytes
                    │
                    ├─→ Upload DXF as companion file to ACC (same as PNG)
                    │
                    └─→ POST Model Derivative translation job (source: the DXF's URN)
                              │
                              ├─→ Async: poll GET manifest until status = success/failed
                              │
                              └─→ Store on ModelVersion:
                                    - CadPreviewUrn (the DXF's own ACC URN)
                                    - DerivativeUrn (translation job's output URN, used by the Viewer)
                                    - TranslationStatus (Pending | Success | Failed)
```

**Dashboard**: a new "🧊 3D View" button per version, shown only when `TranslationStatus = Success` — opens the embedded APS Viewer pointed at the derivative URN.

## Data Model Additions

Extends `ModelVersion` (per [09-data-model.md](09-data-model.md)):

```
CadPreviewUrn      string?   -- ACC URN of the generated DXF companion
DerivativeUrn       string?   -- Model Derivative's translated output reference
TranslationStatus   enum?     -- Pending | Success | Failed
TranslationError    string?   -- failure detail, if any
```

## New Infrastructure Needed

1. **`DxfWriter`** — new class, mirrors `PngMapRenderer`'s structure: takes a `NetworkGraph`, writes valid DXF entities (HEADER/TABLES/ENTITIES sections, LWPOLYLINE for pipes, POINT or block-inserted symbols for nodes, one layer per element type).
2. **Model Derivative client** — new methods on `ApsAccClient` (or a sibling class): submit translation job, poll manifest, extract the viewable derivative URN.
3. **Viewer-scoped token endpoint** — the APS Viewer SDK needs a token with `viewables:read` scope; likely reuse the existing `ApsTokenService` with an additional scope profile.
4. **Background polling** — translation jobs are async and can take time; needs either a background service (like the existing `CheckoutExpirySweeper`) or on-demand polling triggered when the dashboard requests viewer status.
5. **Dashboard**: embed the APS Viewer JS library (`<script>` include — note this is an *external* script, a deliberate exception to the dashboard's current zero-dependency design).

## Edge Cases

- Very large networks (1000+ nodes) — DXF file size and translation time both scale; may need a size warning or a node-count ceiling for v1.
- Translation job stuck/timeout — define a max wait/poll duration before marking `TranslationStatus = Failed` with a clear message, never hangs indefinitely.
- Units — DXF has no inherent unit system; must document assumed units (meters, matching our existing metadata schema) so scale isn't ambiguous.
- Coordinate system — our data uses arbitrary local plan coordinates (not geo-referenced); DXF entities will use these directly, consistent with existing PNG/SVG behavior.

## Acceptance Criteria

1. A real network (e.g. Net3) produces a valid DXF that opens correctly in any standard DXF-compatible viewer (e.g. LibreCAD, or AutoCAD if available) as a sanity check, before relying on Model Derivative.
2. The DXF uploads to ACC as a companion file, same as the PNG.
3. A submitted translation job reaches `Success` status for a real test network, and the resulting derivative URN successfully loads in the APS Viewer (embedded in the dashboard).
4. A translation failure (simulated) results in a clear "not available" state, never an error that blocks the underlying model upload.

## Open Questions

- Node symbols: plain DXF POINT entities, or proper block-inserted symbols (circle/square/triangle blocks) for better visual fidelity in the CAD viewer? Blocks are more work but look right.
- Should pipe labels (diameter, material) be included as DXF TEXT entities, or kept purely geometric for v1?
- Polling strategy: synchronous wait during upload (simple, but slows the upload response) vs. async background job + dashboard polls status separately (better UX, more moving parts)?
