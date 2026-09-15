# Isolated container deployment

This stack runs the .NET app and a dedicated PostgreSQL 17 database. There are no published host ports. The optional `probe` shares the app's network namespace, so requests reach its loopback-only API without changing the local-access middleware.

## Prepare configuration

Use the repository root as the Compose directory. On the server this can eventually be `/opt/flight-deal-agent`; inspect the server before deploying there.

```sh
umask 077
cp deploy/app.env.example deploy/app.env
cp deploy/postgres.env.example deploy/postgres.env
```

Replace the database password placeholder in both files with the same project-specific password. Keep `SERPAPI_API_KEY` empty and both `FlightDeals__LiveSearchEnabled` and `FlightDeals__SchedulerEnabled` false during validation. Never copy the existing development `.env` into an image. The app and database env files are Git-ignored; the Docker build context excludes them entirely. Docker administrators can inspect container environment variables, so environment files are not a vault.

The default configuration mount is `src/FlightDeals/appsettings.json`, read-only. To keep deployment configuration outside the checkout, set `FLIGHT_DEALS_CONFIG_FILE` to an absolute path to a complete appsettings file. `FLIGHT_DEALS_APP_ENV_FILE` and `FLIGHT_DEALS_DB_ENV_FILE` likewise override env-file locations. These three Compose variables contain paths, not secret values. Pass them through a separate `--env-file` if desired; do not print resolved Compose configuration because it includes secrets.

The defaults define project `flight-deal-agent`, bridge network `flight-deal-agent_default`, and volume `flight-deal-agent_postgres_data`. A different `-p` project name isolates local validation. No existing media-server network or Docker socket is mounted. The bridge permits outbound HTTPS/DNS for future searching; no host networking is used.

## Build and initialize with searching disabled

The example connection string disables GSS/Kerberos negotiation because this stack uses password authentication. This avoids the runtime image's missing-Kerberos-library warning without installing unused packages; it does not change TLS settings. See the [Npgsql guidance](https://www.npgsql.org/doc/release-notes/10.0.html).

```sh
docker compose config --quiet
docker compose build app
docker compose up -d --wait db
docker compose run --rm --no-deps app --init-db
docker compose up -d app
docker compose logs --tail=100 app db
```

Database readiness gates app startup. Initialization is still a separate command; normal startup never creates the application tables. PostgreSQL's initial database/user environment variables apply only when the volume is empty. The initial `POSTGRES_USER` is an administrative database role within this dedicated container; a separately provisioned application role is a possible later hardening step.

The runtime image contains published artifacts and the ASP.NET Core runtime, runs as the image's non-root `app` user, and has no SDK. The build stage uses the SDK selected by `global.json`. The app has no persistent writable volume. PostgreSQL data persists at `/var/lib/postgresql/data` in its named volume, including all four application tables, quota reservations, and scheduling history.

Both long-running services restart unless explicitly stopped. Docker JSON logs are limited to three 10 MB files per container. The probe uses the same logging limit. `/health` is liveness only; database-backed reads below also verify application database connectivity.

## Offline smoke test

```sh
docker compose --profile tools run --rm probe -fsS http://127.0.0.1:5080/health
docker compose --profile tools run --rm probe -fsS http://127.0.0.1:5080/profiles
docker compose --profile tools run --rm probe -fsS http://127.0.0.1:5080/baselines
docker compose --profile tools run --rm probe -fsS http://127.0.0.1:5080/runs
docker compose --profile tools run --rm probe -fsS http://127.0.0.1:5080/observations
docker compose --profile tools run --rm probe -sS -i -X POST \
  -H 'X-Confirm-Scan: true' http://127.0.0.1:5080/scans
```

The POST must return **409** with `Live search is disabled in configuration.` No key or provider call is needed for these checks. Verify both flags remain false before every smoke test.

## Automated tests inside Docker

The optional `tests` Dockerfile target contains the existing test suite and synthetic fixtures. It is not included in the production image. Against this dedicated stack:

```sh
docker build --target tests -t flight-deal-agent-tests:local .
docker run --rm --network flight-deal-agent_default --env-file deploy/app.env \
  flight-deal-agent-tests:local sh -c \
  'export TEST_POSTGRES="$ConnectionStrings__FlightDeals"; exec dotnet test FlightDeals.slnx --no-restore --nologo'
```

Use the corresponding project network and env-file path if overridden. Tests create and remove unique schemas and do not call SerpApi. Keep the key empty when running them. Host `dotnet test` remains supported, but without `TEST_POSTGRES` database integration tests are skipped.

## Images, updates, and shutdown

Validated versions: SDK `10.0.300`, ASP.NET runtime `10.0.8`, PostgreSQL `17.11-bookworm`, and curl probe `8.22.0`. Local validation on Linux/ARM64 built both images, initialized the schema, passed all five read endpoints and the disabled-scan guard, and passed all 39 automated tests with no skips. The initialized database survived container/network recreation using the named volume. Validation used an empty SerpApi key and both switches false; no flight searches were made.

Image versions are explicit in the Dockerfile and Compose file. Tags can receive rebuilt base layers; they are not immutable digest pins. No architecture is forced, so Docker selects the native supported platform. Before home-server deployment, verify `uname -m` and the published manifests. A multi-platform index digest can pin both architectures; a platform-specific manifest digest pins only one. Do not substitute an unverified digest or assume the Mac's image architecture matches the server.

Build a new image for code updates and recreate the app. Recreate it after env-file changes as well. Do not blindly update the PostgreSQL major version or include this stack in undocumented maintenance automation. A read-only configuration mount is writable from the host; manage that file as deployment configuration.

```sh
docker compose down
```

This removes the stack's containers/network and retains the database volume. Do not use `down -v` for a deployed database. Persistence is not backup: preserve the volume and later implement restore-tested backups, including an off-machine copy. A fresh database loses local reservation and pacing history even if provider usage can still be checked.

See [scheduling](scheduling.md) for explicit future enablement and restart/failure behavior. No home-server deployment or live search is part of container packaging validation.
