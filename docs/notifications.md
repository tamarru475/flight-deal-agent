# Email notifications

Notifications are independent of evaluation and scanning. A separate worker checks persisted observations once a minute; it makes no provider requests, changes no scan schedules, and holds a separate PostgreSQL advisory lock. Notifications default to disabled. SMTP failures do not fail scan runs.

## Policy

- Only `Cheap` and `Deal` can notify.
- Notify the first eligible fare, transitions from a non-alerting classification into Cheap/Deal, and Cheap → Deal.
- Otherwise notify only if per-adult price is at least 10% below the last **successfully notified** fare. The fraction is configurable. Deal → Cheap alone does not notify.
- Actual segments must all match the requested cabin for Premium Economy, Business, or First alerts. Mixed/unknown/other cabin premium observations are suppressed. Price classification alone does not establish premium placement on important long-haul segments. No automatic mixed-cabin price penalty is added to the evaluator.
- Comparisons are scoped to the full search definition, including dates, currency, passengers, and requested cabin. Changing that definition creates a new activation boundary and suppression history.

When a search definition is first processed with notifications enabled, database time establishes its activation boundary. Existing observations are skipped, not emailed. An observation already being collected whose observation timestamp precedes that boundary is also skipped. The existing assessment is not reevaluated.

## Durable state and failure handling

Explicit `--init-db` initialization adds two tables idempotently; normal startup never initializes schema:

- `notification_profile_state`: activation time, last processed observation/classification, last notified observation/classification/per-adult price/time, keyed by search-definition hash.
- `notifications`: one decision per observation (unique FK), scope, reason, stable Message-ID, delivery status, creation/attempt/sent timestamps. No credentials, email addresses, or raw SMTP responses are stored here.

Decision and updated state commit together. `Pending` means never attempted; it can be dispatched after a restart. `Sending` commits **before** SMTP starts. SMTP acceptance produces `Sent` and atomically updates the comparison price. Sent means accepted by the SMTP server, not guaranteed inbox delivery.

An interrupted `Sending` becomes `Unknown`. Preparation failures become `Failed`; ambiguous transport failures become `Unknown` conservatively, even if the message may not have left the application. There are no automatic retries. Failed/Unknown deliveries block further notifications for that search definition, including improved prices, until reviewed. Later observations still receive recorded suppression decisions. Scanning continues normally.

`GET /notifications` exposes the latest 50 records through the existing loopback-only API. Worker errors use generic logs; SMTP exception text and protocol traces are never logged. A failure saving the SMTP result leaves Sending, which recovers as Unknown. Stable Message-ID is useful for investigation, but does not guarantee recipient-side deduplication.

V1 has no retry or resolution endpoint. Review the record, provider sent-mail/delivery evidence, and database state before an explicit operator correction. Never blindly change Unknown to Pending: it may already have been delivered. Disabling notifications pauses processing; re-enabling an already initialized definition resumes its unprocessed observations (only first activation skips history).

## SMTP settings to obtain from your own provider

Use your own sender account and chosen recipient, independently configured. Do not reuse the server's media/curator mail credentials or recipient lists. Confirm that the account permits automated low-volume personal alerts without fees.

| Environment variable | Required value |
| --- | --- |
| `Notifications__Enabled` | Leave `false` until delivery is approved |
| `Notifications__MaterialImprovementFraction` | `0.10` initially |
| `Notifications__SmtpHost` | Provider's SMTP submission hostname |
| `Notifications__SmtpPort` | Provider's STARTTLS submission port, normally `587` |
| `Notifications__SmtpUsername` | SMTP login, often your full email address |
| `Notifications__SmtpPassword` | Provider-issued app password or SMTP password |
| `Notifications__Sender` | Your authorized From address |
| `Notifications__Recipient` | One recipient address; may differ from sender |

TLS certificate validation remains enabled. This minimal transport requires STARTTLS and username/password authentication; it does not support implicit TLS on port 465 or OAuth-only accounts. Verify provider compatibility before enabling. See [Microsoft's STARTTLS documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.mail.smtpclient.enablessl?view=net-10.0). No additional email service subscription is required by this application; availability depends on your mailbox provider's terms.

## Add settings privately on the server

These are instructions for the owner, not commands already executed by this milestone:

```sh
ssh cyrus@homeainsworth1
cd /opt/flight-deal-agent
chmod 600 deploy/app.env
git check-ignore deploy/app.env
nano --ignorercfiles deploy/app.env
```

Append the notification keys from `deploy/app.env.example`, leaving Enabled=false. Paste credentials only inside the private editor; do not paste them into shell commands, chat, tracked appsettings, screenshots, or commits. Preserve the existing database/SerpApi values and scan flags. For Compose env values containing `$` or `#`, use single-quoted literal values; escape any embedded single quote according to Compose env-file syntax. Do not use `export` inside the env file.

Do not run `cat` on the file, rendered `docker compose config`, container-environment inspection, or SMTP protocol logging. `docker compose --env-file deploy/compose.env config --quiet` validates without rendering secrets. Docker administrators can still inspect container environments; mode 0600 protects the file, not against host administrators. Keep editor backups disabled.

After code deployment, schema initialization and an app recreation will be required, with notifications still disabled. Do not enable delivery before credentials are supplied and verified for provider compatibility. No real email or extra flight scan is needed to run the automated tests: they use fake senders and an isolated PostgreSQL schema.
