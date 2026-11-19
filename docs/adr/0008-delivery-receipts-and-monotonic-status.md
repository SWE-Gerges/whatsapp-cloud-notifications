# ADR 0008: Delivery receipts are append-only, joined at read time, and ordered monotonically

Status: accepted. Receipts are an addition beyond a minimal Cloud API integration.

## Context

`Sent` in the outbox means the provider accepted the message. What happens next (delivered, read, failed) arrives later
as status webhooks, in any order: a `delivered` can overtake a `sent`, a receipt can arrive before the dispatcher has
committed `Sent` for that very message, and a receipt can concern a message this service never sent. Receipts are
redelivered. Rejecting or losing any of them, or letting arrival order decide the outcome, would make the status wrong.

## Decision

- **Append-only table.** `DeliveryReceipts` stores one row per status event (provider message id, status, the
  provider's timestamp, the echoed notification id, and the error code and title for `failed`). A unique index on
  `(provider message id, status, timestamp)` with a conflict-free insert makes a replay a no-op. A receipt is never
  rejected, whatever it refers to.
- **Joined when read.** A receipt belongs to a notification of the same tenant when it names the notification's provider
  message id, or when it carries the notification's id through `biz_opaque_callback_data` and was recorded during the
  current delivery cycle (`LastAttemptAt`, which a worker's claim and an administrator's retry both move). The
  endpoints return the receipt timeline and the derived status. The tenant check keeps a receipt from reaching another
  tenant's notification.
- **A derived delivery status, not arrival order.** `sent < delivered < read` is a ladder; `failed` is terminal. The
  derivation depends only on which statuses were seen, so every arrival order gives the same answer, and a late `sent`
  or `delivered` can neither lower a `read` nor overwrite a `failed`. A status this service does not know is stored and
  ignored by the derivation. The list endpoint filters on the same rule (a test keeps SQL and code in agreement).
- **Two vocabularies.** `NotificationStatus` is about hand-off to the provider (and the service's own decisions);
  `DeliveryStatus` is what the provider reported afterwards.
- **Effects, applied idempotently.** What receipts say about a notification is one function of the row and its
  receipts, applied when a receipt arrives, again right after the dispatcher commits an outcome (a receipt may have
  beaten it; a row still in flight is left to its worker), and by the reconciler. Whichever happens last sees both
  halves. A `failed` receipt marks the notification `Failed` with the code and title and does not retry it; only an
  administrator's retry sends again. That is stricter than "permanent codes only": a provider that gave up on a message
  has given up, and a retry is a human decision. A `failed` receipt with error 131047 (free text outside the window) on a
  free-text notification that has a template fallback queues the template version once: the fallback has a fixed event
  key and the original notification's id as its reference key, so a replay or a repeated attempt finds the same row, and
  the row records it (`FallbackNotificationId`). The same rule applies when the provider answers 131047 synchronously.

## Consequences

- No receipt is lost and none is applied twice; a stale or replayed one cannot undo a newer fact.
- After a manual retry the timeline covers the latest delivery cycle: receipts of the earlier message stay in the table
  but are no longer joined, so they cannot decide the outcome of the new send.
- Reading a notification costs one extra indexed query; a list page costs one query for all its rows.
- The join and the derivation are implemented twice (in memory and in SQL for the filter). The agreement test covers
  the combinations it lists, not every possible set.
