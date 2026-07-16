# Event Ledger — Incremental, Phase-by-Phase Plan

## Context

Take-home assignment ([Requirment.md](Requirment.md)): build an **Event Ledger** of two C#/ASP.NET Core microservices — a public **Event Gateway API** and an internal **Account Service** — handling financial transaction events with idempotency, out-of-order tolerance, distributed tracing, resiliency, tests, Docker Compose, and a README.

**Stack:** .NET 8 (LTS), ASP.NET Core Minimal APIs, EF Core + SQLite (one embedded DB per service), Polly v8 via `Microsoft.Extensions.Http.Resilience`, OpenTelemetry, xUnit + WireMock.Net.
**Scope:** all required items + two cheap bonuses (retry with exponential backoff + jitter, Prometheus `/metrics`).

**Working style:** do NOT build everything at once. Build one service, write and pass its tests, commit — then move to the next phase. Each phase ends with green tests and a git commit (commit history is graded; no squashing). Only the current phase is planned in detail; later phases get detailed as we reach them.

## Repository layout (established in Phase 0, filled in over phases)

```
event-ledger/
├── Requirment.md, PLAN.md
├── EventLedger.sln
├── docker-compose.yml            (Phase 5)
├── README.md                     (Phase 6)
├── src/
│   ├── AccountService/           (Phase 1)
│   └── Gateway/                  (Phase 2)
└── tests/
    ├── AccountService.Tests/     (Phase 1)
    └── Gateway.Tests/            (Phase 2+)
```

---

## Phase 0 — Scaffold (~15 min)

1. `git init`; add a .NET `.gitignore`; first commit includes `Requirment.md`/`PLAN.md`.
2. `dotnet new sln`, `dotnet new web` for `src/AccountService` (port 8081), `dotnet new xunit` for `tests/AccountService.Tests`; add both to the solution. (Gateway projects are created in Phase 2, not now.)
3. Verify `dotnet build` and `dotnet test` run clean.
4. **Commit:** `chore: scaffold solution with AccountService project and test project`

## Phase 1 — Account Service + its tests (CURRENT DETAILED PHASE, ~60 min)

The internal service, built and tested standalone before the Gateway exists.

### Implementation
- **Model:** `AccountTransaction { Id, EventId (unique index), AccountId, Type (CREDIT/DEBIT), Amount (decimal), Currency, EventTimestamp (DateTimeOffset, stored UTC), AppliedAt }`. EF Core + SQLite file DB (`Data Source=accounts.db`; avoid `:memory:` — it dies per-connection). `decimal` for money, never `double`.
- **Endpoints (Minimal APIs):**
  - `POST /accounts/{accountId}/transactions` — validate body (eventId/type/amount>0/currency/timestamp required; type ∈ {CREDIT, DEBIT}); **idempotent by `eventId`**: unique constraint, on conflict return the existing transaction with `200` (fresh apply returns `201`). This idempotency is what will later make Gateway retries safe.
  - `GET /accounts/{accountId}/balance` — `{ accountId, balance, currency }`, balance = SUM(credits) − SUM(debits) (commutative → arrival order irrelevant).
  - `GET /accounts/{accountId}` — details + recent transactions (ordered by `eventTimestamp` desc), `404` if account has no transactions.
  - `GET /health` — status + DB connectivity check.
- Validation errors as RFC 7807 `ProblemDetails` (built into ASP.NET Core) with field messages.
- Structured JSON console logging (`AddJsonConsole`) with service name — trace enrichment comes in Phase 4.

### Tests (xUnit + `WebApplicationFactory`, isolated SQLite DB per test)
- Idempotency: same `eventId` POSTed twice → one row, balance changed once, second response `200` with original.
- Balance: mixed CREDIT/DEBIT → exact decimal net.
- Out-of-order: shuffled `eventTimestamp`s → balance correct, listing chronological.
- Validation: missing fields / amount ≤ 0 / unknown type → `400` with meaningful body.
- Health: `200` with DB status.

### Exit criteria
`dotnet test` green. **Commit:** `feat(account): apply transactions idempotently and compute balances`

---

## Later phases (outline only — each gets its own detailed mini-plan when reached, then build → test → commit)

- **Phase 2 — Gateway core + tests (~60 min):** `src/Gateway` (port 8080) with its own SQLite events DB. `POST /events` (validation, idempotency by `eventId` returning original on duplicate, save as `PENDING` → typed HttpClient call to Account Service → mark `APPLIED`), `GET /events/{id}`, `GET /events?account=` sorted by `eventTimestamp`, `/health`. Tests use WireMock.Net as a fake Account Service; one true integration test runs both real services via two `WebApplicationFactory`s. Commit.
- **Phase 3 — Resiliency + graceful degradation + tests (~35 min):** standard resilience handler on the Account Service client (2s timeout, 3 retries with exponential backoff + jitter, circuit breaker with test-configurable thresholds). Account Service down → `POST /events` returns `503` (event stays `PENDING`), read endpoints still work, balance proxy returns a clear `503`. WireMock failure tests verify retries, circuit opening, fast-fail. Commit.
- **Phase 4 — Tracing + structured logging + tests (~30 min):** OTel SDK in both services; W3C `traceparent` auto-propagates over HttpClient; JSON logs enriched with trace_id + service name. Test: assert the `traceparent` received by the mocked Account Service carries the Gateway request's trace-id. Commit.
- **Phase 5 — Metrics bonus + Docker Compose (~40 min):** OTel metrics → Prometheus `/metrics` (request count + latency histogram) on both services. Dockerfile per service; compose with healthchecks; Account Service on the internal network only. Commit. (Metrics droppable if over time.)
- **Phase 6 — README (~20 min):** architecture, prerequisites, run (compose or `dotnet run` ×2), `dotnet test`, resiliency rationale (circuit breaker + retry/backoff/jitter — safe only because the downstream apply is idempotent), trade-offs/next steps (async fallback queue, Jaeger, contract tests). Commit.

## Verification (per phase and final)

- Every phase: `dotnet test` from repo root must pass before its commit.
- Final end-to-end: `docker compose up --build`, then curl the flow — POST an event, POST it again (idempotent 200), GET listing (chronological), GET balance; stop the account-service container and confirm POST → 503 while GETs still work; check both services' logs share a trace_id for one request.

## Key decisions to carry through

1. **Idempotency in BOTH services** — Gateway dedupe for clients, Account Service dedupe to make Gateway retries safe.
2. **Out-of-order** handled by sort-at-query + commutative balance sum.
3. **`PENDING → APPLIED`** event status on the Gateway write path; failed downstream call → 503, retry-able without double-count.
4. **`decimal` for money**; SQLite file DB (not `:memory:`); timestamps as UTC `DateTimeOffset`.
