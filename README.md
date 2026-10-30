# Tailorly Remote

*Your daily shortlist of remote roles, and the résumé to win them.*

Tailorly Remote helps developers in India find remote jobs that are actually open to them, and tailors their
résumé (.docx) to each job without breaking formatting or inventing facts.

**Status:** M3 (enrichment, fit scoring, the scheduled daily digest by email, the Shortlist). See
`docs/PRD_TailorlyRemote.md` §15 for the milestone plan.

### What works (M3)
- **Daily digest**: a dispatcher (every 5 min) claims one scheduled digest per user per local day at the user's
  schedule time (any IANA time zone, DST-safe, windows that cross midnight included, caught up for 4 hours). The run
  takes new jobs since the last digest, filters them on raw fields, **enriches** the survivors once for everyone
  (`job_enrichment`, platform cost, daily ceiling), applies the enriched filters (India eligibility vs scope, work mode,
  job type, hiring model, cities), **scores** up to 20 (`job_fit_score`, Discovery budget), then applies the salary rule,
  the minimum score and the eligibility display rule. The email lists each match with fit, reasons, eligibility,
  salary, Open in app, Apply and a followed "via {Source}" link, and carries one-click unsubscribe.
- **Never silent**: failures retry at 10/30/60 minutes inside the 4-hour window; when the AI stays down, the Discovery
  budget is used up or the enrichment ceiling is reached, jobs are ranked by title and skills instead (`degraded`), and
  the email says so.
- **Shortlist** (`/shortlist`): master-detail list with filters (eligibility, minimum score, text), "N more below your
  minimum score", keyboard shortcuts (`/`, `j`/`k`, `o`, `Esc`), job detail with fit, skills, eligibility evidence,
  description and source attribution, and **Run now** (3 a day, no email). **Digest history** at `/digests`.
- **Eligibility**: source fields first, phrase rules next, then the model; explicit restrictions always win.
- **Notification email**: a changed address gets digests only after it is confirmed (until then, the sign-in email).
- **FX**: daily Frankfurter rates for "≈ ₹" and salary comparison (static fallback).
- **Eval** (`tests/Tailorly.Eval`): labelled eligibility and fit datasets, real-model runs and a comparison report.

### What works (M2)
- **Preferences** (`/preferences`, onboarding steps 3–4): search scope, roles, cities, work modes, job types, hiring
  models, salary, match score, exclusions, digest schedule; saved with a concurrency token.
- **Companies to watch**: paste a Greenhouse/Lever/Ashby board URL; the server checks it through the platform's fixed
  public API (never the URL you typed), up to 25 per user, and polls it right away.
- **Job sourcing**: a `FetchSources` dispatcher (every 10 min) runs each enabled source when its
  `poll_interval_minutes` is due, isolated (one failure never stops the others) and recorded in `source_health`.
  Remote OK (feed) and Greenhouse boards ship enabled; Himalayas, Jobicy, WWR, Lever and Ashby ship disabled.
  Listings are normalised to plain text, de-duplicated (an employer ATS listing suppresses an aggregator copy, which
  keeps its own apply link), linked to known companies, and purged on expiry/retention.
- **Admin console** (`/admin`, Admin role): Status, Sources (terms-review guard, enable/disable, run now), Companies
  (seed list of 169 verified boards, add/verify/override) and the **Coverage** report (India-eligible jobs per day by
  source and role).

### What works (M1)
Journey A up to profile review: sign up (consent, optional invite code) → verify email (link by email) → sign in →
upload a `.docx` (every safety check of SDD §10.5) → the résumé is parsed in the background (`resume_parse`, fake or
real Anthropic provider) → review and correct the parsed profile. Plus: password reset and change, profile and time
zone, re-consent, résumé versions (download, activate, delete), confirmed skills, the monthly Usage page, and account
deletion by email confirmation with a purge job.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0 (LTS) | pinned by `global.json` |
| Node.js | 22.12+ (24 LTS recommended) | for `web/` |
| Docker | Desktop or Engine with Compose v2.24+ | integration tests (Testcontainers) and deployment |

## Repository layout

```
src/Tailorly.Core     pure rules and the five ports (IJobSource, IModelProvider, IEmailSender, IFileStore, IFxRateProvider)
src/Tailorly.Docx     all OpenXML work (from M1/M4)
src/Tailorly.App      API + Hangfire server, EF Core, Identity, feature folders
tests/Tailorly.Tests  unit (Tests.Unit) and Testcontainers integration (Tests.Integration) tests
tests/Tailorly.Eval   prompt/model evaluation harness (from M1)
web/                  React + TypeScript + Vite + Tailwind + shadcn/ui SPA
deploy/               Dockerfiles, docker-compose.yml, Caddyfile, .env.example, backup/restore scripts
docs/                 PRD, System Design, Database Design, API Design (source of truth)
design/               UI prototypes (open in a browser; #system lists tokens and components)
```

