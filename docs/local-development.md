# Local development and API

## Run locally

Install the .NET SDK specified in `global.json` and provide a dedicated PostgreSQL database (PostgreSQL 17 is used for integration testing). Configure its connection string through the environment:

```sh
export ConnectionStrings__FlightDeals='Host=localhost;Port=5432;Database=flight_deals;Username=flight_deals;Password=YOUR_LOCAL_PASSWORD'
dotnet run --project src/FlightDeals -- --init-db
dotnet run --project src/FlightDeals
```

The API binds to `http://127.0.0.1:5080` and rejects non-loopback clients. Keep it local; authentication and container/reverse-proxy access are outside this slice. `/health` is a liveness check, not a database readiness check.

| Endpoint | Purpose |
| --- | --- |
| `GET /profile` | Default (first) search configuration |
| `GET /profiles` | All search profiles with activity, interval, and priority |
| `GET /baselines` | Configured manual baselines |
| `POST /scans` | Manually scan the first profile; requires `X-Confirm-Scan: true` |
| `POST /profiles/{profileId}/scans` | Manually scan a selected profile; same confirmation header |
| `GET /runs` and `/runs/{id}` | Latest 50 runs or one run |
| `GET /observations` and `/observations/{id}` | Latest 50 paired observations or one observation |
| `GET /assessments` | Assessments for the latest 50 observations |
| `GET /quota` | Free provider account check and local quota ledger; requires API key |

Rerun `--init-db` when upgrading from the first slice: it adds the scheduler-state table without removing observations or quota history. Initialization does not start the monitoring worker.

Edit `src/FlightDeals/appsettings.json` for dates, trip-duration bounds, eligibility, and baselines, then restart. Environment variables using .NET's double-underscore configuration convention can override settings.

To deliberately enable live scanning, supply `SERPAPI_API_KEY` in the process environment and set `FlightDeals__LiveSearchEnabled=true`. The app does **not** automatically load `.env`. If your ignored local `.env` contains trusted shell assignments, load it without printing its contents:

```sh
set -a
source .env
set +a
export FlightDeals__LiveSearchEnabled=true
dotnet run --project src/FlightDeals
```

Automatic monitoring additionally requires `FlightDeals__SchedulerEnabled=true`; see [scheduling](scheduling.md) for pacing, state, and configuration. Keep it disabled for manual-only operation.

From another terminal, a deliberate live trigger is:

```sh
curl -X POST -H 'X-Confirm-Scan: true' http://127.0.0.1:5080/scans
```

Never commit credentials or enable HTTP request-URI logging: SerpApi places its key in the query string. Raw provider responses and provider tokens are not persisted or exposed by this app.

## Tests

```sh
dotnet test FlightDeals.slnx
```

Provider tests use explicitly synthetic fixtures and fake HTTP responses, with no live searches. The database integration test is skipped unless `TEST_POSTGRES` contains a connection string to a test database. It creates and removes a unique test schema, verifies persistence, locking and interrupted-run quota accounting, and never contacts SerpApi:

```sh
export TEST_POSTGRES='Host=localhost;Database=flight_deals_test;Username=postgres;Password=YOUR_TEST_PASSWORD'
dotnet test FlightDeals.slnx
```

No home-server deployment is included. Later deployment requires the agreed read-only server inspection, an isolated Compose project and PostgreSQL volume, and a backup/restore plan distinguishing same-server copies from off-machine backups.
