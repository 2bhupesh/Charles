# Event Ledger — Incremental, Phase-by-Phase Plan

> **Status: complete.** All six phases delivered, **109 tests green**. See
> [README.md](README.md) to run it and [SPEC.md](SPEC.md) for the system specification.
> This document is the record of how the work was sequenced and where reality argued with
> the plan.

## Context

Take-home assignment ([Requirment.md](Requirment.md)): build an **Event Ledger** of two C#/ASP.NET Core microservices — a public **Event Gateway API** and an internal **Account Service** — handling financial transaction events with idempotency, out-of-order tolerance, distributed tracing, resiliency, tests, Docker Compose, and a README.

**Stack:** .NET 10 (LTS), ASP.NET Core Minimal APIs, EF Core + SQLite (one embedded DB per service), Polly v8 via `Microsoft.Extensions.Http.Resilience`, OpenTelemetry, xUnit + WireMock.Net.
**Scope:** all required items + two bonuses (retry with exponential backoff + jitter, Prometheus `/metrics`).

**Working style:** don't build everything at once. Build one service, write and pass its tests, commit — then move to the next phase. Each phase ended with green tests and its own commit (commit history is graded; no squashing). Only the current phase was planned in detail; later phases were detailed on arrival.

## Repository layout

```
event-ledger/
├── Requirment.md, PLAN.md, SPEC.md, README.md
├── EventLedger.slnx
├── docker-compose.yml
├── src/
│   ├── AccountService/           Domain, Data, Endpoints, Validation, Logging, Diagnostics, Dockerfile
│   └── Gateway/                  + Clients (the resilient Account Service client)
└── tests/
    ├── AccountService.Tests/     38 tests
    └── Gateway.Tests/            71 tests
```

---

## Phase 0 — Scaffold ✅

Solution, Account Service project, xUnit test project, .NET `.gitignore`; clean `dotnet build` and `dotnet test`.

**Commits:** `docs: retarget stack from .NET 8 to .NET 10 (LTS)` · `chore: scaffold solution with AccountService project and test project`