## Commands

All commands run from the repository root unless stated.

### Build and test
```bash
dotnet tool restore                      # dotnet-ef (local tool)
dotnet build -warnaserror                # also regenerates web/openapi/openapi.json
dotnet test                              # all tests (needs Docker running for Testcontainers)
dotnet test --project tests/Tailorly.Tests --filter-namespace "Tailorly.Tests.Unit*"          # unit only
dotnet test --project tests/Tailorly.Tests --filter-namespace "Tailorly.Tests.Integration*"   # integration only
dotnet run --project tests/Tailorly.Eval  # eval: validate datasets + rules baseline (see "Evaluation" below)

cd web && npm ci && npm run lint && npm run build
```

Opt-in test runs (skipped by default):
```bash
# Journey A against the real Anthropic API (one synthetic résumé, a few thousand Documents tokens)
TAILORLY_LIVE_ANTHROPIC_KEY=sk-ant-... dotnet test --project tests/Tailorly.Tests --filter-class "*LiveAnthropicTests"
# ...and re-record the fake provider's fixture from that real reply
TAILORLY_RECORD_FIXTURES=1 TAILORLY_LIVE_ANTHROPIC_KEY=sk-ant-... dotnet test --project tests/Tailorly.Tests --filter-class "*LiveAnthropicTests"
# Extractor check on your own résumés (never committed; asserts and reports counts only)
TAILORLY_LOCAL_RESUMES=/path/to/docx-folder TAILORLY_LOCAL_RESUMES_REPORT=/tmp/report.txt \
  dotnet test --project tests/Tailorly.Tests --filter-method "*Real_resumes_in_a_local_folder*"
```

Source tests never touch the network: every source `HttpClient` gets a fake handler that serves the real responses
saved in `tests/Tailorly.Tests/Fixtures/Sources` (see its README) and records every outbound call, so tests assert the
exact requests made (incremental fetches, SSRF cases with zero calls).

Trust tests worth knowing: `IsolationTests` discovers every `/api/v1` route and fails on one that is not classified
(new routes are covered automatically; jobs and digests included); `LogScanTests` runs the M1 and M3 flows with verbose
logging and fails if résumé or job text, prompts, replies, emails, passwords or tokens appear in any log event;
`DigestScheduleTests` (own database) covers claim idempotency across ticks and instances, IST/DST/midnight windows,
retries, the fallback digest, the email and one-click unsubscribe; `UploadSafetyTests` and
`DocxSafetyInspectorTests` cover macros, OLE, external links, zip bombs, nested archives, DOCTYPE, password protection
and a PDF renamed to `.docx`.

### Run everything with Docker Compose (recommended)
```bash
cp deploy/.env.example deploy/.env       # then set POSTGRES_PASSWORD (any long random string)
docker compose -f deploy/docker-compose.yml up -d --build
# https://localhost          SPA (Caddy internal CA: accept the browser warning, or trust Caddy's root cert)
# https://localhost/healthz  liveness     https://localhost/readyz  database readiness
# https://localhost/admin/jobs  Hangfire dashboard (Admin role)

docker compose -f deploy/docker-compose.yml --profile dev up -d   # + Mailpit: http://localhost:8025
docker compose -f deploy/docker-compose.yml --profile pdf up -d   # + Gotenberg (PDF export, off by default)
docker compose -f deploy/docker-compose.yml down                  # stop (add -v to wipe data volumes)
```
`up` runs the one-shot `migrate` service (`Tailorly.App --migrate`) before `app` starts. The app never migrates
itself.

### Run locally without Docker for the app
```bash
docker run -d --name tailorly-pg -p 5432:5432 -e POSTGRES_DB=tailorly -e POSTGRES_USER=tailorly \
  -e POSTGRES_PASSWORD=tailorly-dev postgres:16-alpine          # matches appsettings.Development.json
dotnet dev-certs https --trust                                  # once
dotnet run --project src/Tailorly.App -- --migrate              # migrate + seed, then exit
dotnet run --project src/Tailorly.App                           # API on https://localhost:7080
cd web && npm run dev                                           # SPA on http://localhost:5173 (proxies /api)
```

