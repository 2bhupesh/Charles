# Event Ledger — System Specification

Version 1.0 · 2026-07-16
Companion documents: [Requirment.md](Requirment.md) (assignment), [PLAN.md](PLAN.md) (execution phases)

---

## 1. Overview

The Event Ledger is a system of two independently runnable microservices that ingest financial transaction events from upstream systems that may deliver events **out of chronological order** and **more than once**.

| Service | Role | Port | Exposure |
|---|---|---|---|
| **Event Gateway API** | Public entry point: validates, deduplicates, stores events; forwards transactions downstream | 8080 | Public |
| **Account Service** | Owns account state: applies transactions, computes balances | 8081 | Internal only (reachable only by the Gateway) |

```
Client ──HTTP──▶ Event Gateway API ──HTTP (sync REST, resilient)──▶ Account Service
                     │                                                  │
                 SQLite (events.db)                              SQLite (accounts.db)
```

### 1.1 Technology stack

- **.NET 10 (LTS)**, ASP.NET Core Minimal APIs
- **EF Core + SQLite**, one file-based database per service (`events.db`, `accounts.db`). `:memory:` is prohibited (connection-scoped lifetime). Tests use isolated per-test database files or shared-cache connections.
- **Polly v8** via `Microsoft.Extensions.Http.Resilience` for the Gateway→Account Service client
- **OpenTelemetry** for tracing and metrics; W3C Trace Context propagation
- **xUnit + `WebApplicationFactory` + WireMock.Net** for tests

### 1.2 Global conventions

- All money amounts are **`decimal`** end-to-end. JSON serializes as a number (e.g., `150.00`).
- All timestamps are ISO 8601 with offset, normalized to **UTC** on ingestion, serialized as `yyyy-MM-ddTHH:mm:ssZ`.
- All error responses use **RFC 7807 `application/problem+json`** (§6).
- All identifiers (`eventId`, `accountId`) are opaque non-empty strings, max length 128.
- `type` comparison is **case-sensitive**: exactly `"CREDIT"` or `"DEBIT"`.
- Every response includes the request's trace ID in logs (§8); services never leak stack traces to clients.

---

## 2. Domain model

### 2.1 Gateway — `Event` (table `Events`, database `events.db`)

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `EventId` | TEXT | **PRIMARY KEY** | Client-supplied; the idempotency key |
| `AccountId` | TEXT | NOT NULL, indexed | |
| `Type` | TEXT | NOT NULL, `CREDIT` \| `DEBIT` | |
| `Amount` | TEXT (decimal) | NOT NULL, > 0 | EF value converter, decimal-safe |
| `Currency` | TEXT | NOT NULL | e.g. `USD`; no cross-currency math in v1 |
| `EventTimestamp` | TEXT (ISO 8601 UTC) | NOT NULL, indexed with `AccountId` | Sorts lexicographically = chronologically |
| `Metadata` | TEXT (JSON) | NULL | Stored verbatim, returned verbatim |
| `Status` | TEXT | NOT NULL, `PENDING` \| `APPLIED` | §4.2 write-path state |
| `ReceivedAt` | TEXT (ISO 8601 UTC) | NOT NULL | Server-assigned arrival time |

### 2.2 Account Service — `AccountTransaction` (table `Transactions`, database `accounts.db`)

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | INTEGER | PK autoincrement | Internal |
| `EventId` | TEXT | NOT NULL, **UNIQUE index** | Idempotency key for replay-safe applies |
| `AccountId` | TEXT | NOT NULL, indexed | |
| `Type` | TEXT | NOT NULL, `CREDIT` \| `DEBIT` | |
| `Amount` | TEXT (decimal) | NOT NULL, > 0 | |
| `Currency` | TEXT | NOT NULL | |
| `EventTimestamp` | TEXT (ISO 8601 UTC) | NOT NULL | |
| `AppliedAt` | TEXT (ISO 8601 UTC) | NOT NULL | Server-assigned |

Accounts are implicit: an account exists iff it has ≥ 1 transaction. There is no account-creation endpoint.

### 2.3 Balance definition

```
balance(accountId) = Σ Amount where Type = CREDIT  −  Σ Amount where Type = DEBIT
```

Summation is commutative → correctness is independent of arrival order. Balances **may go negative** (no overdraft rule in v1). Balance is computed on read; nothing is cached.

---

## 3. Event Gateway API (public, :8080)

### 3.1 `POST /events` — submit a transaction event

**Request body**

```json
{
  "eventId": "evt-001",
  "accountId": "acct-123",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z",
  "metadata": { "source": "mainframe-batch", "batchId": "B-9042" }
}
```

