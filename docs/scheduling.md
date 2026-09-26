# Automatic monitoring

The application hosts one simple background worker. Both `LiveSearchEnabled` and `SchedulerEnabled` must be true for it to run. It waits one minute before its first check and checks once a minute thereafter. No frontend, external job service, booking-detail request, or retry queue is involved.

## Configuration

`FlightDeals.Profiles` is an ordered list of entries with these fields:

```json
{
  "Active": true,
  "TargetIntervalHours": 56,
  "Priority": 100,
  "Search": {
    "Id": "europe-economy",
    "Origin": "AKL",
    "Destinations": ["CDG"],
    "OutboundDate": "2027-05-01",
    "ReturnDate": "2027-06-15",
    "MinTripDays": 30,
    "MaxTripDays": 60,
    "Adults": 2,
    "Currency": "NZD",
    "RequestedCabin": "Economy",
    "MaxStops": 1,
    "MaxDurationMinutes": 1800,
    "ExcludeKnownSeparateTickets": true,
    "ExcludeAirportChanges": true
  }
}
```

The shipped list retains Tokyo Premium Economy (priority 100) and Paris Economy (priority 90), both at 56-hour intervals: approximately three scans per week. It also includes 15 active Economy historical-sampling profiles at weekly/fortnightly intervals and four inactive October profiles; see [the sampling configuration and quota projection](historical-sampling.md). The Paris profile matches the existing Europe Economy manual baseline. A classification still depends on the returned fare and actual cabin composition.

IDs must be unique and stable. Intervals must be finite and between 1 and 8760 hours; priorities must be nonnegative. Higher numbers take precedence. `Active` controls automatic eligibility; an explicit manual trigger can still scan an inactive profile. Profiles with departure dates today or in the past are skipped automatically. Restart after changing configuration.

The first profile remains the default for `GET /profile` and `POST /scans`. Use `GET /profiles` and `POST /profiles/{profileId}/scans` for explicit selection. Existing configurations with only `FlightDeals.Profile` remain supported when the list is empty. If both forms exist, the list takes precedence.

## Deterministic planning

Under the existing database-wide scan lock, the planner reads the last reserved scan time for each profile and the shared next-dispatch time. A profile is due at its last scan time plus its configured interval. A never-scanned profile is initially due.

If global pacing allows a dispatch, due profiles are ordered by descending priority, oldest due time, then ordinal profile ID. Only the first is scanned. Overdue intervals do not accumulate, and the next due time is based on the actual reservation time, not a missed calendar slot. Lower-priority profiles may run less often, or starve under severe quota pressure; this initial policy does not promise fairness.

## Budget pacing and safety

Before every manual or scheduled flight search, the same free account check and `Budget.Reserve` enforce the 200-credit ceiling and at least 50 untouched credits. Unknown quota/renewal data pauses scanning. Reservations remain conservative even when a response is cached or a request fails.

After reserving two credits, the planner calculates remaining complete scan slots (including this dispatch) and divides the time until the provider renewal date by those slots. That interval becomes the minimum delay before the next automatic dispatch, with a one-minute floor. For example, 20 days and 100 available checks produce 4 hours 48 minutes between automatic dispatches; only two checks produce a 10-day interval. Actual checks are also constrained by each profile's target interval.

Manual triggers bypass due-time and pacing restrictions but never bypass the credit ceiling, reserve, or global lock. They advance both the profile's scan history and shared pacing time, so automatic monitoring does not immediately repeat them. Other account users remain outside this application's lock; use the dedicated free account as described in [credit accounting](credit-budget.md).

## Persistence and restart behavior

Existing `scan_runs.started_at` and the profile ID in each run snapshot supply the last scan time, including failed and interrupted runs. One `scheduler_state` row stores `next_dispatch`. Run insertion, quota reservation, and pacing update commit in one transaction before a flight request is dispatched. Existing run history therefore works without a separate profile-state backfill.

Rerun `--init-db` once when upgrading to create the new table. It remains an explicit, idempotent schema initialization command and does not start the worker. Preserve run history and the quota/pacing tables across deployments; deleting them compromises scheduling history. Changing a profile ID creates a new scheduling identity. Changing an interval recomputes its due time from its last run, while the global pacing limit still applies.

Failed flight searches retain their reservation and normal profile interval. The worker does not retry a failed flight request. Future normal monitoring intervals are new scans. Pre-dispatch account, database, or lock failures are reconsidered at later polling ticks; no flight request was dispatched in those cases. Logs use safe messages without raw exception details or request URIs.

Multiple application instances sharing the same database serialize through the same advisory lock and recheck due state after acquiring it. A restart performs no catch-up loop. This worker requires the application process to remain running; home-server deployment is the next milestone.
