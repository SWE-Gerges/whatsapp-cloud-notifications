# ADR 0001: Talk to the Cloud API directly

Status: accepted

## Context

The service sends transactional WhatsApp messages on behalf of several tenants. It could do so through an
intermediary (a messaging platform that wraps the Cloud API and offers its own SDK, queue and templates) or by
calling the Cloud API itself.

What the service needs from the channel:

- exact control over which template, language and parameters are sent;
- to know precisely why a message was rejected, so retries and dead-lettering are correct;
- credentials that belong to each tenant's own business account, not to a shared middleman;
- the 24 hour service-window rule applied by the sender, because only the sender knows its own conversation state.

An intermediary hides some of this behind its own error model and adds a second party that stores recipient data
and tokens and has its own outages and rate limits.

## Decision

Call the Cloud API directly: `POST {BaseUrl}/{PhoneNumberId}/messages` with a bearer token, through a typed
`HttpClient` in `Whatsapp.CloudApi`. The rest of the service only knows the `IWhatsappClient` interface in
`Whatsapp.Abstractions`, so the transport can be replaced or faked without touching the outbox, the events or the API.

The Graph base URL, including the API version, is configuration per tenant, so moving to a newer API version is a
configuration change.

## Consequences

- We own retry, error classification, template validation and window handling. That is the code this repository is
  about (see ADRs 0002 to 0006).
- Fewer moving parts and no extra processor of personal data, at the cost of more code to maintain.
- API changes (new error codes, payload changes) have to be followed by us. The error table and the payload builder
  are small, isolated and covered by table and golden-JSON tests.
- The service is verified against a fake Graph server that mimics the send endpoint and its error envelope. It has
  not been run against the live API.
