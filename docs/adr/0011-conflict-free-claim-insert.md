# ADR 0011: The claim insert reports a duplicate through the affected-row count, not an exception

Status: accepted

## Context

Claim-first idempotency (ADR 0002) relies on a unique index. A duplicate request is not an error: it is the case the
design exists for, and it happens whenever a caller retries. The first implementation inserted through EF Core and
caught the unique violation. The database driver and EF Core log that failed statement at error level every time, so a
healthy, busy service wrote error lines for expected duplicates. The first version hid them by lowering the log level
of EF Core's command category, which also hid real failed SQL.

## Decision

- The claim is a single raw statement whose result says whether the key was taken: `INSERT ... ON CONFLICT (TenantId,
  Recipient, EventKey, ReferenceKey) DO NOTHING` on PostgreSQL and `INSERT OR IGNORE` on SQLite. One affected row means
  this caller created the row; zero means the key was already claimed, and the existing row is read and returned as the
  duplicate. No exception is raised on the normal path, so nothing is logged at error level.
- The unique index is still the guard. The statement only changes how its decision is reported.
- A failure that is not a key conflict (a missing table, a lost connection, a syntax error) still throws and is logged by
  EF Core as usual. There are no blanket log-level overrides in `appsettings*.json` or in code; one targeted category
  filter exists for another reason (the request-line log would print the webhook verify token, ADR 0007).
- The same helper writes inbound messages and delivery receipts, where replays are routine as well.

## Consequences

- A test captures every log entry during a duplicate enqueue, and during dozens of parallel duplicates, and asserts that
  none is at error level; another asserts that a failing statement still is.
- The SQL differs by provider, so each new provider needs its own case (an unknown provider is refused, not guessed).
- The PostgreSQL statement is verified here only as generated text. It has not been executed against a PostgreSQL server,
  and neither have the committed migrations or the existence probe that precedes them.
