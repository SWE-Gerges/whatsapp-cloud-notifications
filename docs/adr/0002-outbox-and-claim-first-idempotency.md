# ADR 0002: Outbox with claim-first idempotency

Status: accepted (the duplicate path and crash recovery were refined in [ADR 0010](0010-unconfirmed-state-and-reconciliation.md) and [ADR 0011](0011-conflict-free-claim-insert.md))

## Context

Booking systems retry. The same event can arrive twice, or two requests can race. Sending a customer the same
confirmation twice is a visible defect, and the Cloud API has no idempotency key that could prevent it for us.
A WhatsApp outage must also never fail the request that reports the booking event.

## Decision

Every notification is first written to a table (`NotificationLogs`) and only the dispatcher sends it.

- **Claim first.** Enqueue inserts the row before anything else happens. A unique index on
  `(TenantId, Recipient, EventKey, ReferenceKey)` decides races: exactly one insert wins. The insert is conflict-free
  (see ADR 0011): the affected-row count says whether the key was taken, and a loser reads the existing row and returns
  it as a duplicate (HTTP 200 with `duplicate: true`). Keys are trimmed before the insert and the reference key is
  shortened to 100 characters, so equal keys compare equal.
- **Deterministic keys for events.** A booking-created event uses the booking id; a status change uses
  `bookingId:status`. Re-posting an event is therefore harmless.
- **Leased dispatch.** The dispatcher selects due `Pending` rows, then flips them to `Sending` with a
  `LeaseToken` and expiry in one conditional `UPDATE ... WHERE Status = 'Pending'`. The database lets only one of
  several concurrent dispatchers win a row. Completing a row is also conditional on holding the lease, so a worker
  that lost its lease cannot overwrite the new owner's result.
- **Outcomes are data.** `Sent`, `Skipped`, `Failed` (rejected locally, or reported undeliverable by a receipt),
  `DeadLettered` (permanent provider error, attempts exhausted, or an unconfirmed send that stayed unconfirmed),
  `Unconfirmed` (the provider may have accepted the message), or back to `Pending` with an exponentially backed-off,
  jittered `NextAttemptAt`. The dispatcher records errors on the row and never throws to callers.
- **Crash recovery.** A row stuck in `Sending` past its lease is not sent again: the message may already have left. It
  becomes `Unconfirmed` and waits for a receipt or for the reconciler (ADR 0010).

## Consequences

- At most one row, and so at most one send per attempt, per notification key. Tests check this with dozens of parallel
  enqueues and several parallel dispatchers on SQLite; the PostgreSQL statements are checked only as generated text
  and have not been run against a server here.
- Delivery is not instantaneous: it waits for the next poll (one second by default).
- A message can still be delivered twice, but only by an explicit choice: the at-least-once reconciliation policy, or an
  administrator retrying an unconfirmed notification. The default never sends an unconfirmed message again by itself.
- The database becomes a hard dependency of the event endpoints; if it is down, an event request fails with a 5xx so
  the caller can retry the same key safely.
- Latency and throughput are bounded by batch size, concurrency and the poll interval, all configurable.
