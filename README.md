# Event Ledger

Two independently runnable microservices that ingest financial transaction events from
upstream systems that deliver them **out of order** and **more than once**, and keep
account balances correct anyway.

Built with .NET 10 (ASP.NET Core Minimal APIs), EF Core + SQLite, Polly v8 and
OpenTelemetry. See [Requirment.md](Requirment.md) for the assignment,
[SPEC.md](SPEC.md) for the full system specification, and [PLAN.md](PLAN.md) for how the
work was sequenced.

---

## Architecture

```
                        ┌───────────────────────┐
Client ────HTTP────────▶│  Event Gateway API    │   public, :8080
                        │  events.db (SQLite)   │
                        └───────────┬───────────┘
                                    │ REST, W3C traceparent propagated,
                                    │ timeout → retry → circuit breaker
                                    ▼
                        ┌───────────────────────┐
                        │  Account Service      │   internal only, :8081
                        │  accounts.db (SQLite) │
                        └───────────────────────┘
```

| Service | Role | Owns |
|---|---|---|
| **Event Gateway** | The public entry point. Validates, deduplicates, records events, and asks the Account Service to apply them. | The event log |
| **Account Service** | Applies transactions and computes balances. Never exposed to clients. | Account state |

Separate processes, separate databases, no shared in-process state. They meet only over
HTTP, and the contract between them is pinned by tests.

**The write path** (`POST /events`) records the event as `PENDING` *before* calling
downstream and promotes it to `APPLIED` only once the Account Service accepts it. A
failed call therefore leaves a record rather than losing the event, and the client can
drive it to completion by resubmitting the same `eventId`.

### Endpoints

**Gateway (`:8080`, public)**

| Method | Path | Notes |
|---|---|---|
| `POST` | `/events` | `201` new · `200` duplicate or retry · `400` invalid · `502` downstream rejected · `503` downstream unavailable |
| `GET` | `/events/{id}` | Local read — works while the Account Service is down |
| `GET` | `/events?account={id}` | Chronological by `eventTimestamp`. Local read |
| `GET` | `/accounts/{id}/balance` | Proxied to the Account Service. `503` if it is unreachable |
| `GET` | `/health` | `200 Healthy` / `200 Degraded` / `503 Unhealthy` |
| `GET` | `/metrics` | Prometheus |

**Account Service (`:8081`, internal)**

| Method | Path | Notes |
|---|---|---|
| `POST` | `/accounts/{id}/transactions` | `201` applied · `200` replay, no state change |
| `GET` | `/accounts/{id}/balance` | `404` if the account has no transactions |
| `GET` | `/accounts/{id}` | Details + 10 most recent transactions |
| `GET` | `/health`, `/metrics` | |

---

## Prerequisites

- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** — the only requirement to build,
  test and run. `dotnet restore` fetches everything else.
- **Docker** (optional) — only for the Compose path below.

No database to install: each service creates its own SQLite file on startup.

---

## Running

### Option A — Docker Compose

```bash
docker compose up --build
# Gateway on http://localhost:8080; the Account Service is deliberately not published.
```

The Account Service has **no host port mapping** — it is reachable only from the gateway
on the Compose network, which is the assignment's "not exposed to external clients" made
real rather than promised. The gateway waits for the Account Service to report *healthy*
before starting.

> ⚠️ **The Compose path is written and validated (`docker compose config`) but has not been
> executed.** The machine this was built on cannot pull from `mcr.microsoft.com`: MCR's
> IPv6 addresses are unreachable from that network while its IPv4 works (`curl -6` fails,
> `curl -4` returns `200`; NuGet and Docker Hub are unaffected). The `dotnet run` path
> below is fully verified and exercises identical code — Compose only supplies the base URL
> via `AccountService__BaseUrl`, and that override is verified. If `docker compose up`
> misbehaves on your machine, please fall back to Option B; I would rather flag this than
> let it read as tested.

### Option B — `dotnet run` (verified)

Two terminals, from the repository root:

```bash
# Terminal 1 — Account Service
dotnet run --project src/AccountService --urls http://localhost:8081
```

