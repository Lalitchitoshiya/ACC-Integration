# ACC Water Connector — Phase 1

ASP.NET Core (net10.0) + EF Core + PostgreSQL implementation of the Connector API from [../specs/](../specs/00-overview.md). Phase 1 scope per [specs/10-phasing.md](../specs/10-phasing.md): **InfoWorks WS Pro only — upload + version history**. Checkout/review land in Phase 2.

## Run locally

```powershell
# 1. Start PostgreSQL
docker compose up -d

# 2. Run the API (dev mode auto-creates schema + seeds demo data)
dotnet run --project src/Connector.Api
```

Health check: `GET http://localhost:5000/health` (port may differ — see launch output).

## Dev auth (Phase 1 placeholder)

Endpoints identify the caller via the `X-Dev-User` header (email of a seeded user). Replaced by APS OAuth2 3-legged auth when real ACC integration is wired (see `docs/APS-SETUP.md`). Seeded users:

| Email | Role |
|---|---|
| `admin@demo.local` | Admin |
| `modeler@demo.local` | Modeler |
| `reviewer@demo.local` | Reviewer |
| `viewer@demo.local` | Viewer |

## ACC integration modes

- `Acc:UseMock = true` (default): `MockAccClient` stores files on local disk with `mock:` URNs — full API workflow works with zero Autodesk credentials.
- `Acc:UseMock = false`: `ApsAccClient` — requires an APS app (`docs/APS-SETUP.md`); currently a skeleton with the implementation order documented in-code.

## Smoke test

```powershell
$H = @{ "X-Dev-User" = "admin@demo.local" }
$proj = (Invoke-RestMethod http://localhost:5000/api/v1/... )  # get seeded project id from DB or add a list endpoint
# Create a model
Invoke-RestMethod -Method Post -Headers $H -ContentType "application/json" `
  -Uri "http://localhost:5000/api/v1/projects/$projectId/models" `
  -Body '{"name":"Zone A Distribution","toolType":"InfoWorksWSPro","accFolderUrn":"mock:folder:zone-a"}'
# Upload a version (as modeler)
curl.exe -H "X-Dev-User: modeler@demo.local" -F "file=@model_export.csv" `
  -F "changeDescription=Initial network import for Zone A" -F "sourceTool=InfoWorksWSPro" -F "sourceToolVersion=2026.1" `
  http://localhost:5000/api/v1/models/$modelId/versions
```

## Layout

```
src/Connector.Api/
  Domain/Entities.cs        entities per specs/09-data-model.md
  Data/ConnectorDbContext.cs  EF Core mapping (jsonb metadata, xmin concurrency)
  Data/DevSeed.cs           dev-only demo data
  Metadata/                 specs/13-metadata-schema.md types + WS Pro extractor (placeholder)
  Acc/                      IAccClient + Mock (dev) + Aps (real, pending credentials)
  Http/                     endpoints per specs/11, error shape + roles per specs/12
```

## Known Phase 1 placeholders

1. **WS Pro metadata extractor** returns zeroed counts with a warning — blocked on the field-name spike flagged in [specs/13-metadata-schema.md](../specs/13-metadata-schema.md).
2. **`ApsAccClient`** is a documented skeleton — activate after creating the APS app.
3. **Dev header auth** — swapped for APS OAuth2 before any non-local deployment.
4. **`EnsureCreated` schema creation** — switch to EF migrations (`dotnet ef migrations add Initial`) before the schema stabilizes.
