# ADR 0005: Tenant options are resolved per call and are immutable

Status: accepted

## Context

Each tenant has its own phone number id, access token and defaults. A client that stores tenant settings on a shared
`HttpClient` (default headers, base address) or infers the tenant from ambient request state can send one tenant's
message with another tenant's credentials the moment two requests overlap. Such bugs are rare, silent and serious.

## Decision

- `ITenantOptionsResolver.ResolveAsync(tenantId)` returns a new immutable `WhatsappTenantOptions` record on every call:
  enabled flag, phone number id, access token, base URL, default template language, window enforcement.
- `IWhatsappClient.SendAsync(message, options, ct)` receives those options as an argument. It sets the URL and the
  `Authorization` header on the individual request and keeps no tenant state. The shared `HttpClient` has no base
  address and no default headers.
- The tenant is always an explicit value: it comes from the API key on the way in, is stored on the outbox row, and is
  used to resolve options on the way out. It is never read from request headers on the sending side.
- Access tokens are not configuration values. The configuration names a secret, and an `ISecretProvider` resolves it
  (environment or a secret store). The options record masks the token in `ToString`, and logs mask tokens and phone
  numbers.
- A disabled tenant resolves without reading a secret, and its messages are `Skipped`.

## Consequences

- Concurrent sends for different tenants cannot mix credentials; a test sends many in parallel and checks every request.
- Resolution happens for every send. The configuration-backed resolver is cheap, and a vault-backed one can cache
  inside the `ISecretProvider`.
- Rotating a token needs no restart: the next call resolves the new value.