```bash
# Terminal 2 — Gateway
dotnet run --project src/Gateway --urls http://localhost:8080
```

The Gateway defaults to `http://localhost:8081` for the Account Service. To point it
elsewhere, set `AccountService__BaseUrl` (the `__` is .NET's nesting convention):

```bash
AccountService__BaseUrl=http://localhost:8081 dotnet run --project src/Gateway --urls http://localhost:8080
```

```powershell
# PowerShell
$env:AccountService__BaseUrl = "http://localhost:8081"; dotnet run --project src/Gateway --urls http://localhost:8080
```

### Try it

```bash
# Submit an event
curl -i -X POST http://localhost:8080/events -H 'Content-Type: application/json' -d '{
  "eventId": "evt-001", "accountId": "acct-123", "type": "CREDIT",
  "amount": 150.00, "currency": "USD", "eventTimestamp": "2026-05-15T14:02:11Z",
  "metadata": { "source": "mainframe-batch", "batchId": "B-9042" }
}'                                                            # 201

# Submit it again — idempotent: returns the original, balance untouched
curl -i -X POST http://localhost:8080/events -H 'Content-Type: application/json' -d '{
  "eventId": "evt-001", "accountId": "acct-123", "type": "CREDIT",
  "amount": 999.00, "currency": "USD", "eventTimestamp": "2026-05-15T14:02:11Z"
}'                                                            # 200, amount still 150.00

# An earlier event, arriving later
curl -X POST http://localhost:8080/events -H 'Content-Type: application/json' -d '{
  "eventId": "evt-002", "accountId": "acct-123", "type": "DEBIT",
  "amount": 24.50, "currency": "USD", "eventTimestamp": "2026-05-15T08:00:00Z"
}'

curl "http://localhost:8080/events?account=acct-123"          # chronological, evt-002 first
curl http://localhost:8080/accounts/acct-123/balance          # 125.50
```

Stop the Account Service and the degradation is visible: `POST /events` returns `503` with
`Retry-After` and keeps the event `PENDING`, the two `GET /events` reads keep working,
`/health` reports `Degraded`, and the balance proxy returns a `503` that names the Account
Service. Start it again, resubmit the same `eventId`, and it applies — exactly once.

---

## Tests

```bash
dotnet test
```

**109 tests**, no external dependencies — WireMock.Net stands in for the Account Service,
and each test gets its own SQLite file.

| Area | Covers |
|---|---|
| Core | Idempotency (including concurrent and replay-with-a-different-amount), out-of-order arrival, exact decimal balances, validation |
| Resiliency | Retries happen and stop; the circuit opens and fails fast without another downstream call; recovery after the break; `4xx` is never retried |
| Degradation | `503` + `PENDING` when downstream is down; reads still `200`; health `Degraded` |
| Tracing | Trace propagation to the Account Service; a generated trace when the client sends none; retries sharing one trace id |
| Integration | Both real services in-process, no stubs — including 8 concurrent duplicates leaving the balance at `150.00` |

---

## Design decisions

### Idempotency lives in *both* services — and neither is sufficient alone

The Gateway deduplicates so clients can safely resubmit. The Account Service deduplicates
so the Gateway's **retries** are safe. Both delegate it to the database — `eventId` is a
primary key in the Gateway and a unique index in the Account Service — so a duplicate
cannot physically become a second row, whatever the application logic does.

They are not redundant. Under concurrency, a request that finds an event already `PENDING`
cannot distinguish *"the previous apply failed"* from *"it is still in flight"*, so it
retries, and the same `eventId` can reach the Account Service several times. That is safe
by design rather than by luck: the downstream unique index collapses those into one effect
on the balance. The integration test that submits 8 concurrent duplicates and asserts a
balance of `150.00` fails if **either** service's idempotency is removed.

### Out-of-order arrival needs no special handling

A balance is a commutative sum (`Σ credits − Σ debits`), so arrival order cannot affect it.
Listings sort by `eventTimestamp` at query time rather than trusting arrival order.
Timestamps are stored as fixed-width UTC text, so SQLite's lexicographic ordering *is*
chronological ordering.

### Resiliency: timeout → retry with backoff and jitter → circuit breaker

All three, on the Gateway's only route into the Account Service, because they solve
different problems:

| Setting | Value | Why |
|---|---|---|
| Per-attempt timeout | 2s | A hung downstream must not hang the client |
| Retries | 3, exponential backoff from 200ms, **with jitter** | Absorbs transient blips; jitter prevents synchronised retry storms |
| Circuit breaker | opens at ≥50% failure over 10s (min 5 calls), 5s break | Fails fast while the downstream recovers |
| Total timeout | 10s | An upper bound on client-perceived latency |

Retry alone would hammer a service that is already failing. The **circuit breaker** is the
one I would keep if forced to choose: it converts sustained failure into immediate `503`s,
which keeps Gateway threads off a struggling downstream (a bulkhead-like effect) and gives
it room to recover.

**The point worth making: retrying a POST is only safe because the downstream apply is
idempotent.** The resiliency and the idempotency are co-designed, not independent choices.
Retrying a non-idempotent apply would double-count money.

Everything lives under `AccountService:Resilience` in `appsettings.json`, so tests trip the
breaker in milliseconds rather than waiting out production windows.

### Money and storage

`decimal` end-to-end, stored as TEXT. SQLite has no decimal type and REAL would silently
lose precision on money. The trade-off: SQLite cannot `SUM` text, so an account's
transactions are summed in memory — exact by construction, bounded by one account's
transaction count, and worth revisiting only if an account grows large enough to make that
read expensive.

Connection pooling is **disabled**. EF Core registers user-defined functions per SQLite
connection and Microsoft.Data.Sqlite unregisters them when returning one to the pool; under
concurrent requests that races with active statements and throws SQLite error 5, surfacing
as a `500` on a valid request. It reproduced at roughly 1 in 1,600 concurrent same-`eventId`
requests before the fix. Opening a local-file connection is cheap, so the pool bought little
and cost correctness under load.

### Observability

One client request produces **one trace id across both services**. ASP.NET Core continues
an inbound `traceparent` (or starts one), .NET propagates it over HttpClient, and a custom
`ConsoleFormatter` puts `traceId`, `spanId` and `service` at the top level of every JSON log
line — so "show me every line from both services for this request" is one `grep`. The trace
id also appears in every RFC 7807 error, so a client-reported failure is findable in logs.

Metrics are on `/metrics` in Prometheus format. `http_server_request_duration_seconds` is
labelled by route and status (count, latency and error rate all derive from it), plus
custom counters: `gateway_events_total` by outcome and `account_transactions_applied_total`
split `applied` vs `replayed` — the visible evidence that idempotency works, since replays
should be common and must never move a balance.

---

## Trade-offs and next steps

- **A `PENDING` event needs a client to resubmit it.** The obvious next step is an async
  fallback: queue events locally when the Account Service is down and drain the queue when
  it recovers, so the client's `503` becomes an accepted-and-pending `202`. Both services'
  idempotency already make that safe.
- **No authentication, HTTPS termination or rate limiting.** Out of scope here; a real
  public gateway needs all three.
- **Single currency per account is assumed, not enforced.** Balances would be wrong for a
  mixed-currency account. Enforcing it, or keying balances by currency, is the fix.
- **Traces are not exported anywhere** — the logs carry the ids. An OTLP endpoint is
  configurable (`Otlp:Endpoint`), so pointing at Jaeger is config, not code.
- **The two services duplicate small pieces of infrastructure** (the log formatter, the
  `CREDIT`/`DEBIT` constants) rather than sharing a library, keeping their builds
  independent. At a third service, a shared package would start to pay for itself.
- **Contract tests (Pact) would replace hand-written stubs.** The Gateway's WireMock fake
  encodes the Account Service's contract by hand, so the two could drift; the in-process
  integration test is the current guard against that.
- **SQLite files are ephemeral**, inside each container, with no volumes — deliberate for
  this assignment, not a production choice.
- **The Prometheus exporter is a `-beta` package.** It is the only supported option and has
  been beta for years, but it is a pre-release dependency.
