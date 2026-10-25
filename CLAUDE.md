# Tailorly Remote: instructions for Claude Code

Tailorly Remote helps developers in India find remote jobs that are actually open to them, and tailors their résumé (.docx) to each job without breaking formatting or inventing facts.
Tagline: *Your daily shortlist of remote roles, and the résumé to win them.*

## Source of truth (read before coding)
| File | Authority |
|---|---|
| `docs/PRD_TailorlyRemote.md` | **What** to build: scope, features F1–F15, rules, milestones (§15), source terms (Appendix D) |
| `docs/SystemDesign_TailorlyRemote.md` | **How** it is structured: architecture, flows, algorithms, security, deployment, ADRs, and **what we do NOT build (§17.3)** |
| `docs/DatabaseDesign_TailorlyRemote.md` | Authoritative schema (DDL, constraints, seed data, key queries) |
| `docs/ApiDesign_TailorlyRemote.md` | Authoritative HTTP contract (endpoints, types, error codes) |
| `design/tailorly-app.html`, `design/tailorly-auth-B.html` | Visual reference for every screen. `#system` in the app shows tokens and components |

If documents conflict: PRD wins on *what*; System Design wins on *how*; Database and API designs win for schema and HTTP contract. If something is truly ambiguous, choose the simplest option that satisfies the requirement, record it in `DECISIONS.md`, and continue. Ask the user only when blocked.
Do not edit the docs on your own. Propose doc changes in `DECISIONS.md`.

## Principles
1. **The simplest thing that works.** Do not add infrastructure, tables, queues or abstractions beyond the docs. SDD §17.3 lists what is deliberately not built (no microservices, broker, Redis, outbox table, operations table, idempotency-key table, budget reservations, OpenTelemetry stack, etc.).
2. **Trust comes from a few strong guarantees**, not many moving parts (see Hard rules).
3. **Interfaces only at real seams:** `IJobSource`, `IModelProvider`, `IEmailSender`, `IFileStore`, `IFxRateProvider`. Everything else is a plain class.

## Stack and layout
- Backend: ASP.NET Core on the latest .NET LTS, C#, EF Core + Npgsql, Hangfire (PostgreSQL storage), ASP.NET Identity (Guid keys, cookie auth + antiforgery), Serilog, Polly via `Microsoft.Extensions.Http.Resilience`, `DocumentFormat.OpenXml`, DiffPlex.
- DB: PostgreSQL 16+. One database. `snake_case` naming. Migrations via `Tailorly.App --migrate` (never on app startup in production).
- Frontend: React + TypeScript + Vite, Tailwind, shadcn/ui (Radix) components styled to the design tokens, TanStack Query, `dnd-kit` for the board. TypeScript API client generated from the backend OpenAPI document.
- Deploy: Docker Compose (`caddy`, `app` = API + Hangfire in one process, `postgres`; profiles `pdf` and `dev`).
- Solution: `src/Tailorly.Core` (pure rules and ports), `src/Tailorly.Docx` (all OpenXML), `src/Tailorly.App` (everything with I/O, feature folders), `tests/Tailorly.Tests`, `tests/Tailorly.Eval`, `web/`, `deploy/`.

## Hard rules (never violate; each has a test)
**Tenancy and privacy**
- Every user-owned entity has the EF global query filter. An integration test calls **every user-owned endpoint as another user** and expects 404 or an empty list.
- Never log résumé text, job descriptions, prompts, model responses, emails or tokens. Log ids, sizes, durations and error codes only.
- Résumé and tailored files live in private storage behind `IFileStore`; downloads go through authorised endpoints. Account deletion removes files and rows, then appends to the off-DB deletion log.

**AI**
- The model gets **no tools**. It returns JSON that is schema-validated, then checked by deterministic validators. Retry once with a repair prompt, then fail cleanly.
- Résumés are changed only through **edit-only JSON** applied to a copy of the original .docx. Never regenerate a document. Never let an edit add facts (numbers, employers, titles, dates, degrees, skills) not in the résumé or the user's confirmed skills.
- Treat résumé and job text as untrusted data inside delimiters. Never execute or render model output as HTML.
- Model IDs live **only** in `ai_task_config`. Prompts are files in the repo.
- Token budgets: check remaining budget, call, then atomically add actual usage. Discovery 600K and Documents 1.4M per user per month (Asia/Kolkata month). Enrichment is a platform cost.

**Sources**
- Per-source flags on `job_sources` drive behaviour (PRD §7.1a). A job from a source with `require_source_apply_url` always keeps that source's apply URL. Himalayas, Jobicy and WWR ship **disabled**. **Adzuna is not built.** LinkedIn, Naukri and Indeed are never accessed.
- Never fetch a user-supplied URL (SSRF). Board tokens must match `^[a-z0-9_-]{2,60}$` and are used only in fixed URL templates on allowlisted hosts.
- Every job returned by the API carries source attribution. Delete job text when the source's expiry passes if `purge_on_expiry`.
- One source failing (404, 429, bad schema, timeout) must never stop the others or a digest.

**Correctness**
- One scheduled digest per user per local day, enforced by the unique index and `INSERT … ON CONFLICT DO NOTHING RETURNING`.
- Duplicate in-flight work is prevented by partial unique indexes, not by extra tables.
- Secrets only from environment variables. No secrets in the repo or in logs.

## Working agreement
- Build in the order of PRD §15 (M0 → M6). **Plan first**, then implement. Finish a milestone completely (tests green, runs via Docker Compose) before starting the next.
- Write tests with the code. Before saying a milestone is done, run the whole test suite and the app, and report the results.
- External API details in the docs (URLs, fields, limits, terms) come from secondary sources. **Verify each against the provider's official documentation and real sample responses before building its adapter**, and save sample responses as test fixtures.
- Prefer the official Anthropic .NET SDK only if a stable release exists; otherwise implement the Messages API with a thin `HttpClient` wrapper behind `IModelProvider`. Always provide a `FakeModelProvider` so tests and local dev work without an API key.
- Match the design prototypes (colours, type, spacing, states). Every AI task shows queued, running, ready and failed states.
- Small, focused commits with clear messages. Keep `DECISIONS.md` (date, decision, reason) and `README.md` (how to run, test, migrate) up to date.
- At the end of each milestone, report: what was built, how to run it, test results, deviations from the docs, open questions.

## Commands
Filled in during M0: build, test, run, migrate, seed, generate API client, run eval.

## Definition of done (every milestone)
Builds with no warnings treated as errors · unit and integration tests pass · `docker compose up` runs the milestone's features · migrations apply cleanly on an empty database · no new secrets or PII in logs · docs-conformance checked against the milestone's PRD features and acceptance criteria · README and DECISIONS updated.
