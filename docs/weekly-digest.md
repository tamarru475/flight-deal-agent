# Weekly digest

The weekly digest is a deterministic summary of persisted observations, separate from immediate Cheap/Deal alerts. It uses the same private SMTP host, login, sender, and recipient, but has an independent enable switch, worker, advisory lock, and delivery table. It neither reads nor changes immediate-alert suppression state. No LLM, flight searches, booking lookups, or new paid service is involved.

## Configuration and activation

In the application's restricted environment file:

```dotenv
WeeklyDigest__Enabled=false
WeeklyDigest__MovementThresholdFraction=0.05
```

Delivery is disabled by default. Scheduling is fixed at **Monday 09:00 Pacific/Auckland**, checked once a minute. The report covers the preceding Monday 00:00 through the following Monday 00:00 (exclusive); these calendar boundaries correctly handle daylight saving. SMTP settings must be valid when enabling the digest, even if immediate alerts are disabled. The two delivery switches operate independently.

Run the existing explicit `--init-db` command when deploying this schema addition. Normal startup does not create tables. This milestone does not enable or deploy the digest on the home server.

On the first enabled worker check, a durable Scheduled row records the next Monday's report. No past digest is sent immediately, including when activation occurs exactly at Monday 09:00. The first scheduled report covers its entire calendar week, which can include observations collected before enablement. Restarts retain this boundary.

After downtime, only the latest report whose Monday 09:00 deadline has passed is considered. Older unsent Scheduled rows become Skipped; missing weeks are not backfilled. No burst of missed reports is sent. Disabling and re-enabling retains the existing activation boundary and follows this same recovery rule.

## Contents

Each currently active profile appears, including an explicit no-observations message when applicable. Observations are selected by observation timestamp in the calendar week. Different persisted search definitions under the same profile ID receive separate sections; the current definition is shown even if it has no observations.

Sections include route, travel dates, requested cabin, adults/currency, observation count, lowest and latest total/per-adult quotes, classification counts, actual segment cabin descriptions, baseline sources/versions/assumptions, and limitations. Fare notes remain labelled unverified. A mixed-cabin premium price classification is not a claim that premium cabin occupies the important long-haul segments.

Summary wording is exactly `Deal observed` if any Deal occurred, otherwise `Cheap observed` if any Cheap occurred, otherwise the counts of persisted classifications. Historical assessments are not recalculated using today's baseline.

Movement uses the comparable subset matching the latest quote: identical full search definition, outbound/return segment airports, airlines and cabins, fare notes, and baggage/protection status. This conservative rule avoids merging changed routes or known baggage conditions; it does not establish full fare-product equivalence. Require at least three observations with distinct first/last timestamps and a positive initial price. Otherwise say `Too little comparable data to assess movement`.

Compare first and last per-adult prices within that subset. An increase/decrease of at least the configured fraction gives `ended higher`/`ended lower`; otherwise `roughly unchanged`. Include the percentage and sample count, explicitly described as not a market trend. Prices can fluctuate between those endpoints.

The footer includes:

- Successful, failed, no-suitable-itinerary and still-running counts for runs started in the week, using their status when the report is generated.
- Reserved credits for those runs and potentially charged requests whose attempt timestamp falls in the week. These are different measures and are not exact provider billing.
- Immediate alerts accepted by SMTP during the week and counts by current status for decisions created during the week. SMTP acceptance is not guaranteed inbox delivery.
- A current provider quota snapshot, obtained with one free account-status request when preparing the report, and headroom under the configured operating ceiling/reserve. No search endpoint is called. If unavailable or invalid, the report says so; current-period local headroom is explicitly an unverified local estimate. A previous-period quota row is not presented as current.

Week data is read in a repeatable-read transaction, without the API's recent-50 limit. The transaction ends before account and SMTP calls. The generated subject/body and snapshot timestamp are frozen in the delivery record; late observations or later status changes do not rewrite a sent report.

## Delivery and duplicate prevention

`weekly_digests` stores unique UTC period-start and period-end boundaries plus a JSON payload: period/due time, status, stable message ID, frozen email, generation/attempt/acceptance times, and a safe reason. The first Scheduled row also serves as the activation marker, so no separate scheduling table is needed. Retain it if implementing data retention later.

The digest worker holds its own PostgreSQL advisory lock. It commits Sending with the frozen email **before** SMTP. SMTP acceptance becomes Sent. A preparation failure becomes Failed, and an interrupted or ambiguous send becomes Unknown. SMTP responses and credentials are not persisted or logged. There is no automatic delivery retry; database/preparation interruptions before a durable send claim cannot have sent mail. A failed/unknown week does not block subsequent weeks.

The unique period and durable Sending state prevent automatic duplicate sends across restarts or multiple app instances. They cannot guarantee inbox delivery or provider-side exactly-once semantics. Never blindly reset Unknown to Scheduled: a message may already have been accepted.

`GET /digests` exposes the latest 20 delivery records through the existing loopback-only API. It does not send mail. There is no manual-send/test-email endpoint or command.

## Validation

Tests use fake SMTP and fake provider account responses, with search calls forbidden. Tests cover weekly boundaries including both Auckland daylight-saving transitions, first activation, quiet weeks, unchanged-price interpretation, product changes, quota reporting, delivery failures, restart recovery, and independent notification state. PostgreSQL tests require an isolated `TEST_POSTGRES` database, as for the existing suite.