### Migrate and seed
```bash
dotnet run --project src/Tailorly.App -- --migrate     # EF migrations + Hangfire schema + idempotent seed
dotnet run --project src/Tailorly.App -- --seed        # idempotent seed only
docker compose -f deploy/docker-compose.yml run --rm migrate                        # same, in Docker
dotnet ef migrations add <Name> --project src/Tailorly.App --output-dir Infrastructure/Data/Migrations
dotnet ef migrations has-pending-model-changes --project src/Tailorly.App           # CI check
```
Seed data: `ai_task_config` (model per task), `feature_flags`, `job_sources` + `source_health` (compliance flags,
enabled state, poll intervals), the `Admin` role, and the Admin role for `Admin__BootstrapEmail` once that account
exists. Existing rows are never overwritten, so admin changes survive redeploys.

### Start sourcing (first run)
1. Sign in as an Admin (register with `Admin__BootstrapEmail`, then re-run `--seed` / the `migrate` service).
2. Admin console → **Companies** → **Load seed list** (169 boards from `watched_companies.seed.json`; verified in the
   background).
3. Admin console → **Sources** → **Run now** on Greenhouse (or wait for its 6-hour schedule). Remote OK runs on the
   next 10-minute tick.
4. Admin console → **Coverage** shows India-eligible jobs per day by source and role. Run for a week before M3.

Enabling another source needs its terms reviewed first (tick **Terms reviewed**, then enable). Keep Himalayas off
until Himalayas confirms in writing that a personal job-matching service is allowed.

### See a digest locally (M3)
1. With the stack running (`--profile dev` for Mailpit), sign up, verify, upload a résumé and save Preferences with a
   **digest time a minute or two from now** in your time zone.
