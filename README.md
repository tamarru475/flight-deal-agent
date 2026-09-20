# Flight deal monitor

A personal flight-deal monitoring project built with .NET 10, PostgreSQL, and SerpApi. The aim is to identify worthwhile fares using itinerary quality and comparable prices, within a strict **$0/month** operating budget.

The monitor supports manual and automatic searches across configured exact-date profiles, initially AKL–Tokyo Premium Economy and AKL–Paris Economy for two adults in NZD. It stores paired itineraries and segment-level cabin information, enforces a 200-credit monthly limit with a 50-credit reserve, and explains assessments against configurable manual baselines.

## Quick start

Use the SDK specified in `global.json` and a dedicated PostgreSQL database (tested with PostgreSQL 17):

```sh
export ConnectionStrings__FlightDeals='Host=localhost;Port=5432;Database=flight_deals;Username=flight_deals;Password=YOUR_LOCAL_PASSWORD'
dotnet run --project src/FlightDeals -- --init-db
dotnet run --project src/FlightDeals
```

Inspect the local API at `http://127.0.0.1:5080/profile`. Live searching is disabled by default. Configure dates and baselines in `src/FlightDeals/appsettings.json`; see the local-development guide before enabling a quota-consuming search.

```sh
dotnet test FlightDeals.slnx
```

Tests use synthetic provider responses. Set `TEST_POSTGRES` to include the PostgreSQL integration test; otherwise it is skipped.

## Current scope

Multiple exact-date profiles have individual intervals and priorities. A quota-aware scheduler shares the manual scan path, with at most two search requests per scan. Email notifications have durable duplicate suppression and no automatic delivery retries. Scheduling, live searches, and notifications are disabled by default. There is no UI, historical scoring, or LLM dependency. Tokyo Premium Economy uses user-approved manual seed thresholds; these are assumptions, not learned market history.

Mixed cabins are descriptive, with no automatic penalty; main-long-haul premium applicability is not evaluated yet. Prices are unverified search quotes, baggage can be incomplete, and missing separate-ticket data does not prove connection protection. The API is local-only. Deployment and backups are later work.

## Further reading

- [Container packaging and safe Compose validation](docs/containers.md)
- [Local setup, API, live scanning, and tests](docs/local-development.md)
- [Multi-profile configuration and scheduling](docs/scheduling.md)
- [Credit accounting and operational limits](docs/credit-budget.md)
- [Persistence, observations, and manual assessment](docs/design.md)
- [Email policy, delivery state, and private SMTP setup](docs/notifications.md)
