# InfoWorks WS Pro ⇄ ACC Scripts

Two scripts complete the round-trip: `upload_to_acc.rb` (WS Pro → ACC) and
`download_from_acc.rb` (ACC → local disk, ready for WS Pro import).

# Upload Script (upload_to_acc.rb)

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

# Download Script (download_from_acc.rb)

Fetches the latest **approved** version of the model from ACC. If no version has
been approved yet (review workflow lands in Phase 2), it downloads the latest
version and prints a clear WARNING that it's unapproved work-in-progress —
mirroring the connector's FR3.2 behavior, never silently substituting.

## Use

1. Edit the CONFIG block (`MODEL_ID`, `USER_EMAIL`) — same values as the upload script.
2. Run it inside WS Pro (**Network → Run Ruby Script…**) — no network needs to be open.
3. The file is saved to `Downloads\acc-models\model_v<N>_<status>.csv` and the
   output shows version number, review status, change description, and network
   stats (node/link counts) from the stored metadata.
4. To bring it into WS Pro: import via the **Open Data Import Centre** against the
   sectioned CSV, or open the file to inspect what changed.

# Open-from-ACC Script (open_from_acc.rb)

The closest thing to "File → Open from ACC" WS Pro allows: downloads the latest
version AND writes its rows directly into the currently open network via the
Exchange API — no Open Data Import Centre configuration needed.

## Use

1. In the database tree: right-click a Model Group → **New → Network** (empty).
2. **Open** that new empty network (double-click).
3. **Network → Run Ruby Script…** → `open_from_acc.rb`.
4. Refresh the GeoPlan — the model from ACC appears.

Safety: the script refuses to run if the open network already contains nodes or
pipes — it only ever fills an empty network, never merges into or overwrites work.
Derived/read-only fields (flags, spatial blobs) are skipped and reported.

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