**Validation rules** (all violations → `400`, see §6):

| Field | Rule |
|---|---|
| `eventId` | required, non-empty string ≤ 128 chars |
| `accountId` | required, non-empty string ≤ 128 chars |
| `type` | required, exactly `CREDIT` or `DEBIT` |
| `amount` | required, number, strictly > 0 |
| `currency` | required, non-empty string (3-letter code expected, not enforced beyond non-empty) |
| `eventTimestamp` | required, parseable ISO 8601 |
| `metadata` | optional; if present must be a JSON object |

**Processing sequence**

1. Validate (§ above). Invalid → `400`, nothing persisted.
2. Look up `eventId`.
   - Exists with status `APPLIED` → **duplicate**: return `200` with the original stored event. No downstream call, no state change.
   - Exists with status `PENDING` → previous apply failed; proceed to step 4 (retry path) using the stored record.
   - Not found → insert as `PENDING` (single INSERT; a concurrent duplicate loses on the PK and is treated as duplicate).
3. Call Account Service `POST /accounts/{accountId}/transactions` through the resilient client (§7).
4. Outcome:
   - Downstream `201` or `200` → set status `APPLIED`, return `201` (or `200` if this was a retry of a previously `PENDING` event) with the stored event.
   - Downstream unreachable/timeout/circuit-open → leave `PENDING`, return `503` (§6). Client may safely resubmit the same `eventId`.
   - Downstream `400` → data inconsistency between services; return `502` and log at Error.

**Responses**

| Code | Meaning | Body |
|---|---|---|
| `201 Created` | New event accepted and applied | Event resource (§3.5) |
| `200 OK` | Duplicate `eventId` (already applied), or successful retry of a `PENDING` event | Original event resource |
| `400 Bad Request` | Validation failure | ProblemDetails |
| `502 Bad Gateway` | Account Service rejected a validated transaction | ProblemDetails |
| `503 Service Unavailable` | Account Service unreachable; event saved as `PENDING` | ProblemDetails + `Retry-After: 5` |

### 3.2 `GET /events/{id}`

- `200` with the event resource (§3.5) if found — **works even when Account Service is down** (local read).
- `404` ProblemDetails if unknown.

### 3.3 `GET /events?account={accountId}`

- `200` with `{ "accountId": "...", "events": [ ... ] }`, events sorted **ascending by `eventTimestamp`** (tie-break: `receivedAt` ascending). Empty list if the account has no events.
- `400` if the `account` query parameter is missing or empty.
- **Works even when Account Service is down** (local read).

### 3.4 `GET /health`

`200` when healthy:

```json
{
  "status": "Healthy",
  "service": "gateway",
  "checks": { "database": "Healthy", "accountService": "Healthy | Degraded" }
}
```

`accountService` reflects last-known reachability (circuit state); a `Degraded` downstream does **not** make the Gateway report unhealthy (`200` with `"status": "Degraded"`). Database failure → `503` with `"status": "Unhealthy"`.

### 3.5 Event resource (response shape)

```json
{
  "eventId": "evt-001",
  "accountId": "acct-123",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z",
  "metadata": { "source": "mainframe-batch", "batchId": "B-9042" },
  "status": "APPLIED",
  "receivedAt": "2026-07-16T09:00:00Z"
}
```

### 3.6 `GET /metrics` — Prometheus exposition format (bonus, §9)

---

## 4. Account Service (internal, :8081)

### 4.1 `POST /accounts/{accountId}/transactions`

**Request body** — same fields as §3.1 minus `accountId` (taken from the route) and minus `metadata`:

```json
{
  "eventId": "evt-001",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z"
}
```

Validation identical to §3.1 for the present fields → `400` on violation.

**Idempotency contract (critical):** `eventId` has a UNIQUE index. On conflict, the service returns `200` with the previously stored transaction and **does not** modify state. A fresh apply returns `201`. This makes the endpoint safe under Gateway retries — the same event can be posted any number of times with exactly-once effect.

| Code | Meaning |
|---|---|
| `201 Created` | Transaction applied |
| `200 OK` | Replay of an already-applied `eventId`; no state change |
| `400 Bad Request` | Validation failure |

Response body (both 200 and 201):

```json
{
  "eventId": "evt-001",
  "accountId": "acct-123",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z",
  "appliedAt": "2026-07-16T09:00:01Z"
}
```

### 4.2 `GET /accounts/{accountId}/balance`

