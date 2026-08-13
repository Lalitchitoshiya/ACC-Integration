# 05 — Version Comparison

Status: Draft v1 (Phase 3)
Depends on: [00-overview.md](00-overview.md), [01-upload.md](01-upload.md), [04-version-history.md](04-version-history.md), [13-metadata-schema.md](13-metadata-schema.md)

## Purpose

Let a reviewer or team member understand what changed between two versions of a model without opening both files in the native desktop tool.

## Requirements

- **FR6.1**: User can select two versions of the same model and get a metadata-level diff: element count deltas, extent changes, changed-attribute summary (best-effort, format-dependent).
- **FR6.2**: Full geometric/attribute diffing is out of scope for v1 where the source format has no accessible schema (documented per-format capability, see [06-cross-tool-exchange.md](06-cross-tool-exchange.md) capability matrix).

## Flow

1. User picks Version A and Version B (typically: current approved vs. a new `In Review` candidate) from the version history view.
2. Connector retrieves stored `metadata` for both (captured at upload time, per FR1.4).
3. Diff view renders: node count delta, link count delta, catchment count delta (if applicable), extent/bounding-box change, and — where the format parser supports it — a coarse list of added/removed/modified named elements.
4. If either version's metadata is missing (parse failure or unmanaged upload), comparison degrades gracefully: show what's available, flag what isn't, never fabricate a diff.

## Edge Cases

- Comparing versions from different source tools (e.g., an InfoWorks WS Pro version vs. a Civil 3D-derived version of the "same" model): only comparable if both went through a compatible metadata extractor; otherwise UI states "not directly comparable — different source formats."
- One of the two versions has a `parseError` (per [01-upload.md](01-upload.md)): comparison shows available side fully, other side marked "metadata unavailable."

## Acceptance Criteria

1. Comparing a version to itself returns zero deltas (sanity check on the diff logic).
2. Comparing two versions with known, hand-verified differences (test fixture) produces correct count deltas.
3. Comparison never silently omits a missing-metadata side — it's explicitly labeled, not left blank without explanation.

## Open Questions

- Is element-level (not just count-level) diffing a hard requirement for pilot reviewers, or is count/extent-level sufficient to unblock v1?
- Which format(s) get priority for deeper attribute-level diffing first — EPANET INP is the most likely starting point given FR7 overlap.
