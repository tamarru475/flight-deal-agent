# Historical sampling profiles

These profiles collect exact-date Economy quotes, not recommendations to book immediately. All use AKL, two adults, NZD, priority 20, and exclusions for known separate tickets and airport changes. No manual baselines have been added for them: assessments remain InsufficientBaseline and are visible in the weekly digest without immediate Cheap/Deal emails.

| Destination | Outbound / return departure (2027) | Max stops each way | Max duration each way | Interval |
| --- | --- | --- | --- | --- |
| BNE | Aug 10 / Aug 17 | 0 | 5h | 108h |
| SYD | Aug 10 / Aug 17 | 0 | 5h | 108h |
| NAN | Aug 10 / Aug 17 | 0 | 5h | 108h |
| RAR | Aug 10 / Aug 16 | 0 | 5h | 168h |
| PER | Aug 10 / Aug 17 | 0 | 9h | 168h |
| CNS | Aug 10 / Aug 17 | 0 | 7h | 168h |
| PPT | Aug 10 / Aug 16 | 0 | 7h | 168h |
| BKK | Aug 3 / Aug 17 | 1 | 18h | 108h |
| SGN | Aug 3 / Aug 17 | 1 | 18h | 108h |
| MNL | Aug 3 / Aug 17 | 1 | 18h | 168h |
| Japan Economy (NRT/HND) | Aug 3 / Aug 17 | 1 | 18h | 108h |
| SCL | Aug 3 / Aug 17 | **0** | 16h | 168h |
| LIM | Jul 27 / Aug 17 | 1 | 26h | 168h |
| GIG | Jul 27 / Aug 17 | 1 | 30h | 168h |
| CPT | Jul 27 / Aug 17 | 2 | 30h | 168h |

The four October BNE/SYD/NAN/RAR profiles are configured but inactive, all at 336h. BNE/SYD/NAN depart October 12 and return October 19; RAR returns October 18. Activation requires a deliberate configuration change and a new quota review. The current scheduler still permits an explicit manual scan of an inactive profile; inactive means excluded from automatic monitoring and the weekly digest.

Tokyo Premium Economy retains its 56h interval and priority 100. Europe uses the three weekly priority-90 windows below. Repository live-search/scheduler switches remain false by default. Dates are sampling candidates, not live-validated availability, and all new travel windows avoid the user's existing travel commitments. No rotation or automatic date generation is involved.

## Quota projection

At two reserved credits per scan, the 21 active profiles comprise one at 56h, six at 108h and fourteen at 168h. Unconstrained usage is 225.71 credits per 30 days or 233.24 per 31 days. Independent conservative 31-day demand is `2 × (1 × 14 + 6 × 7 + 14 × 5) = 252`. This is demand, not permitted usage: the shared ceiling is 240, protecting 10 of the free 250 credits. Quota-aware pacing may defer scans, and manual activity or other account usage reduces available headroom. No catch-up bursts or automatic retries are introduced. October profiles remain excluded.

Targets are not guarantees. Global pacing can delay lower-priority profiles; failed/early-exit scans retain their two-credit reservations. Never-scanned profiles become eligible under normal pacing, not in a forced deployment burst. The initial partial renewal period also includes existing account usage. The digest's movement rule still requires three comparable observations within its reporting week, so weekly/fortnightly sampling will normally report too little comparable data for movement.

## Destination-local nights

Only RAR and PPT set `RequiredDestinationNights: 7`. The existing MinTripDays/MaxTripDays fields continue to validate departure-date subtraction (six for these pairs); they do not represent accommodation nights.

After selecting the outbound, return candidates are filtered using the final outbound arrival at the destination and the first return departure. Both must reference the same configured destination airport. Parse their provider-local timestamps (`yyyy-MM-dd HH:mm`) and require exactly seven local calendar nights. Arrival August 9 / departure August 16 passes; arrival August 10 does not. October RAR requires arrival October 11 / departure October 18. No UTC conversion or assumed date-line offset replaces the returned dates.

Missing/malformed dates, different destination airports, or the wrong night count fail the check. Then the usual return selection chooses the cheapest eligible candidate from the same response. If none qualify, the run ends NoSuitableReturn with no extra search or retry. This validates local calendar nights, not guaranteed hotel occupancy or 168 elapsed hours.

The optional field is persisted in new search snapshots and included in digest comparison keys. When unset it is omitted from JSON, preserving existing notification scope hashes and leaving legacy itinerary behavior unchanged. No database schema migration or historical rewrite is required.

## Europe baseline revision

`akl-europe-economy` is now manual version 2: Deal below NZ$2,200 per adult; Cheap below NZ$2,700; Normal below NZ$3,500; Expensive at or above NZ$3,500. This records user-approved newer route/date evidence (roughly NZ$3,000 normal and NZ$2,500 cheap), not learned market truth.

New assessments store version 2 and its thresholds. Historical observations retain their original assessment, thresholds and version; immediate-alert suppression state is not reset. A future observation may legitimately receive a different classification under the revised baseline.

## Planned-trip Cyprus searches

These are higher-priority planned-trip searches, not historical sampling. Both use Economy, two adults, NZD, weekly (168h) scans and priority 80, below Europe/Tokyo and above historical sampling. Known separate tickets and connection airport changes are excluded. Neither has a manual baseline or destination-local-night rule.

| Profile | Origin | Destination | Outbound / return departure | Max stops | Max duration each way |
| --- | --- | --- | --- | --- | --- |
| bcn-cyprus-economy-2027-05 | BCN | LCA | 5–11 May 2027 | 1 | 8h |
| tlv-cyprus-economy-2027-05 | TLV | LCA | 6–10 May 2027 | 0 | 3h |

Both profiles now use LCA only, removing LCA/PFO branching. The existing selected-outbound/return-options flow still reserves two credits and does not guarantee the globally cheapest round trip. Historical observations remain unchanged; the changed destination definition creates a new notification/comparison scope, while scheduler timing remains keyed by the same profile IDs. No booking-link enrichment or extra provider calls are included. BNE, SYD, BKK and SGN now sample every 108h under the revised 240-credit policy.

## Europe planned-trip windows

The retired `europe-economy` profile searched 1 May–15 June 2027. Its replacement uses three weekly (168h), priority-90 exact-date profiles:

- `cdg-economy-2027-04-22`: AKL–CDG, 22 April–3 June 2027.
- `cdg-economy-2027-04-24`: AKL–CDG, 24 April–3 June 2027.
- `bcn-economy-2027-04-22`: AKL–BCN, 22 April–3 June 2027.

All retain Economy, two adults, NZD, 30–60-day trip bounds, at most one stop and 30 hours each way, and the existing separate-ticket/airport-change exclusions. Dates specify local departures, not guaranteed arrival dates. Barcelona onward travel is not included. The Europe manual baseline remains unchanged.

Europe expected demand remains 25.71/26.57 credits over 30/31 days; its independent 31-day bound increases from 28 to 30. There are 25 configured profiles: 21 active and four inactive October profiles. The shared 240-credit ceiling may defer scans.

Old observations, assessments and notification state are not rewritten. Each replacement gets an independent scheduler ID and notification scope. The existing global dispatch gate remains in force; new profiles become eligible naturally when it opens. Retired Europe observations remain readable through diagnostics but no longer appear in active-route sections of future digests. No date rotation or additional provider calls are introduced.