> **Deviation — .NET 8 → .NET 10.** The plan said .NET 8 (LTS). The build machine had only the .NET 10 SDK — no ASP.NET Core 8 runtime or targeting pack — so `net8.0` could not build or test at all. .NET 10 is the current LTS, so the original rationale still holds. Also pinned `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.12: EF Core 10.0.10 resolves it to 2.1.11, which carries GHSA-2m69-gcr7-jv3q.

## Phase 1 — Account Service + tests ✅

The internal service, built and tested standalone before the Gateway existed. Apply a transaction (idempotent by `eventId`, enforced by a UNIQUE index), read a balance, read account details, health. `decimal` money stored as TEXT; RFC 7807 errors; JSON logs.

**Commit:** `feat(account): apply transactions idempotently and compute balances` — 32 tests (SPEC §10.1 A1–A6).

> **Found by running it, not by the tests:** ProblemDetails carried the full `00-<trace>-<span>-00` traceparent instead of the bare 32-hex trace-id logs correlate on; and malformed JSON returned **500 in Development only**, because minimal APIs throw `BadHttpRequestException` there but return 400 elsewhere — the test host runs a non-Development environment, so the test passed. Both fixed and now covered in both environments.

## Phase 2 — Gateway core + tests ✅

`POST /events` (validate → record `PENDING` → call downstream → `APPLIED`), `GET /events/{id}`, `GET /events?account=`, health. WireMock.Net as the fake Account Service, plus a true integration test running both real services in-process.

**Commit:** `feat(gateway): record events and apply them via the Account Service` — 44 Gateway tests (G1–G4, G7, G8, I1), 76 total.

> **The concurrency test taught the design.** A request that finds an event already `PENDING` cannot distinguish "the previous apply failed" from "it is still in flight", so concurrent duplicates each retry and the same `eventId` can reach the Account Service several times. That is safe *by design* — the downstream unique index collapses them — and it is exactly why SPEC §5.5 requires that apply be idempotent. The assertion that the downstream is called exactly once was therefore wrong; the end-to-end test now asserts what matters (8 concurrent duplicates leave the balance at `150.00`) and fails if **either** service's idempotency is removed.

## Phase 3 — Resiliency + graceful degradation + tests ✅

Standard resilience handler on the Account Service client: 2s per-attempt timeout, 3 retries with exponential backoff + jitter, circuit breaker (≥50% over 10s, min 5 calls, 5s break), 10s total. Settings bound from configuration so tests trip the breaker in milliseconds.

**Commits:** `fix(account): stop a 500 under concurrent writes by disabling SQLite pooling` · `feat(gateway): add resilience pipeline and balance proxy` — 89 tests.

> **A real bug, found chasing a 1-in-10 test flake.** Concurrent same-`eventId` submissions intermittently returned 500. Not the constraint violation the code already handles: EF Core registers user-defined functions per SQLite connection and Microsoft.Data.Sqlite unregisters them when returning one to the pool, which races with active statements and throws SQLite **error 5**, escaping a catch filtered on error 19. Reproduced at ~1 in 1,600 concurrent requests; gone across 400 rounds (~12,800 requests) with pooling disabled. Committed separately — it is a defect in Phase 1 code, not part of this phase's feature.
>
> **Scope added — the balance proxy.** `GET /accounts/{id}/balance` on the Gateway (SPEC §3.6). Requirement §6 demands a clear error for balance queries when the Account Service is unreachable, but the Account Service is internal — without this endpoint a client cannot read a balance at all, so the requirement was unsatisfiable. It is the one endpoint with no local fallback.

## Phase 4 — Tracing + structured logging + tests ✅

OpenTelemetry in both services; W3C `traceparent` continued inbound and propagated over HttpClient; request-completion logging.

**Commit:** `feat(observability): propagate W3C traces and log them as top-level fields` — 101 tests.

> **Deviation — a custom `ConsoleFormatter` instead of `AddJsonConsole`.** The built-in formatter buries the trace ID in a nested `Scopes` array and has nowhere for a service name, which makes the only question worth asking — *every line from both services for this request* — awkward. `traceId`, `spanId` and `service` are now top-level fields. Framework chatter (HttpClient, Polly) quietened to Warning: at Information it added five lines of noise per request and buried the two that mattered; their Warning/Error events are required log points and were verified to survive.

## Phase 5 — Metrics + Docker Compose ✅

Prometheus `/metrics` on both services: `http_server_request_duration_seconds` by route and status, plus custom counters. Dockerfile per service (multi-stage, non-root); Compose with healthchecks and the Account Service on the internal network only.

**Commit:** `feat(observability): expose Prometheus metrics; add Dockerfiles and compose` — 109 tests.

> **Deviation — wider outcome sets than SPEC §8.3 sketched.** The write path has more distinct endings than a created/duplicate split admits: a `PENDING` event retried into `APPLIED` is neither, and a downstream 502 is a different operational problem from a 503.
>
> **⚠️ Compose is not verified.** `docker compose config` validates and the `AccountService__BaseUrl` override it depends on is verified against the real services, but `docker compose up --build` was never executed: the build machine cannot pull from `mcr.microsoft.com`, whose IPv6 addresses are unreachable from that network while its IPv4 works (`curl -6` fails, `curl -4` returns 200; NuGet and Docker Hub unaffected). **This is the one thing left to confirm on a network that can reach MCR** — it is the first thing a reviewer runs.

## Phase 6 — README ✅

Architecture, prerequisites, both run paths, tests, and the resiliency rationale. Every command in the walkthrough was executed as written before committing, and the Compose caveat is stated where a reviewer will hit it rather than buried.

**Commit:** `docs: add README`

---

## Verification

- **Every phase:** `dotnet test` green before its commit. The suite is run repeatedly (5–8 full runs) after any change that touches concurrency or timing, because both flakes found in this project appeared only under the load of parallel test assemblies.
- **Every phase also ran for real.** Tests use an in-process host; running the services over real HTTP is what caught the Development-only 500, the traceparent format, and the 2s attempt timeout actually being bound from config (the library default is 10s).
- **End-to-end, verified:** POST → `201`; resubmit → `200` with the original amount, not the resubmitted one; out-of-order listing chronological; balance exact; Account Service killed → `POST` `503` + `Retry-After` with the event `PENDING`, reads still `200`, health `Degraded`, balance proxy `503` naming the downstream; restarted → resubmit applies exactly once; one client request produces **one trace id in both services' logs**.
- **Not verified:** `docker compose up --build` (see Phase 5).

## Key decisions

1. **Idempotency in BOTH services, and neither is sufficient alone** — Gateway dedupe for clients, Account Service dedupe to make Gateway retries safe. Delegated to the database (primary key / unique index), not application logic.
2. **Retrying a POST is only safe because the downstream apply is idempotent** — resiliency and idempotency are co-designed, not independent choices.
3. **Out-of-order** handled by sort-at-query + commutative balance sum; no special write-path handling needed.
4. **`PENDING → APPLIED`** on the Gateway write path; a failed downstream call leaves a record the client can safely resubmit, rather than losing the event.
5. **`decimal` for money**, stored as TEXT (SQLite has no decimal; REAL loses precision silently) — so balances are summed in memory, exact by construction.
6. **SQLite connection pooling disabled** — the pool bought little and cost correctness under concurrency (Phase 3).
7. **The two services share no code** — small duplication (log formatter, `CREDIT`/`DEBIT` constants) keeps their builds independent. At a third service, a shared package would start to pay for itself.