- `200`: `{ "accountId": "acct-123", "balance": 125.50, "currency": "USD" }`
  (`currency` = currency of the account's transactions; v1 assumes one currency per account and does not enforce it)
- `404` ProblemDetails if the account has no transactions.

### 4.3 `GET /accounts/{accountId}`

- `200`:

```json
{
  "accountId": "acct-123",
  "balance": 125.50,
  "currency": "USD",
  "transactionCount": 7,
  "recentTransactions": [ /* up to 10, newest eventTimestamp first */ ]
}
```

- `404` if the account has no transactions.

### 4.4 `GET /health`

Same shape as §3.4 with `"service": "account-service"` and a `database` check only.

### 4.5 `GET /metrics` — Prometheus exposition format (bonus, §9)

---

## 5. Cross-service invariants

1. **No shared state.** Separate processes, separate databases, communication only via HTTP.
2. **Exactly-once effect per `eventId`** on the balance, regardless of duplicate submissions to the Gateway or duplicate deliveries Gateway→Account Service (retries).
3. **Chronological reads:** `GET /events?account=` is ordered by `eventTimestamp`, never by arrival order.
4. **Balance correctness is arrival-order-independent** (commutative sum, §2.3).
5. An event with Gateway status `APPLIED` **must** exist in the Account Service. A `PENDING` event may or may not (the downstream call may have succeeded just before a timeout) — which is exactly why §4.1 must be idempotent.

---

## 6. Error format (both services)

RFC 7807 `application/problem+json`:

```json
{
  "type": "https://httpstatuses.io/400",
  "title": "Validation failed",
  "status": 400,
  "detail": "One or more fields are invalid.",
  "errors": { "amount": ["Amount must be greater than 0."], "type": ["Type must be CREDIT or DEBIT."] },
  "traceId": "6f1c4b2a9d3e4f5a8b7c6d5e4f3a2b1c"
}
```

- `errors` (field → messages) present on validation failures.
- `traceId` present on every ProblemDetails so a client-reported error is findable in logs.
- `503` from the Gateway carries `"detail": "Account Service is currently unavailable. The event was recorded and may be retried with the same eventId."` and a `Retry-After: 5` header. Balance-proxying/unavailable-downstream errors say explicitly that the **Account Service** is unreachable.

---

## 7. Resiliency (Gateway → Account Service client)

A single typed `HttpClient` wrapped in a standard resilience pipeline, order: **outer timeout → retry → circuit breaker → inner per-attempt timeout**.

| Setting | Value | Rationale |
|---|---|---|
| Per-attempt timeout | 2 s | Fail slow calls fast |
| Retries | 3, exponential backoff base 200 ms **+ jitter** | Bonus item; jitter prevents synchronized retry storms |
| Retry triggers | 5xx, 408, timeouts, transport errors — **never 4xx** | 4xx won't heal with retry |
| Circuit breaker | opens at ≥ 50 % failure over a 10 s sampling window (min 5 calls); break duration 5 s | Fail fast while downstream recovers; protects it from load |
| Total request timeout | 10 s | Upper bound on client-perceived latency |

All values live in `appsettings.json` so tests can trip the breaker quickly (e.g., min 2 calls, 1 retry).

**Why this pattern (README talking point):** retry-with-backoff absorbs transient blips; the circuit breaker converts sustained failure into immediate `503`s, keeping Gateway threads free (a bulkhead-like effect) and giving the Account Service headroom to recover. Retrying a POST is only safe because §4.1 is idempotent — resiliency and idempotency are co-designed.

### 7.1 Graceful degradation matrix (Account Service down)

| Gateway endpoint | Behavior |
|---|---|
| `POST /events` | `503` ProblemDetails after retries/circuit-open; event kept `PENDING`; never hangs, never `500` |
| `GET /events/{id}` | Normal `200`/`404` (local data only) |
| `GET /events?account=` | Normal `200` (local data only) |
| `GET /health` | `200` `Degraded` (Gateway itself is fine) |

---

## 8. Observability

### 8.1 Distributed tracing

- OpenTelemetry SDK in both services; ASP.NET Core + HttpClient auto-instrumentation.
- **W3C Trace Context** (`traceparent`/`tracestate` headers). The Gateway starts a trace per inbound request (or continues an inbound `traceparent`); .NET propagates it on the outbound HttpClient call automatically.
- One client request ⇒ one trace ID visible in both services' logs.
- Exporter: none required in v1 (logs carry the IDs). OTLP endpoint left configurable for the Jaeger bonus.

### 8.2 Structured logging

JSON console logs (one JSON object per line) with, at minimum:

```json
{
  "timestamp": "2026-07-16T09:00:01.123Z",
  "level": "Information",
  "service": "gateway",
  "traceId": "6f1c4b2a9d3e4f5a8b7c6d5e4f3a2b1c",
  "spanId": "a1b2c3d4e5f60718",
  "message": "Event evt-001 applied to account acct-123",
  "eventId": "evt-001"
}
```

Required log points: request completion (method, path, status, duration), validation rejections (Warning), downstream call failures and circuit state changes (Warning/Error), event applied (Information). Never log full payloads at Information in case metadata carries sensitive data.

### 8.3 Metrics (custom metric requirement + Prometheus bonus)

Via OpenTelemetry Metrics, exposed at `GET /metrics` (Prometheus exposition format) on both services:

- `http_server_request_duration_seconds` histogram, labeled by route + status code (latency + request count + error rate derivable)
- Gateway custom counters: `gateway_events_total{outcome="created|duplicate|rejected|downstream_unavailable"}` and `gateway_account_client_failures_total`
- Account Service custom counter: `account_transactions_applied_total{result="applied|replayed"}`

---

## 9. Deployment — Docker Compose

- One Dockerfile per service (multi-stage: `sdk:10.0` build → `aspnet:10.0` runtime).
- `docker-compose.yml`:
  - `gateway`: ports `8080:8080`; env `AccountService__BaseUrl=http://account-service:8081`; `depends_on: account-service (condition: service_healthy)`.
  - `account-service`: **no host port mapping** (internal network only); healthcheck curls `/health`.
  - SQLite files live inside each container (ephemeral by design for this assignment).
- Non-Docker fallback documented in README: two `dotnet run` commands with the base-URL env var.

---

## 10. Test specification

Runnable with `dotnet test` from the repo root. Naming: `MethodOrScenario_Condition_ExpectedResult`.

### 10.1 Account Service tests (`tests/AccountService.Tests`)

| ID | Scenario | Expectation |
|---|---|---|
| A1 | Apply CREDIT then DEBIT | Balance = credits − debits, exact decimal |
| A2 | Same `eventId` applied twice | One row; second call `200` with original; balance counted once |
| A3 | Transactions applied in shuffled timestamp order | Balance identical to sorted order |
| A4 | Missing field / amount ≤ 0 / type `credit` (wrong case) / bad timestamp | `400` ProblemDetails with field errors |
| A5 | Balance/details for unknown account | `404` |
| A6 | `/health` | `200`, database check present |

### 10.2 Gateway tests (`tests/Gateway.Tests`, WireMock.Net as fake Account Service)

| ID | Scenario | Expectation |
|---|---|---|
| G1 | Valid POST, downstream 201 | `201`; event stored `APPLIED`; downstream called once |
| G2 | Same `eventId` POSTed twice | Second → `200` original; downstream called exactly once total |
| G3 | Events POSTed out of order | `GET /events?account=` returns chronological order |
| G4 | Validation failures (per §3.1 table) | `400`, nothing persisted, downstream never called |
| G5 | Downstream returns 500 ×N | Retries observed (WireMock call count), then `503`; event `PENDING` |
| G6 | Failures exceed breaker threshold | Circuit opens; next call fails fast (no new downstream request); `503` |
| G7 | Downstream down | `GET /events/{id}` and `GET /events?account=` still `200` |
| G8 | Resubmit a `PENDING` eventId after downstream recovers | Downstream called again, event becomes `APPLIED`, `200` |
| G9 | Trace propagation | `traceparent` captured by WireMock contains the same trace-id as the Gateway's inbound request/response context |

### 10.3 Integration (`tests/Gateway.Tests`, both real services in-process)

| ID | Scenario | Expectation |
|---|---|---|
| I1 | POST event through real Gateway wired to real Account Service (two `WebApplicationFactory` hosts) | `201`; Account Service balance reflects the amount; duplicate POST leaves balance unchanged |

### 10.4 Manual end-to-end verification (post-compose)

`docker compose up --build`, then: POST event → `201`; repeat → `200`; POST out-of-order second event → listing chronological; GET balance via account endpoints → correct; `docker stop` the account-service container → POST `503` fast, GETs still `200`; confirm one trace ID spans both services' logs for a single request.

---

## 11. Out of scope (v1)

- Authentication/authorization, HTTPS termination
- Currency conversion or per-account currency enforcement
- Persistent volumes / database durability across container restarts
- Async fallback queue for `PENDING` events (documented as next step), rate limiting, contract tests, Jaeger UI — bonus items beyond the two selected
