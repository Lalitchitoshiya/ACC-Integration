# ACC Water Connector — 8-Day Development Workflow

How the project went from problem statement to working product: cloud-based water
network model collaboration between InfoWorks WS Pro and Autodesk Construction
Cloud (ACC). Each day lists the goal, what was produced, and the proof point.

---

## Day 1 — Problem Definition & Spec-Driven Foundation

**Goal:** Turn the problem statement into implementable specifications.

- Analyzed the collaboration pain points: divergent model copies, no authoritative
  version, manual sharing, silent overwrites, no audit trail, no ACC integration.
- Wrote the full spec suite ([specs/](../specs/00-overview.md)) — 14 documents:
  overview, upload, checkout, review workflow, version history, comparison,
  cross-tool exchange, notifications/audit, architecture, data model, phasing,
  API contracts, permissions/error model, hydraulic metadata schema.
- Defined 5-phase delivery plan; Phase 1+2 = MVP, scoped to InfoWorks WS Pro only.

**Deliverable:** Complete spec suite with FR-numbered requirements, acceptance
criteria, and explicit non-goals.

## Day 2 — Tech Stack & Architecture

**Goal:** Choose the stack deliberately and scaffold the solution.

- Evaluated Node.js/TypeScript vs C#/.NET vs Python against APS SDK support,
  team fit, and the Windows/desktop-tool ecosystem; researched what existing
  ACC integrations use. Decision: **ASP.NET Core (.NET 10) + EF Core + PostgreSQL**.
- Scaffolded solution, entities per the data model spec (Project, User,
  ProjectMembership, Model, Version, ReviewEvent, CheckoutState, AuditEvent),
  JSONB metadata column, optimistic-concurrency tokens.

**Deliverable:** Building solution with schema matching [specs/09](../specs/09-data-model.md).

## Day 3 — Phase 1 Connector API

**Goal:** Core version-control API, testable without any Autodesk dependency.

- Endpoints per [specs/11](../specs/11-api-contracts.md): model registration,
  multipart version upload, paginated history, latest-approved with flagged
  fallback, download; single error contract and per-role checks per
  [specs/12](../specs/12-permissions-errors.md).
- Mock ACC client (local storage, `mock:` URNs) to decouple development from
  credentials; audit events on every state change; dev seed with 4 role users.

**Proof:** End-to-end smoke test — upload as Modeler, 403 as Viewer,
400 on short change description, latest-approved fallback labeled `Draft`.

## Day 4 — Autodesk Cloud Integration (APS + OSS)

**Goal:** Replace the mock with the real Autodesk cloud.

- APS app registration; secrets in .NET user-secrets (never committed).
- 2-legged OAuth token service with caching; scope debugging (AUTH-001/AUTH-010).
- Real storage client: OSS bucket auto-provisioning, signed-S3 upload flow,
  signed download URLs; fixed double-encoded object-key bug.

**Proof:** File uploaded through the connector, listed back directly from
Autodesk's OSS API, downloaded byte-identical via signed URL.

## Day 5 — ACC Docs Integration (Real Project, India Region)

**Goal:** Files visible in the actual ACC Docs UI, not just raw cloud storage.

- 3-legged OAuth (user login) with persisted refresh token across restarts.
- Discovered hub/project/folder URNs via Data Management API (region IND).
- Rewrote the ACC client to the full Docs flow: project storage → S3 upload →
  Docs item (first upload) / version N+1 (subsequent) — ACC's native version
  stack, never overwriting (FR1.2). Fixed the DM API's charset-sensitive 415.

**Proof:** `zoneA_network.csv` visible in the ACC project's Project Files with
2 versions; download round-trip returns exact content.

## Day 6 — InfoWorks WS Pro Plugin (Upload) + Real Metadata

**Goal:** Publish to ACC from inside WS Pro; extract real hydraulic metadata.

- `upload_to_acc.rb`: exports every table of the open network to sectioned CSV,
  prompts for change description (with fallbacks for dialog-less builds),
  multipart-uploads to the connector — pure stdlib Ruby, no gems.
- First real run in WS Pro 2026.3.1 against EPANET Net3 → SUCCESS.
- Used the real export to close the metadata-schema spike: mapped WS Pro's
  `wn_*` tables (their "reservoir" = tank, "fixed head" = EPANET reservoir) and
  replaced the placeholder extractor with a real parser.

**Proof:** Net3 metadata extracted exactly right — 92 junctions, 2 reservoirs,
3 tanks, 117 pipes, 2 pumps, 65.7 km pipe, extents/elevations, and
117/117 pipes flagged missing material (correct: INP has no material data).

## Day 7 — Round-Trip & Multi-Project Support

**Goal:** Complete the loop back into WS Pro; scale beyond one model.

- `download_from_acc.rb`: fetches latest **approved** version (falls back to
  latest draft with an explicit warning — FR3.2 honored in the desktop tool),
  shows provenance + network stats before the file is even opened.
- `open_from_acc.rb`: downloads and writes rows directly into an empty open
  network via the Exchange API (transactional, refuses non-empty networks).
- `GET /projects` endpoint + name-based model selection in scripts
  (`MODEL_NAME = 'My INP Network'`), with list-when-blank discovery.

**Proof:** Download script run inside WS Pro pulled v2 from ACC with full
provenance printout.

## Day 8 — Phase 2 Governance + Web Dashboard

**Goal:** The collaboration controls that make ACC storage a workflow.

- **Checkout/soft-lock** ([specs/02](../specs/02-checkout.md)): acquire with
  holder-identity 409, audited override, admin release, auto-release on
  check-in, background expiry sweep.
- **Review workflow** ([specs/03](../specs/03-review-workflow.md)):
  Draft → InReview → Approved/Rejected; API-level role enforcement;
  comment-required rejection; first-reviewer-wins concurrency; approval flips
  `currentApprovedVersionId` — the download script's warning became
  "Latest APPROVED version found."
- **Audit** ([specs/07](../specs/07-notifications-audit.md)): filtered query,
  Admin-only CSV export (itself audited), download events logged.
- **Web dashboard** served at `/`: model selector, version history with status
  badges + metadata chips + ⭐ approved marker, checkout banner with override
  flow, submit/approve/reject actions, review history, audit trail with CSV
  export, and a role switcher demoing all four roles.

**Proof:** Full manual test matrix passed — state machine (422), roles (403),
comment validation (400), approval flipping latest-approved, audit CSV with
every event since Day 3.

---

## End State

```
InfoWorks WS Pro ──upload_to_acc.rb──►  Connector API  ──►  ACC Docs (IND region)
      ▲                                (roles, review,          │  native version stack
      └──open/download_from_acc.rb──   audit, metadata)  ◄──────┘  visible in ACC UI
                                            ▲
                                    Web Dashboard (/)
                              4 roles · review queue · audit
```

**Solved from the original problem statement:** single source of truth in ACC ✓ ·
authoritative "latest approved" ✓ · no manual sharing ✓ · overwrite protection
(checkout + versioning) ✓ · who-changed-what visibility ✓ · version history &
approval workflow ✓ · ACC integration ✓

**Known remaining work** (tracked, deliberate): real per-user APS login (dev
header auth is demo-only), `open_from_acc.rb` first-run verification, version
comparison UI ([specs/05](../specs/05-comparison.md)), notifications, additional
tools (InfoWater Pro, InfoDrainage, ICM — Phase 4), cross-tool exchange (Phase 5).
