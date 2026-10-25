# Tailorly Remote

*Your daily shortlist of remote roles, and the résumé to win them.*

Tailorly Remote helps developers in India find remote jobs that are actually open to them, and tailors their
résumé (.docx) to each job without breaking formatting or inventing facts.

**Status:** M0 (scaffold, platform, app shell). See `docs/PRD_TailorlyRemote.md` §15 for the milestone plan.

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
dotnet test --project tests/Tailorly.Tests --filter-namespace "Tailorly.Tests.Unit"          # unit only
dotnet test --project tests/Tailorly.Tests --filter-namespace "Tailorly.Tests.Integration"   # integration only
dotnet run --project tests/Tailorly.Eval  # evaluation harness (no eval sets until M1)

cd web && npm ci && npm run lint && npm run build
```

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
Seed data: `ai_task_config` (model per task), `feature_flags`, the `Admin` role, and the Admin role for
`Admin__BootstrapEmail` once that account exists.

### Regenerate the TypeScript API client
```bash
dotnet build src/Tailorly.App            # writes web/openapi/openapi.json (the committed contract snapshot)
cd web && npm run gen:api                # writes web/src/api/schema.d.ts
```
CI fails if either file is out of date.

## Configuration

Everything comes from environment variables (`deploy/.env` in Docker; see `deploy/.env.example` for every key,
following PRD Appendix A). No secrets live in the repository. `Ai__Provider=Fake` works without an API key.

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

## Open items before launch
- Written reply from Himalayas on whether a per-user matching service is permitted. Keep Himalayas disabled until confirmed.
- Adzuna is excluded from the MVP.
- Confirm the default assumptions in PRD §17 (INR default, unclear-eligibility handling, tones, 600K/1.4M split).
- Legal review of the privacy notice (résumé text is processed by an AI provider).
- Formal colour-contrast check of the final palette.
- Collect about 10 anonymised résumé files for the format-preservation suite (M4).
