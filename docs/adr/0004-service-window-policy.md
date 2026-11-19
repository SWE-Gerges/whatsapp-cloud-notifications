# ADR 0004: The 24 hour service window is a policy applied at dispatch time

Status: accepted (the source of the window was settled in [ADR 0009](0009-window-source-of-truth.md))

## Context

WhatsApp accepts free-form text only within 24 hours of the recipient's last inbound message. Outside that window
only approved templates may be sent, and a free-text attempt is rejected (error code 131047). Whether a window is open
changes with time and with inbound traffic, so it can differ between the moment a notification is requested and the
moment it is sent.

## Decision

- `IConversationWindowStore` records the last inbound message per tenant and recipient (`RecordInboundAsync`) and
  answers `IsOpenAsync`. There is an in-memory implementation and a database implementation shared by all instances.
  The window only moves forward: a late or replayed message never shortens it.
- `IConversationWindowPolicy` decides at dispatch time:
  - a template request is always sent as a template;
  - a free-text request is sent as text only when the window is open;
  - otherwise it falls back to the template stored with the request, if there is one;
  - otherwise it is `Skipped` with the reason `window closed`. This is an outcome, not an error.
- A tenant can switch enforcement off (`ServiceWindowEnabled = false`), for example for a test number.
- Booking events are queued as templates by default. A setting makes them free text with the template as the
  fallback, so customers in an open window get plain text and everyone else gets the template.
- Inbound messages arrive through the signed webhook (ADR 0007), which opens the window. An admin endpoint still
  simulates an inbound message for demos; it is marked demo-only and goes through the same recorder.
- If the provider disagrees about the window anyway (error 131047, synchronously or in a `failed` receipt), a free-text
  notification with a template is not lost: its template version is queued once (ADR 0008).

## Consequences

- A request is almost never rejected by the provider for being outside the window, because the sender checks first.
  The window is computed from the timestamps in the webhook payloads, so a clock difference between this service and
  the provider can still produce a disagreement, which the fallback above absorbs.
- Deciding at dispatch time means the answer reflects the state when the message is really sent, not when it was queued.
