# Search credit budget

Each scan reserves **two credits before dispatch**, under a PostgreSQL advisory lock. It performs at most one outbound search and one return-options search for the selected outbound. There are no automatic retries, alternative-outbound searches, or booking-detail requests. The inbound response supplies the paired return-trip price; outbound and inbound prices are not added together.

The app verifies an active 250-credit Free Plan and its renewal date using the free account endpoint. It uses the greater of reported usage and its durable reservations, stops at 200, and preserves at least 50 credits. Unexpected or incomplete quota information blocks scanning. Period rollover is accepted only after the previous renewal date has passed; scans pause on the renewal date when the reset cannot be established safely.

Reservations are deliberately not refunded for cached responses, partial failures, or unused second requests. They are a conservative upper bound, not a count of actual SerpApi charges. Interrupted runs are marked failed when the next scan obtains the lock; their reservations remain. Keep this ledger and use a dedicated free account: independent clients or another installation with a separate database can spend credits outside this lock. Application accounting cannot disable billing at the provider; keep the account on its free plan without paid billing.

At two credits per check, 200 credits permit at most 100 reserved checks per provider month. A single exact-date/cabin profile checked three times weekly would reserve roughly 26–28 credits monthly. Scheduling and priority allocation remain later work.

