# Historical sampling profiles

These profiles collect exact-date Economy quotes, not recommendations to book immediately. All use AKL, two adults, NZD, priority 20, and exclusions for known separate tickets and airport changes. No manual baselines have been added for them: assessments remain InsufficientBaseline and are visible in the weekly digest without immediate Cheap/Deal emails.

| Destination | Outbound / return departure (2027) | Max stops each way | Max duration each way | Interval |
| --- | --- | --- | --- | --- |
| BNE | Aug 10 / Aug 17 | 0 | 5h | 168h |
| SYD | Aug 10 / Aug 17 | 0 | 5h | 168h |
| NAN | Aug 10 / Aug 17 | 0 | 5h | 168h |
| RAR | Aug 10 / Aug 16 | 0 | 5h | 168h |
| PER | Aug 10 / Aug 17 | 0 | 9h | 336h |
| CNS | Aug 10 / Aug 17 | 0 | 7h | 336h |
| PPT | Aug 10 / Aug 16 | 0 | 7h | 336h |
| BKK | Aug 3 / Aug 17 | 1 | 18h | 168h |
| SGN | Aug 3 / Aug 17 | 1 | 18h | 168h |
| MNL | Aug 3 / Aug 17 | 1 | 18h | 168h |
| Japan Economy (NRT/HND) | Aug 3 / Aug 17 | 1 | 18h | 168h |
| SCL | Aug 3 / Aug 17 | **0** | 16h | 168h |
| LIM | Jul 27 / Aug 17 | 1 | 26h | 336h |
| GIG | Jul 27 / Aug 17 | 1 | 30h | 336h |
| CPT | Jul 27 / Aug 17 | 2 | 30h | 336h |

The four October BNE/SYD/NAN/RAR profiles are configured but inactive, all at 336h. BNE/SYD/NAN depart October 12 and return October 19; RAR returns October 18. Activation requires a deliberate configuration change and a new quota review. The current scheduler still permits an explicit manual scan of an inactive profile; inactive means excluded from automatic monitoring and the weekly digest.

Europe Economy and Tokyo Premium Economy retain their 56h intervals, priority 90/100 respectively, search dates and eligibility rules. Repository live-search/scheduler switches remain false by default. Dates are sampling candidates, not live-validated availability, and all new travel windows avoid the user's existing travel commitments. No rotation or automatic date generation is involved.

## Quota projection

At two reserved credits per scan, the 17 active profiles comprise two at 56h, nine at 168h and six at 336h. Projected usage is 144 credits per 28 days, 154.3 per 30 days, or 159.4 per 31 days before pacing delays. A conservative 31-day bound is `2 × 14 × 2 + 9 × 5 × 2 + 6 × 3 × 2 = 182` credits, leaving 18 below the 200-credit operating ceiling. The final 50 credits of the free 250-credit allowance remain protected. Manual scans and other account usage consume that headroom; October profiles are not included in the active totals.

Targets are not guarantees. Global pacing can delay lower-priority profiles; failed/early-exit scans retain their two-credit reservations. Never-scanned profiles become eligible under normal pacing, not in a forced deployment burst. The initial partial renewal period also includes existing account usage. The digest's movement rule still requires three comparable observations within its reporting week, so weekly/fortnightly sampling will normally report too little comparable data for movement.

## Destination-local nights

Only RAR and PPT set `RequiredDestinationNights: 7`. The existing MinTripDays/MaxTripDays fields continue to validate departure-date subtraction (six for these pairs); they do not represent accommodation nights.

After selecting the outbound, return candidates are filtered using the final outbound arrival at the destination and the first return departure. Both must reference the same configured destination airport. Parse their provider-local timestamps (`yyyy-MM-dd HH:mm`) and require exactly seven local calendar nights. Arrival August 9 / departure August 16 passes; arrival August 10 does not. October RAR requires arrival October 11 / departure October 18. No UTC conversion or assumed date-line offset replaces the returned dates.

Missing/malformed dates, different destination airports, or the wrong night count fail the check. Then the usual return selection chooses the cheapest eligible candidate from the same response. If none qualify, the run ends NoSuitableReturn with no extra search or retry. This validates local calendar nights, not guaranteed hotel occupancy or 168 elapsed hours.

The optional field is persisted in new search snapshots and included in digest comparison keys. When unset it is omitted from JSON, preserving existing notification scope hashes and leaving legacy itinerary behavior unchanged. No database schema migration or historical rewrite is required.

## Europe baseline revision

`akl-europe-economy` is now manual version 2: Deal below NZ$2,200 per adult; Cheap below NZ$2,700; Normal below NZ$3,500; Expensive at or above NZ$3,500. This records user-approved newer route/date evidence (roughly NZ$3,000 normal and NZ$2,500 cheap), not learned market truth.

New assessments store version 2 and its thresholds. Historical observations retain their original assessment, thresholds and version; immediate-alert suppression state is not reset. A future observation may legitimately receive a different classification under the revised baseline.
