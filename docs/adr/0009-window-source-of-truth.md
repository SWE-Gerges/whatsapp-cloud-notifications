# ADR 0009: Real inbound messages are the source of truth for the service window

Status: accepted

## Context

The 24 hour window only matters if it reflects what the provider thinks. In the first version nothing opened it
except an admin endpoint that simulated a customer message. Real windows are opened by customers writing to the
business number, which the provider reports through the inbound webhook (ADR 0007).

## Decision

- An inbound message handled by the webhook records `(tenant, sender, timestamp)` through one recorder. The timestamp is
  the one **in the payload**, not the time of arrival, so a delivery that the provider retried for hours is judged by when
  the customer actually wrote. A message that is older than the window opens nothing.
- The sender's `wa_id` (digits) goes through the same E.164 normaliser as outbound recipients, so the window key and the
  notification key are the same string.
- The window is persisted in `ConversationWindows`, shared by all instances, and written by a single statement that
  creates the row or moves it forward (`ON CONFLICT ... DO UPDATE ... WHERE LastInboundAt < excluded.LastInboundAt`). An
  older, replayed or out-of-order message matches neither branch and changes nothing, so the window never shortens and
  concurrent writers need no retry. The in-memory store remains for tests and single-instance experiments.
- Only the minimum of a message is stored: message id, type, timestamp, tenant and sender. Message bodies are
  discarded. There is no switch to keep them.
- The admin "simulate inbound" endpoint stays, marked demo-only in its OpenAPI summary and in the README, and calls the
  same recorder, so the demo and the webhook cannot disagree about what a message does to a window. A test compares the
  state both leave behind.
- The window is open while `now < last inbound + 24 h`; at exactly 24 hours it is closed. Its length is configuration,
  and is validated to be no longer than 24 hours.

## Consequences

- Free text is sent as text only for customers who actually wrote, and falls back to the template (or is skipped) for
  everyone else.
- The window depends on the provider's timestamps and on the clock of this service. If they disagree by a few seconds
  the provider may still refuse a free-text message near the edge; the 131047 fallback (ADR 0008) covers that.
- A customer's `wa_id` can differ from the number that was written to the notification (some countries add or drop a
  digit), in which case the window is opened for a key the notification does not use. That case is not handled.
