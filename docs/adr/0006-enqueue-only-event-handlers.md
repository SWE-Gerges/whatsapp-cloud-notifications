# ADR 0006: Event handlers only enqueue

Status: accepted

## Context

Booking events (created, status changed) are published inside the service with MediatR. If a handler called the
Cloud API itself, a slow or failing provider would slow down or fail the request that reports the booking event, and a
retry of that request could send the message a second time.

## Decision

- `BookingCreatedHandler` and `BookingStatusChangedHandler` only build the notification (template name, language,
  parameters, recipient in E.164, deterministic keys) and call `INotificationEnqueuer`. They have no dependency on
  the Cloud API client. Sending is the dispatcher's job (ADR 0002).
- The status-changed handler skips customers who did not opt in. A booking with an invalid phone number is logged and
  not queued; the HTTP endpoints validate the number first and answer 400.
- A failure to write to the outbox is not swallowed: the publish fails and the HTTP request answers 5xx, so the caller
  retries and the unique key makes that safe.
- MediatR is pinned to the 12.x line and registered in exactly one place, `AddWhatsappEvents`. Scanning the same
  assembly twice registers each handler twice, which would queue every notification twice, so the method is idempotent
  and a test resolves every handler from the real container and asserts that each is registered once.

## Consequences

- A provider outage never affects event requests; messages wait in the outbox and are retried.
- Event handling is deterministic and fast: it is a validation plus one insert.
- Extending the events means adding a handler that follows the same rule. The registration test fails if a handler is
  registered twice, but it cannot enforce "does not call the API"; the tests that publish events against a recording
  HTTP stub cover that for the existing handlers.
