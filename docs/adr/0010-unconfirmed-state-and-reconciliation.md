# ADR 0010: An unconfirmed send waits for a receipt; the reconciler applies a configured policy

Status: accepted. The `Unconfirmed` state and the reconciler are an addition beyond a minimal Cloud API integration.

## Context

A message can reach the provider and be accepted, and the caller can still not find out: the process dies after the
provider accepted it but before the row is marked `Sent`, a read times out, or a connection breaks after the request was
written. The row does not know whether the message exists. Sending again after the lease expires gives the customer the
message twice. Not sending again may mean a message that was never sent is lost. There is no way to be right in every
case, so the trade-off has to be explicit.

## Decision

- **Tag every message.** The notification's id is sent as `biz_opaque_callback_data`, which the provider returns on every
  status webhook for that message. A receipt can therefore name the notification even when the send was never confirmed.
- **A state for "don't know".** `Unconfirmed` is where a row goes when its lease expired in `Sending` (a worker died, or
  the host shut down mid-send), and when a send failed after the request may have been written: a read timeout, a reset
  after the write, a broken stream, a success answer without a message id, or an unexpected exception from the client
  (ADR 0003). Failures that provably never left (DNS, connect, TLS) and answers with an error body keep the old paths.
  `NextAttemptAt` of an `Unconfirmed` row is the end of its grace period, not a scheduled send.
- **A receipt confirms it.** A receipt that echoes the id moves the row to `Sent` with the provider message id (a
  `failed` receipt moves it to `Failed`). This is the same function that handles ordinary receipts (ADR 0008), so it is
  idempotent with the normal path, and it is also applied right after a row becomes `Unconfirmed`, in case the receipt
  came first. A late receipt still resolves a row the reconciler had already dead-lettered, or one that was requeued but
  not yet claimed.
- **No receipt: the policy decides.** The reconciler runs on a timer. For each `Unconfirmed` row it first applies the
  receipts already stored, and only then, once the grace period (15 minutes by default) is over, applies
  `Whatsapp:Reconciliation:OnGraceExpired`:
  - `DeadLetter` (the default) is **at most once**: the row is dead-lettered with the reason `unconfirmed` and is never
    sent again automatically; an administrator can retry it after checking with the provider. The price is that a message
    which was never actually sent is not sent until a human acts.
  - `Resend` is **at least once**: the row goes back to `Pending` and is sent again, within the normal attempt budget.
    The price is a duplicate message whenever the first request had in fact been accepted.
- **The retry endpoint accepts `Unconfirmed`** as well as `Failed` and `DeadLettered`.
- **One winner.** Every transition is a conditional update on the status, so a receipt, two reconcilers and a dispatcher
  racing for one row have exactly one winner.

## Consequences

- With the default, the service never sends a message a second time on its own. Operators should watch for
  dead-lettered rows whose error starts with `unconfirmed`.
- A graceful shutdown that interrupts an in-flight send ends in `Unconfirmed`, not in a resend. In-flight sends at
  shutdown are rare, but each one needs a receipt or a human.
- The reconciler depends on receipts arriving. Without a webhook subscription, every unconfirmed send ends in the policy.
- The whole mechanism is exercised against the fake Graph server, which can accept a message and then drop the answer;
  it has not been run against the live API.
