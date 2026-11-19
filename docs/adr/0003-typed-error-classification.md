# ADR 0003: Typed errors, classified as transient or permanent

Status: accepted

## Context

A failed send needs one of three reactions: try again soon, try again much later, or stop. Retrying a permanent
failure wastes quota and can harm the sender's quality rating; giving up on a transient one loses a message.
The Cloud API reports failures as an HTTP status plus a JSON error envelope with a numeric `code`, an optional
`error_subcode`, a message and a trace id.

## Decision

The client turns every failure into a `WhatsappApiException` carrying a `WhatsappErrorKind`, the parsed envelope,
the HTTP status and any `Retry-After`. The kind decides the reaction:

| Transient (retry) | Permanent (never retried) |
| --- | --- |
| `Network`, `Timeout` | `InvalidRecipient` |
| `RateLimited` (HTTP 429 and rate-limit codes) | `TemplateUnavailable`, `ParameterMismatch` |
| `ServerError` (HTTP 5xx and temporary-error codes) | `Authentication`, `TokenExpired` (its own kind) |
| | `OutsideServiceWindow`, `InvalidRequest`, `InvalidResponse` |

The Graph error code is the most specific signal and wins; the HTTP status is the fallback for unknown codes.

Transient is not the whole story. A second property, `OutcomeUnknown`, says whether the request may have reached the
provider. A response that carries an error body is a known outcome: the provider looked at the request and refused it.
A DNS, connect, TLS or proxy-tunnel failure is a known outcome too, because the request provably never left. A read
timeout, a connection that broke after the request was written (any other `HttpRequestError`, a broken stream, a call
the transport cancelled) and a success response without a message id are **unknown**: the message may have been
accepted, so retrying it can deliver it twice. Those are never retried inline or by the outbox; the row becomes
`Unconfirmed` (ADR 0010).

Retries happen at two levels, for transient failures whose outcome is known:

1. **In the client**, a Polly retry strategy with exponential, jittered backoff for short blips. It honours
   `Retry-After` up to a small limit; a longer wait is not slept inside the request.
2. **In the outbox**, durable retries with exponential backoff and jitter, never earlier than a `Retry-After` from the
   provider, until the attempt budget is used up and the row is dead-lettered.

Permanent errors skip both levels and dead-letter immediately. Messages from the provider are stored with phone
numbers masked and truncated to 500 characters.

## Consequences

- Behaviour is explicit and testable: one table test covers the classification, retry tests use a fake clock.
- The classification table needs maintenance when the provider adds codes. Unknown codes degrade safely: a 4xx is
  permanent, a 5xx or 429 is transient.
- Two retry layers multiply attempts, so the inline layer is deliberately small (two extra attempts by default) and the
  outbox counts only its own attempts.
- Treating an unknown transport failure as "maybe sent" costs some retries that would have been harmless, and buys the
  guarantee that the service never sends a message again on its own initiative unless it is configured to.
