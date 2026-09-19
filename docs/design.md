# First vertical slice design

One .NET application separates API wiring, scan orchestration, provider parsing, assessment, and PostgreSQL persistence through ordinary files and methods. Manual and scheduled scans share the same scan service and database lock. Existing run timestamps provide per-profile scheduling history; `scheduler_state` stores the shared next-dispatch time, atomically with credit reservation.

The provider and scan-store interfaces allow offline tests without adding deployment layers.

Schema initialization is explicit and idempotent; the API does not create tables on startup. This initial schema stores typed run/observation snapshots in JSONB, with relational IDs, timestamps, and an independently persisted quota ledger. Schema migrations and historical query indexes can be introduced as the model evolves.

## Observations and assessment

Paired observations preserve quoted party total, per-adult average, profile snapshot, each actual segment's cabin, duration, airports and available local timestamps, plus reported layover data. Connection airport changes are determined from segment airports. Missing separate-ticket information does not establish protected connections. Baggage remains incomplete/unverified, prices are not checkout-confirmed, and `price_insights` is not required.

Manual baselines match market airports, cabin, currency, trip duration, and cabin applicability. Europe Economy is seeded with the user's starting assumptions per adult: deal below NZ$2,000; cheap below NZ$2,500; expensive at or above NZ$4,000; otherwise normal. Tokyo Premium Economy has explicit all-segments and mixed-cabin manual baselines: deal below NZ$2,500, cheap below NZ$3,000, expensive at or above NZ$4,500 per adult, otherwise normal. These are user-approved cold-start thresholds, anchored loosely to one observed quote. Not learned market history. Revise as comparable observations accumulate. Changing adult count preserves per-adult comparison, but the per-adult figure is a party average, not a provider passenger-level fare breakdown.

Assessments snapshot baseline ID/version, thresholds, assumption, reasons, limitations, and source (`ManualBaseline` or `None` in this slice). Increment the baseline version when changing assumptions. Historical evaluation is not implemented.

`AllSegmentsInRequestedCabin` and explicitly configured `MixedCabin` baselines are supported. Mixed itineraries receive no automatic penalty. `MainLongHaulSegmentsInRequestedCabin` is representable but deliberately not evaluated yet; it produces insufficient baseline unless another supported baseline applies. A mixed-cabin price classification does not certify that the main long-haul segment is premium. Segment facts remain the source for that future evaluation.

Notification requirement for a later milestone: a mixed-cabin itinerary must not trigger a strong Premium Economy deal notification solely from its price classification. Require `MainLongHaulSegmentsInRequestedCabin` or equivalent segment-quality logic first. The current mixed-cabin baseline applies the same thresholds without a penalty, but does not establish that premium seating is on the important long-haul segments. No notifications are implemented or enabled.
