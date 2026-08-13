# InfoWorks WS Pro → ACC Upload Script

Ruby script run inside WS Pro that exports the open network and uploads it to the
Connector API, which versions it into ACC Docs. This is the Phase 1 desktop-plugin
per [specs/08-architecture.md](../../specs/08-architecture.md) — Ruby-script form
first; IExchange (headless) automation deferred by decision.

## Prerequisites

1. Connector API running (see [connector/README.md](../../connector/README.md)):
   `dotnet run --project src/Connector.Api --urls http://localhost:5000`
2. APS user authorization done once: visit `http://localhost:5000/api/auth/login`.
3. A Model registered in the connector whose `accFolderUrn` points at your ACC
   project folder (create via `POST /api/v1/projects/{projectId}/models` as Admin).

## Setup (once)

Edit the CONFIG block at the top of `upload_to_acc.rb`:

| Setting | Meaning |
|---|---|
| `CONNECTOR_URL` | Connector API base URL (default `http://localhost:5000`) |
| `MODEL_ID` | The connector Model id this network belongs to |
| `USER_EMAIL` | Your connector user (dev header auth, until APS user login lands) |

## Use (every upload)

1. Open your network in InfoWorks WS Pro.
2. **Network menu → Run Ruby Script…** → select `upload_to_acc.rb`.
3. Enter a change description (min 10 characters) when prompted.
4. Watch the output window: `SUCCESS: model version uploaded to ACC.`
5. The new version appears in ACC Docs (your project's folder) and in the
   connector's version history — status `Draft` until reviewed (Phase 2).

## What it exports

A generic full-table CSV dump of the open network (every table, every field,
prefixed with a `table` column). This guarantees nothing is silently dropped while
the field-level mapping to the metadata schema
([specs/13-metadata-schema.md](../../specs/13-metadata-schema.md)) is validated
against real WS Pro exports — the open action item from that spec.

## Known limitations (Phase 1)

- **Not yet run against a real WS Pro install** — written to the documented
  InfoWorks Exchange API (`WSApplication.current_network`, `tables`,
  `row_object_collection`). First run on a licensed machine may need small
  adjustments (e.g., prompt layout differences between WS Pro builds); the
  script fails loudly rather than uploading partial data.
- Exports the full network each time (no delta) — fine for Phase 1 scale.
- `X-Dev-User` header auth — replaced when per-user APS login lands in the connector.
- CSV export only; transportable-database snapshot upload (full-fidelity payload)
  is a Phase 2 candidate alongside checkout integration.
