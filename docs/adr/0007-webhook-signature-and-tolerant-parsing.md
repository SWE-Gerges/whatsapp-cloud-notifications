# ADR 0007: Webhooks are authenticated by signature, and parsed tolerantly

Status: accepted. The webhook is an addition beyond a minimal Cloud API integration.

## Context

The provider calls this service: to check that a subscription is wanted (a handshake), and to deliver inbound customer
messages and message status events. The route cannot use API keys, because the caller is the provider. A forged
delivery could open a customer's service window, or mark a notification as failed. A delivery is also retried by the
provider for every answer that is not a 2xx, so a handler that fails on an unexpected field, or applies a replay twice,
would be hit again and again. The payload grows over time: new fields and new message or status types appear without
notice.

## Decision

- **Handshake.** `GET /api/v1/webhooks/whatsapp` compares `hub.verify_token` with the verify token of every configured
  tenant, in constant time and without stopping at the first match (each token is hashed first, so neither the number
  of tenants nor the length of a token shows in the timing). A match with `hub.mode=subscribe` echoes `hub.challenge`
  as plain text; anything else is a 403. The token is never logged. The framework's request-line log category
  (`Microsoft.AspNetCore.Hosting.Diagnostics`) is held at `Warning` for this reason, because it would print the query
  string; it is the only log level override in the repository.
- **Delivery.** `POST` buffers the raw body up to a cap (1 MiB by default, 413 above), then checks
  `X-Hub-Signature-256` before anything else: the header must be `sha256=` plus exactly 64 hex digits, and the digest
  must equal the HMAC-SHA256 of the **raw bytes** keyed with the tenant's app secret, compared with
  `CryptographicOperations.FixedTimeEquals`. A missing, malformed or wrong signature is a 401 with no processing.
- **Which secret.** The tenant is the one that owns `metadata.phone_number_id` in the payload, never anything from a
  request header. That id is the only thing read from the body before the signature is verified. When a delivery names
  several phone numbers, each tenant's secret must verify it, so one signature cannot vouch for another tenant.
  The app secret comes from the tenant's `WebhookAppSecretName` through `ISecretProvider`.
- **Unknown number.** A delivery for a phone number no tenant owns is acknowledged with 200 and ignored, because the
  provider retries anything else, and it is counted in a metric (`whatsapp.webhook.unknown_phone_number_ids`).
- **Tolerant parsing.** The body is walked with `JsonDocument`, not bound to a strict model. Unknown fields are
  skipped, an item without an id is dropped, a message of an unknown type is stored by its type name, an unknown status
  is stored as it came. A new field or type can never fail the delivery. Message bodies are read for nothing and
  discarded.
- **Replays are normal.** Inbound messages are keyed by `(tenant, message id)` and status events by
  `(provider message id, status, timestamp)`, both with unique indexes and conflict-free inserts (ADR 0011), so the same
  body posted three times has one effect.
- **Heavy work is not done inline.** Processing is a handful of single-row writes. Nothing calls the Cloud API from a
  webhook. A storage failure is not swallowed: the delivery answers 5xx and the provider redelivers it.

## Consequences

- Without the app secret a tenant's webhooks are ignored (and counted), never half-trusted.
- Verifying against the raw bytes means the body is buffered once, which the cap bounds.
- The signature scheme and the payload shapes are exercised only against the fake Graph server, which signs with the
  same scheme; they have not been checked against the live provider.
