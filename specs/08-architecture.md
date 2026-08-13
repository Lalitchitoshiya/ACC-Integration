# 08 — System Architecture

Status: Draft v1
Depends on: [00-overview.md](00-overview.md)

## Components

```
Desktop Tools                  Integration Layer                    ACC / APS
─────────────                  ─────────────────                    ─────────
InfoWorks WS Pro   ┐                                          ┌──► ACC Docs (Data Mgmt API)
InfoWorks ICM      ┤     Desktop Plugin/Add-in                │      - Folders, Files, Versions
InfoDrainage       ┼──►  (per-tool, thin) ──► Connector API ──┤
InfoWater Pro      ┤     - Upload/Download UI    (middleware) │◄──► ACC Reviews/Workflow API
Civil 3D           ┘     - Metadata capture       - Auth (APS OAuth2 3-legged)
                          - Local checkout state   - Metadata extraction service
                                                    - Format converters (INP/EXP/SWMM)
                                                    - Field-mapping config store
                                                    - Notification service
                                                    - Audit log store
                                                            │
                                                            ▼
                                                  Web Dashboard (React)
                                                  - Version history, review queue,
                                                    comparison view, audit export
```

1. **Desktop Plugin(s)** — thin per-tool add-ins (InfoWorks Open Data Import/Export API, InfoWater Pro SDK/COM interface, InfoDrainage SDK, Civil 3D .NET API) that call the Connector API. Keeps tool-specific logic out of the cloud layer.
2. **Connector/Middleware API** — the actual product. Owns auth (Autodesk Platform Services OAuth2), talks to ACC Data Management API + Reviews API, runs metadata extraction and format conversion, stores checkout state and audit log (own DB, since ACC doesn't model "checked out by hydraulic tool" natively).
3. **ACC Docs** — source of truth for file storage/versioning.
4. **Web Dashboard** — cross-team visibility (GIS/BIM/PM don't need the desktop tools installed).

## Non-Functional Requirements

- **Auth**: APS OAuth2 3-legged (user context) for all ACC calls; connector service never stores Autodesk credentials, only tokens.
- **File size**: support model packages up to at least 2 GB (large ICM models can be sizable).
- **Availability**: connector API should not be a single point of failure for read access — cache latest-approved metadata locally.
- **Traceability**: every state-changing action logged with actor, timestamp, before/after state — no exceptions.
- **Extensibility**: adding a new source tool = new plugin + config, not a core rewrite (plugin/connector boundary is the extension point).

## Related

- Data model backing these components: [09-data-model.md](09-data-model.md)
- Feature specs implemented by the Connector API: [01-upload.md](01-upload.md), [02-checkout.md](02-checkout.md), [03-review-workflow.md](03-review-workflow.md), [04-version-history.md](04-version-history.md), [05-comparison.md](05-comparison.md), [06-cross-tool-exchange.md](06-cross-tool-exchange.md), [07-notifications-audit.md](07-notifications-audit.md)
