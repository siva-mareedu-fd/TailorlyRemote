# Conformance report: Tailorly Remote MVP (M0–M6)

Date: 2026-11-01. Maps every PRD feature (F1–F15) to the code and the tests that satisfy it, then records the M6
security review (SDD §14.2), the source-compliance checklist (PRD F14, §7.1a, Appendix D), the load test and the
launch checklist (PRD §17). Deviations from the docs are proposals in `DECISIONS.md` (not applied to `docs/`).

Test suite at this commit: see "Test results" at the end. Paths are relative to the repository root; `App/` means
`src/Tailorly.App/`, `Tests/` means `tests/Tailorly.Tests/`.

## 1. Features

| Feature | Code | Tests | Status |
|---|---|---|---|
| **F1 Account & access**: signup with consent, verification (24 h), login, logout, reset, rate limits, invite-code flag, profile, consent versioning, account deletion with purge and audit | `App/Features/Accounts/` (`AuthEndpoints`, `MeEndpoints`, `AccountEmails`, `AccountDeletion`, `TwoFactorEndpoints`), `App/Api/AuthPolicies.cs`, `App/Api/RateLimiting.cs`; web `pages/auth/`, `pages/AccountPage.tsx` | `Integration/AuthTests` (uniform register, 6th signup/hour → 429, lockout, reset), `AccountTests` (consent, re-consent, deletion: files gone, audit, deletion log), `JourneyATests`, `TwoFactorTests`, `AdminConsoleTests` (invite flag and codes) | Done. Two-step sign-in (M6) added for admins. |
| **F2 Résumé management**: .docx ≤ 5 MB, magic bytes/zip/macro/password checks, PDF message, private storage, parse once, profile review/edit, versions, layout findings, delete | `App/Features/Resumes/`, `src/Tailorly.Docx/` (`DocxSafetyInspector`, `ResumeExtractor`, `LayoutAnalyzer`), `App/Infrastructure/Storage/LocalFileStore.cs`, `App/Ai/Tasks/ResumeParseTask.cs`; web `pages/ResumePage.tsx`, `features/resume/` | `Integration/ResumeTests`, `UploadSafetyTests`, `Unit/DocxSafetyInspectorTests`, `Unit/ResumeExtractorTests`, `Unit/Docx/FormatPreservationTests` (no-op round trip), `LiveAnthropicTests` (opt-in) | Done. |
| **F3 Job preferences**: every field of the table, concurrency token, schedule | `App/Features/Preferences/`; web `pages/PreferencesPage.tsx`, `features/preferences/` | `Integration/PreferencesTests` | Done. |
| **F4 Job sourcing**: central scheduled fetch, adapters, normalisation, dedupe, attribution, source health, isolation | `App/Features/Sources/` (adapters RemoteOK, Himalayas, Greenhouse, Lever, Ashby; `FeedRunner`, `BoardPoller`, `JobIngestor`, `CompanyLinker`, `SeedLoader`), `src/Tailorly.Core/Jobs/` | `Integration/Sources/FeedFetchTests`, `BoardAndDedupeTests` (isolation, 404/429/timeouts, SSRF cases with zero calls), `Unit/Sources/*` (fixtures from real responses) | Done. Jobicy/WWR adapters are M7 (SHOULD); Adzuna not built (by decision). |
| **F5 Eligibility & enrichment**: one shared Haiku call per job on demand, platform cost, cached, ceiling | `App/Features/Digests/EnrichmentService.cs` (single-flight per job, M6), `App/Ai/Tasks/JobEnrichmentTask.cs`, `src/Tailorly.Core/Matching/EligibilityResolver.cs`, `Core/Jobs/EligibilityRules.cs` | `Integration/Digests/RunAndShortlistTests` (shared, never twice), `EnrichmentConcurrencyTests` (M6), `Unit/Matching/EligibilityAndPrefilterTests`, `Tailorly.Eval` (eligibility set) | Done. Eval set labelling is a launch item. |
| **F6 Job Fit Score**: per user, 1–10, reasons, matched/missing skills, salary rule after scoring | `App/Features/Digests/ScoringService.cs`, `App/Ai/Tasks/JobFitScoreTask.cs`, `Core/Matching/SalaryRule.cs`, `Ranking.cs`, `Prefilters.cs` | `RunAndShortlistTests`, `Unit/Matching/SalaryRuleTests`, `DigestUnitTests`, `Tailorly.Eval` (fit set) | Done. |
| **F7 Scheduled search & digest**: 5-min dispatcher, idempotent claim, run, email with attribution and links, history, Run now (3/day), budget fallback | `App/Features/Digests/` (`DigestDispatcherJob`, `PerUserRunJob`, `DigestEmailJob`, `DigestEmailTemplate`, `EmailLinkEndpoints`), `App/Infrastructure/Data/SystemQueries.cs` (claim = insert); web `features/shortlist/`, `pages/DigestsPage.tsx` | `Integration/Digests/DigestScheduleTests` (double tick, two instances, IST/DST/midnight, retries, fallback, email, unsubscribe), `RunAndShortlistTests`, `Load/DigestLoadTests` (100 users, second tick claims 0) | Done. |
| **F8 ATS Match Score & tailoring**: 0–100 "estimated", before/after, edit-only JSON on a copy, diff, accept/reject, download, no new facts | `src/Tailorly.Core/Ats/`, `src/Tailorly.Docx/Tailoring/` (`EditApplier`, `EditValidator`, `FactGuard`, `DiffBuilder`), `App/Features/Tailoring/`, `App/Ai/Tasks/AtsAnalysisTask.cs`, `ResumeTailorTask.cs`; web `features/tailoring/` | `Unit/Ats/AtsScoreCalculatorTests`, `Unit/Docx/FormatPreservationTests` (12 résumé layouts), `HallucinationGuardTests`, `Integration/Tailoring/TailoringFlowTests`, `Integration/Docx/LibreOfficeOpensTests`, `Tailorly.Eval` (tailoring hallucination check: 0 adversarial edits pass) | Done. |
| **F9 Cover letter**: Concise ≤ 180 / Confident ≤ 250 words, editable, .docx | `App/Ai/Tasks/CoverLetterTask.cs`, `src/Tailorly.Docx/CoverLetterWriter.cs`, `App/Features/Tailoring/TailoredDocuments.cs`; web `features/tailoring/CoverLetterTab.tsx` | `TailoringFlowTests`, `Unit/Docx/DiffAndCoverLetterTests` | Done. |
| **F10 Interview prep**: topics, behavioural questions with talking points from the résumé only, gaps, questions; per job, regenerable | `App/Features/Tailoring/InterviewPrepEndpoints.cs`, `App/Ai/Tasks/InterviewPrepTask.cs`; web `features/tailoring/InterviewPrepTab.tsx` | `Integration/Tracker/InterviewPrepTests`, `JourneyBcdTests` (Journey D) | Done. No labelled eval set yet (launch item). |
| **F11 Application tracker**: seven columns, cards, auto-move to Tailored only from Saved, free moves, history, Apply opens the source link and asks "Did you apply?" | `App/Features/Tracker/UserJobEndpoints.cs`; web `features/tracker/TrackerPage.tsx`, `useApplyPrompt.tsx` | `Integration/Tracker/TrackerTests`, `JourneyBcdTests` (Journey B) | Done. |
| **F12 Bring-your-own job**: pasted text required, optional URL (never fetched), enrichment charged to Documents, scoring, tailoring | `App/Features/Tracker/ByoJobs.cs`; web `features/tracker/AddJobPage.tsx` | `Integration/Tracker/ByoJobTests` (no outbound request for the link), `JourneyBcdTests` (Journey C) | Done. |
| **F13 Budgets & routing**: Discovery 600K / Documents 1.4M per IST month, meter, 80 % warning, degradation, model per task in DB | `App/Ai/BudgetService.cs`, `TaskConfigCache.cs`, `AiTaskRunner.cs`, `UsageLedger.cs`, `src/Tailorly.Core/Budgets/BudgetPolicy.cs`; `App/Features/Usage/`; web `pages/UsagePage.tsx` | `Unit/BudgetPolicyTests`, `Integration/SkillsAndUsageTests`, `AiRunnerTests`, `AdminConsoleTests` (AI config edit reaches the next call) | Done. |
| **F14 Source compliance**: "via {Source}" with followed link, per-source flags, terms review | `job_sources` flags (seed in `App/Infrastructure/Data/Seeder.cs`), `App/Features/Jobs/JobContracts.cs` (`SourceInfoDto`), `DigestEmailTemplate`, admin terms guard in `AdminEndpoints`; web `ShortlistPage.tsx`, `JobDetailPane.tsx`, `TrackerPage.tsx` | `Integration/ComplianceTests` (M6: every source's attribution and link, flags, Himalayas guard), `AdminSourcesTests`, `DigestScheduleTests` (email "via" link), `BoardAndDedupeTests` (`The_ats_record_suppresses_the_himalayas_copy_and_each_keeps_its_own_apply_link`, `Ats_links_are_discovered_from_feed_apply_urls_but_never_from_himalayas`) | Done; see §3. |
| **F15 Admin**: source health, company table, AI config, per-user usage, flags (invite code), run fetch now | `App/Features/Admin/` (`AdminEndpoints`, `AdminReportsEndpoints`, `AdminSettingsEndpoints`, `AdminUsersEndpoints`, `AdminAlerts`), `App/Infrastructure/Jobs/HangfireSetup.cs` (dashboard, its audit); web `pages/AdminPage.tsx`, `features/admin/AdminSettingsTabs.tsx` | `Integration/AdminSourcesTests`, `AdminConsoleTests`, `AdminAlertsTests`, `TwoFactorTests` (admin MFA), `IsolationTests` (403 for non-admins on every admin route) | Done (M6). |

Cross-cutting guarantees (CLAUDE.md hard rules):

| Rule | Where | Test |
|---|---|---|
| Every user-owned entity filtered; every endpoint tried as another user | `AppDbContext` global filters, `SystemQueries` (the only `IgnoreQueryFilters`) | `IsolationTests` (route discovery; unclassified routes fail), `Unit/TenancyGuardTests` (static guards) |
| No résumé/job text, prompts, replies, emails or tokens in logs | `App/Infrastructure/Logging/` | `LogScanTests` (M1–M6 flows at Verbose), `HangfirePrivacyTests`, `Unit/PiiLoggingTests` |
| Model gets no tools; schema + deterministic validators; one repair; edit-only | `App/Ai/AiTaskRunner.cs`, `src/Tailorly.Docx/Tailoring/` | `AiRunnerTests`, `HallucinationGuardTests` |
| Model ids only in `ai_task_config`; prompts are files | `App/Ai/Prompts/`, `TaskConfigCache` | `AiUnitTests` |
| One scheduled digest per user per local day | unique index + `INSERT … ON CONFLICT DO NOTHING RETURNING` | `DigestScheduleTests`, `DigestLoadTests` |
| No user-supplied URL fetched; board tokens validated | `Core/Jobs/AtsUrl.cs`, `SourceHttp` | `BoardAndDedupeTests`, `ByoJobTests` |
| One failing source never stops the others | `FeedRunner`, `SourceJobs` | `FeedFetchTests`, `BoardAndDedupeTests` |
| Secrets only from environment | `deploy/.env` (gitignored), `.env.example` | CI secret-free build; review |

## 2. Security review (SDD §14.2 threat table, M6)

Reviewed 2026-11-01 against the code, the running stack (`docker compose up`, Caddy in front) and the tests.

| Asset / threat | Control in place | Evidence | Result |
|---|---|---|---|
| Résumés: IDOR / cross-user access | Global query filters; resource checks on downloads; random storage keys (`{userId}/…/{guid}`); no public URLs | `IsolationTests` (every route as another user, owner gets 2xx with the same ids), `TenancyGuardTests` | OK |
| Résumés: malicious .docx | 5 MB / 25 MB / 500 entries; macros, OLE, ActiveX, nested archives, external relationships, DOCTYPE, encryption and editing restrictions rejected; parsing under a 2-slot gate with a 60 s timeout; request body limit 5 MB on upload, 1 MB elsewhere | `DocxSafetyInspector`, `DocxLimits`, `DocxWork`; `UploadSafetyTests`, `DocxSafetyInspectorTests` | OK (upload-safety review: every SDD §10.5 limit present) |
| AI pipeline: prompt injection | Untrusted data in delimiters (delimiter text neutralised), no tools, JSON schema + deterministic validators, output never rendered as HTML | `AiTaskRunner.BuildUserContent`; `AiRunnerTests`; web never uses `dangerouslySetInnerHTML` (grep: none) | OK |
| Fabricated résumé content | Edit-only contract, fact guard, user approval per edit, eval gate | `HallucinationGuardTests`, `Tailorly.Eval` tailoring check | OK |
| Budget abuse | Verified email before AI, signup limits, per-bucket caps, `ai-start` 30/h, in-flight unique indexes, platform ceiling, **alert on ceiling** (M6) | `SkillsAndUsageTests`, `AdminAlertsTests` | OK |
| Outbound SSRF | No user URL is fetched; fixed hosts per source; token regex; cross-host redirects off; certificate check connects only to the configured host | `BoardAndDedupeTests`, `ByoJobTests` | OK |
| UI: XSS via job text | Plain text stored; React escaping; strict CSP `default-src 'self'`, no inline scripts or styles (built `index.html`: one module script, one stylesheet) | Live headers checked through Caddy; `ApiPlatformTests` (CSP and security headers) | OK. Exception: the admin-only Hangfire dashboard (proposal 4). |
| UI: open redirect / malicious links | `http/https` apply URLs only; `rel="noopener noreferrer"`; links built from fixed paths, never request headers | `JobNormalizer.IsHttpUrl`, `AppLinks` | OK |
| Accounts: credential stuffing, enumeration | `auth-login` 10/min/IP, lockout 5 → 15 min (TOTP failures count too), uniform register/forgot answers, `MFA_REQUIRED` only after a right password; **M6: anonymous token endpoints (verify email, reset password, confirm deletion, notification email) now rate-limited per IP** | `AuthTests`, `TwoFactorTests` | Fixed one gap |
| Email: header injection / phishing | Templates only; no user-controlled headers; signed one-click unsubscribe; SPF/DKIM/DMARC is an operator task | `DigestScheduleTests` | OK (DNS records: launch item) |
| Secrets | Env only; `.env` mode 600; nothing in logs (secrets blocked by name, incl. two-step secrets in M6) | `LogScanTests`, `TwoFactorTests` | OK |
| Supply chain | Dependabot (NuGet, npm, Docker, Actions); `dotnet list package --vulnerable` and `npm audit --audit-level=high` in CI; NuGet audit with warnings as errors | 2026-11-01: NuGet 0 vulnerable; npm production deps 0; dev-only: 1 moderate (minimatch via eslint, not shipped; the suggested fix downgrades typescript-eslint) | Accepted (dev-only) |
| Admin: privilege abuse | Single Admin role; **two-step sign-in required in production** (M6); every admin API call **and every job-dashboard POST** audited (M6); user list shows ids and counts only | `TwoFactorTests`, `AdminConsoleTests`, `IsolationTests` | Fixed two gaps |

HTTP hardening (SDD §14.4), checked live through Caddy: HSTS (1 year), CSP as above, `X-Content-Type-Options`,
`Referrer-Policy: strict-origin-when-cross-origin`, minimal `Permissions-Policy`, `X-Frame-Options: DENY`,
`Cache-Control: no-store` on the API, HTTP → HTTPS 308. Cookies: `__Host-tailorly` (auth: Secure, HttpOnly,
SameSite=Lax), `__Host-tailorly-af` (Secure, HttpOnly, SameSite=Strict), `XSRF-TOKEN` (Secure, SameSite=Strict, readable
by the SPA by design). Same-origin only (no CORS).

Rate limits reviewed: `auth-login` 10/min/IP (now also the anonymous token endpoints and two-step enable/disable),
`auth-register` 5/h/IP + per email, `auth-mail` 5/h, `ai-start` 30/h/user, `manual-run` 3/day, `download` 60/min,
`admin` 60/min, `api-default` 120/min. One-click unsubscribe stays unthrottled (mail providers call it in bulk).

## 3. Source compliance checklist (PRD F14, §7.1a, Appendix D)

| Source | Enabled | Terms reviewed | Attribution rendered | Flags honoured |
|---|---|---|---|---|
| Remote OK | yes | 2026-10-25 | "via Remote OK" on rows, detail ("Source: Remote OK" + followed link), email items, tracker cards | `require_source_apply_url` (its own listing link stays the apply link) |
| Greenhouse boards | yes | 2026-10-25 | "via Greenhouse board"; links to the board listing | public board API only; tokens validated |
| Himalayas | **no** | not reviewed | built and tested (attribution text and link) | `require_source_apply_url`, `forbid_third_party_resolution`, `purge_on_expiry`; enabling refused (`TERMS_NOT_REVIEWED`) until an admin stamps the review, which needs Himalayas' written confirmation (launch item) |
| Lever, Ashby boards | no (M7 SHOULD) | not reviewed | built (attribution like Greenhouse) | as Greenhouse |
| Jobicy, We Work Remotely | no; no adapter | not reviewed | flags seeded | enabling refused until reviewed; adapters are M7 |
| Adzuna | not built | — | — | excluded from the MVP |
| LinkedIn, Naukri, Indeed | never accessed | — | — | users paste jobs (F12) |

Verified by `ComplianceTests` (every source's jobs carry the configured name, attribution text and URL, and the listing
link; the flag matrix equals the reviewed terms; Himalayas cannot be enabled unreviewed), `DigestScheduleTests` (the
email's followed "via" link) and `BoardAndDedupeTests` (an aggregator copy keeps its own apply URL when an ATS listing
suppresses it).

## 4. Load and soak test (SDD §16)

See `tests/load-reports/` and §4 of the operator runbook.

Run: `tests/load-reports/digest-load-20261101-1647.md`, 2026-11-01.

100 users on the same slot (21:56 IST), 150 new jobs, model latency 2000 ms ±25 %, Hangfire workers 4, AI concurrency 4 (production defaults: 4 and 4).

| Measure | Value |
|---|---|
| **All digests done (dispatch → last email)** | **18:13** |
| First digest done | 18:08 |
| Digests ready / failed / degraded / retried | 100 / 0 / 0 / 0 |
| Digest emails sent | 100 |
| Wait in queue before the run started (p50 / p95 / max) | 561s / 1011s / 1053s |
| Run time per user (p50 / p95 / max) | 40s / 72s / 74s |
| Model calls (job_enrichment 150, job_fit_score 2000) | 2150 |
| Jobs scored (sum over users) / enrichments created | 2000 / 150 |
| Peak model calls in flight | 4 (limit 4) |
| Model-bound floor (Σ latency ÷ AI concurrency) | 17:57 (98 % of the total) |
| Second dispatcher tick in the same slot | 0 claimed |
| Test process peak working set | 210 MB |
| Seeding (users, résumés, jobs; not part of the slot) | 00:57 |

## Analysis

- **Completion**: all 100 scheduled digests were built and emailed **18 min 13 s** after the dispatcher tick, with no
  failure, no rule-based fallback and no retry. A second tick in the same slot claimed nothing.
- **Bottleneck: the AI concurrency gate** (`Ai:MaxConcurrentCalls` = 4). The model-bound floor is 98 % of the total
  and calls in flight never exceeded 4. Postgres, Hangfire and memory (210 MB peak for the whole test host) were not
  limiting. Per user the slot costs about 21.5 calls × 2 s ÷ 4 ≈ 10.75 s of gate time, so the total grows linearly
  with users on the same slot, as SDD §16.2 estimates (≈ 17 min for 100 users).
- **Ceiling**: runs wait in the queue for up to the whole slot (p95 17 min here). The stuck-work sweep fails a claimed
  run that has not started within **30 minutes** (`TIMEOUT`), so about **165 users on one slot** at 2 s per call is the
  limit before digests fail. Levers, in order: raise `Ai:MaxConcurrentCalls` with `Hangfire:WorkerCount` (as far as the
  Anthropic rate-limit tier allows; 8/8 halves the time), spread schedule times, or start runs earlier than the delivery
  time (SDD §16.2).
- **Email timing**: digest emails share the single Hangfire queue and are enqueued after the 100 runs, so the first
  email went out after 18 min although its run finished within the first minute. Within the slot this is acceptable;
  DECISIONS proposal 78 (an `email` queue served first) would send each digest seconds after its run.
- **Fixed during M6**: shared enrichment ran once per job (150 calls for 150 jobs); before the fix, runs that started
  together enriched each job once per worker. `ai_usage.latency_ms` no longer includes the wait for the gate (this run
  predates that fix; it does not change the timings above).

## 5. Launch checklist (PRD §17)

Owners: **Siva** (product owner and operator), **Counsel** (legal review), **Claude Code** (engineering, done in the
repository).

| Item | State | Owner | Next step |
|---|---|---|---|
| Written confirmation from Himalayas that a personal job-matching/alert service is permitted | **Open** | Siva | Follow up the email already sent; until then Himalayas stays disabled (guarded in code). |
| Terms confirmed for any other source whose terms are unclear (We Work Remotely: page blocked automated reading) | **Open** (WWR disabled, no adapter) | Siva | Read WWR's terms in a browser before M7 builds its adapter. |
| Terms reviewed for every enabled source and attribution rendered | **Done** | Claude Code | Remote OK and Greenhouse reviewed 2026-10-25; `ComplianceTests`. Re-read quarterly (operator runbook §2). |
| Eval thresholds met (eligibility ≥ 90 %, fit Spearman, JSON validity ≥ 99 %, hallucination 0) | **Open** | Siva (labels), then Claude Code (run) | Label the eval sets (≥ 100 eligibility, ≥ 50 fit; tailoring items are `starter (unreviewed)`), then `dotnet run --project tests/Tailorly.Eval -- --models …` and record the report. The deterministic hallucination gate passes in CI. |
| Format-preservation suite green | **Done** | Claude Code | 12 layouts + LibreOffice check in CI. Add anonymised real résumés when available. |
| Cross-user isolation tests green | **Done** | Claude Code | `IsolationTests` runs every route as another user. |
| Backups restore-tested | **Done locally; open on production** | Siva | Scripts verified end to end (restore runbook §5). Run `restore-test.sh` on the production VM after the first nightly backup and log it. |
| Privacy notice + legal review (DPDP Act, AI-provider processing) | **Open** | Counsel, Siva | Review the notice (`web/src/pages/auth/LegalPages.tsx`, consent version `2026-10-1`) before public launch. |
| Alerts configured | **Done in code; open on production** | Siva | Set `Alerts__To` (or verify the admin email), `BACKUP_*`, SMTP; check one alert arrives (on-call checklist). |
| Admin account secured (MFA) | **Done** | Siva | Production requires two-step sign-in for admins; set it up at first sign-in (operator runbook §1.6). |
| Email deliverability (SPF, DKIM, DMARC on the sending domain) | **Open** | Siva | DNS records at the email provider (SDD §20 T6). |
| Production VM, DNS, registry login, CI `production` environment | **Open** | Siva | Operator runbook §1. |

## 6. Test results

Run on 2026-11-01 on the final M6 code (Windows 11, Docker Desktop, PostgreSQL 16 via Testcontainers):

| Check | Result |
|---|---|
| `dotnet build -warnaserror` (also regenerates `web/openapi/openapi.json`) | 0 warnings, 0 errors; snapshot and generated client in sync |
| `dotnet ef migrations has-pending-model-changes` | no changes (M6 adds no migration) |
| `dotnet test` (unit + integration) | **758 tests: 752 passed, 0 failed, 6 skipped** (opt-in: 3 local-résumé runs, 2 live Anthropic runs, the load test) |
| `dotnet run --project tests/Tailorly.Eval -- --no-write` | datasets valid; tailoring guard: 40 adversarial edits, **0 slipped**, 0 false positives on 12 benign |
| `npm run lint`, `prettier --check`, `npm run build` | clean |
| `dotnet list package --vulnerable`, `npm audit --omit=dev` | 0 / 0 |
| `shellcheck -x -S warning deploy/*.sh`, actionlint on `ci.yml` | clean |
| `docker compose --profile dev up -d --build` | all services healthy; `/readyz` 200; headers and cookies as in §2 |
| `tests/ops/backup-restore.sh` against the stack | `RESULT ok`: 45 tables match, 15 documents, an account deleted after the backup purged again by the replay |
| `DigestLoadTests` (100 users, 2 s per call) | all 100 digests emailed in 18 min 13 s, 0 failed (§4) |
| In-browser check (Edge, the Compose stack): sign-in with a two-step code, the 8 admin tabs, invite code, Account card, 390 px | works; no page errors, no CSP violations; Users tab shows no email |