2. Load jobs (Admin → Companies → Load seed list, then Sources → Run now on Greenhouse), or wait for the schedules.
3. Within 5 minutes of the digest time the dispatcher claims it; the email arrives in Mailpit (http://localhost:8025)
   and the run shows on `/digests`. **Run now** on `/shortlist` runs the same search without email.

With `Ai__Provider=Fake` every job gets the recorded enrichment and fit score, so the flow works without a key.

### Evaluation (`tests/Tailorly.Eval`)
```bash
dotnet run --project tests/Tailorly.Eval                                   # validate datasets, rules baseline only
Anthropic__ApiKey=sk-ant-... dotnet run --project tests/Tailorly.Eval -- --models claude-haiku-5-5,claude-sonnet-5-5
#   --tasks eligibility,fit · --limit N · --effort low|medium|high · --no-write
```
Datasets: `tests/Tailorly.Eval/datasets/eligibility.json` and `fit_scores.json`, each with a JSON schema next to it.
Items carry `labelSource`; the starter items are `starter (unreviewed)` until the owner labels them. The runner uses
the production prompts, input builders and validators (one repair turn) and the same hard-rule resolver as the app;
the report (`tests/Tailorly.Eval/reports/eval-*.md|json`) shows eligibility accuracy (also "unclear allowed"), critical
errors (said open when restricted), JSON validity first try / after repair, fit Spearman, MAE, ±1 agreement and
tokens. Model ids are passed with `--models`; production routing stays in `ai_task_config` and is changed only after
the owner reviews a report.

### Regenerate the TypeScript API client
```bash
dotnet build src/Tailorly.App            # writes web/openapi/openapi.json (the committed contract snapshot)
cd web && npm run gen:api                # writes web/src/api/schema.d.ts
```
CI fails if either file is out of date.

## Configuration

Everything comes from environment variables (`deploy/.env` in Docker; see `deploy/.env.example` for every key,
following PRD Appendix A). No secrets live in the repository. `Ai__Provider=Fake` works without an API key.

| Key | Purpose |
|---|---|
| `Ai__Provider` | `Fake` (recorded fixtures, no key) or `Anthropic` (official .NET SDK; startup fails without a key) |
| `Anthropic__ApiKey` | Anthropic API key (only with `Ai__Provider=Anthropic`) |
| `Ai__Effort`, `Ai__Tasks__{task}__Effort` | Reasoning effort per call (`low` default). Model ids live only in `ai_task_config` |
| `Ai__MaxConcurrentCalls` | Global limit on concurrent model calls (default 4) |
| `Budgets__DiscoveryTokens`, `Budgets__DocumentsTokens` | Monthly per-user limits (600K / 1.4M); periods are `YYYY-MM` in Asia/Kolkata |
| `App__PublicBaseUrl` | Origin used in email links (Compose sets `https://${SITE_ADDRESS}`) |
| `Security__IpHashKey` | HMAC key for consent IP hashes (`openssl rand -hex 32`) |
| `DeletionLog__Path` | Off-DB deletion log (Compose: volume `deletionlog`) |
| `Consent__Version` | Current Privacy Notice version; users re-consent when it changes |
| `RateLimits__{bucket}__PermitLimit` / `__WindowSeconds` | Rate limits (ApiDesign §6, plus `auth-register-email`, `auth-mail-email`, `auth-resend-user`) |
| `Sources__UserAgent`, `Sources__TimeoutSeconds`, `Sources__CircuitOpenMinutes` | Source HTTP: user agent, per-attempt timeout (15 s), how long a 429/403 keeps a source off (60 min, or `Retry-After`) |
| `Sources__DefaultRequestsPerMinute`, `Sources__{key}__RequestsPerMinute` | Per-source rate limit (default 60/min) |
| `Sources__Himalayas__ApiKey` | Optional Himalayas key (higher limits) |
| `Fetch__MaxPagesPerRun`, `Fetch__FirstRunBackfillDays`, `Fetch__BoardBulkDetailThreshold` | Feed page cap (50), first-run backfill (14 days), new-job count above which a board is read once with content (25) |
| `Enrichment__MaxPerRun`, `Scoring__MaxPerRun` | Jobs enriched (60) and scored (20) per run |
| `Ai__PlatformDailyEnrichmentTokenCeiling` | Platform `job_enrichment` tokens per Asia/Kolkata day (2,000,000); empty = no ceiling |
| `Digest__WindowHours`, `Digest__RetryDelayMinutes__0..2`, `Digest__MaxManualRunsPerDay`, `Digest__LookbackHours`, `Digest__EmailMaxItems` | Catch-up window (4 h), retry delays (10/30/60 min), Run now per day (3), how far back new jobs are read (72 h), jobs listed per email (20) |
| `Fx__Provider`, `Fx__StaticRates__USD_INR` | `Frankfurter` (daily public rates, no key) or `Static`; the static rate is also the fallback |

Poll intervals are not configuration: they are `job_sources.poll_interval_minutes`, edited in Admin → Sources.

To run the stack with the real model: set `Ai__Provider=Anthropic` and `Anthropic__ApiKey=...` in `deploy/.env`,
then `docker compose -f deploy/docker-compose.yml --profile dev up -d`. Verification and reset emails land in Mailpit
(http://localhost:8025).

## Source of truth

| File | Authority |
|---|---|
| `docs/PRD_TailorlyRemote.md` | **What** to build (features F1–F15, milestones, source terms) |
| `docs/SystemDesign_TailorlyRemote.md` | **How** it is structured, and what is deliberately not built (§17.3) |
| `docs/DatabaseDesign_TailorlyRemote.md` | Schema |
| `docs/ApiDesign_TailorlyRemote.md` | HTTP contract |
| `design/tailorly-app.html`, `design/tailorly-auth-B.html` | Visual reference |
| `DECISIONS.md` | Decisions made while building, and proposed doc changes |

Published prototypes: [app](https://claude.ai/artifact/4tG96dUJXB3AVjk7GLFSo3) ·
[sign-in / sign-up](https://claude.ai/artifact/VD4jtxQ5wz4VPG1JCW2BYM).
`CLAUDE.md` holds the standing instructions for Claude Code; `ClaudeCode_Prompts.md` has one prompt per milestone.

## Restore runbook note (deletion log)
After restoring a database backup, replay the purge for every user id in the deletion log
(`/data/deletion-log/deletions.log`, copied by `backup.sh`): accounts deleted after the backup was taken would
otherwise come back (SDD §13.4). The full runbook arrives with M6.

## Open items before launch
- Written reply from Himalayas on whether a per-user matching service is permitted. Keep Himalayas disabled until confirmed.
- We Work Remotely's terms could not be read automatically (403); review them in a browser before building/enabling it.
- Label the eval sets (≥ 100 eligibility, ≥ 50 fit pairs, PRD §14.3) and run the AI quality gate before launch.
- Adzuna is excluded from the MVP.
- Confirm the default assumptions in PRD §17 (INR default, unclear-eligibility handling, tones, 600K/1.4M split).
- Legal review of the privacy notice (résumé text is processed by an AI provider).
- Formal colour-contrast check of the final palette.
- Collect about 10 anonymised résumé files for the format-preservation suite (M4).
