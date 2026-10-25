# Claude Code prompts: Tailorly Remote

How to use these:
1. Create an empty repo. Add `CLAUDE.md` at the root, the four documents in `docs/`, and the two final design files in `design/`.
2. Start Claude Code in the repo root. Begin the first session in plan mode so it proposes a plan before changing files.
3. Paste **Prompt 0 (kickoff)**. Review its plan, then let it build M0.
4. After each milestone, review the report and run the app yourself. Then start a **fresh session** and paste the next milestone prompt, so context stays clean. `CLAUDE.md` is read automatically each session.
5. Run the review prompts (Section B) at the points suggested there.

Things only you can provide: an Anthropic API key (in `.env`, never committed), an email provider for production (Mailpit is used in development), about 10 real résumé `.docx` files for M4 (anonymise them), and the Himalayas reply before enabling that source.

---

## A. Build prompts

### Prompt 0: Kickoff and M0 (scaffolding)

```text
You are the lead engineer for "Tailorly Remote", a web app that helps developers in India find
remote jobs open to them and tailors their résumé (.docx) per job without breaking formatting or
inventing facts.

FIRST, read everything before writing code:
- CLAUDE.md (your standing instructions)
- docs/PRD_TailorlyRemote.md, docs/SystemDesign_TailorlyRemote.md,
  docs/DatabaseDesign_TailorlyRemote.md, docs/ApiDesign_TailorlyRemote.md
- design/tailorly-app.html and design/tailorly-auth-B.html (open them and study the tokens,
  layout and components; "#system" in the app lists every token and component)

Then reply with a short written plan: your understanding of the product in 10 lines or fewer,
the milestone order, risks you see, and any questions that BLOCK you. Do not ask about things
the docs already decide. Wait for my "go".

After my "go", implement MILESTONE M0 only (PRD section 15):
1. Repo scaffolding exactly as in PRD section 9.2: src/Tailorly.Core, src/Tailorly.Docx,
   src/Tailorly.App, tests/Tailorly.Tests, tests/Tailorly.Eval, web/, deploy/.
   Treat warnings as errors. Use the latest .NET LTS.
2. Tailorly.App: ASP.NET Core host that runs the API and the Hangfire server in one process;
   ASP.NET Identity with Guid keys and cookie auth; antiforgery (XSRF-TOKEN cookie + X-XSRF-TOKEN
   header); RFC 7807 problem details with stable error codes (ApiDesign section 10); rate-limiting
   middleware with the buckets from ApiDesign section 6; security headers (HSTS, CSP, nosniff,
   referrer policy); Serilog JSON logging with a destructuring policy that drops fields named
   resume*, description*, content*, email, prompt, response; correlation id middleware;
   GET /healthz and GET /readyz (DB only); GET /api/v1/meta; startup self-test that resolves the
   time zone "Asia/Kolkata".
3. EF Core + Npgsql with snake_case naming, the EF global query filter infrastructure
   (ICurrentUser, Guid.Empty when unauthenticated), and `Tailorly.App --migrate` that applies
   migrations and idempotent seed data. Create the first migration containing the ACCOUNT tables
   from DatabaseDesign sections 3.1 plus the ai_task_config and feature_flags seed data.
4. Hangfire with PostgreSQL storage and a dashboard at /admin/jobs behind an Admin policy.
5. Ports and fakes: IModelProvider + FakeModelProvider, IEmailSender (SMTP implementation +
   dev Mailpit), IFileStore (local-disk implementation), IFxRateProvider, IJobSource (interface only).
6. web/: React + TypeScript + Vite + Tailwind + shadcn/ui. Put the colour tokens (light and dark),
   fonts (IBM Plex Sans/Mono) and spacing from the design files into the Tailwind theme and CSS
   variables. Build the app shell (sidebar, mobile menu, theme toggle) and an empty page per route.
   Generate the TypeScript API client from the backend OpenAPI document and add a script for it.
7. deploy/: Dockerfile(s), docker-compose.yml (caddy, app, postgres; profiles "pdf" and "dev"),
   Caddyfile (TLS, reverse proxy, SPA hosting), .env.example, and a backup/restore script skeleton.
8. CI (GitHub Actions): build, unit tests, integration tests with Testcontainers, frontend lint
   and build, dependency vulnerability scan.
9. Tests: a first integration test that boots the app against Testcontainers Postgres and checks
   /healthz, /readyz and the problem-details shape; a unit test for the time-zone self-test.
10. README.md with exact commands to build, test, run, migrate, seed and regenerate the API client;
    fill the "Commands" section of CLAUDE.md; create DECISIONS.md.

Definition of done for M0: `docker compose up` starts caddy, app and postgres; the SPA loads
through caddy; /healthz and /readyz respond; CI is green; the first migration applies to an empty
database. Do NOT start M1. When finished, report: what you built, how to run it, test results,
deviations from the docs, and open questions.
```

### Prompt 1: M1 (accounts, résumé, budgets)

```text
Read CLAUDE.md and the docs, then implement MILESTONE M1 (PRD section 15) in a plan-then-build way.

Scope: F1 account and consent, F2 résumé management, F13 budgets, plus the AI runner.
1. Auth endpoints per ApiDesign 4.2 and 4.3: register (uniform response, rate limits, optional
   invite code behind the feature flag), verify email, resend, login (lockout), logout, forgot and
   reset password, change password, GET/PUT /me, consent, account deletion with email
   confirmation, the PurgeDeletedAccounts job (delete files, cascade rows, append to the
   deletion log outside the DB, write audit_log without PII). Verification/reset/deletion emails
   go through a Hangfire SendEmail job with retries.
2. Résumé: POST /resumes with every safety check in SystemDesign 10.5 (extension, magic bytes,
   5 MB, zip entry/uncompressed limits, reject macros, OLE, external relationships, password
   protection, nested archives). PDFs get UNSUPPORTED_FILE_TYPE with the friendly message.
   Store through IFileStore with the key convention in DatabaseDesign. Tailorly.Docx:
   ResumeExtractor (paragraph ids, fingerprints, sections, locked/editable flags, locked prefix,
   complex paragraphs locked) and LayoutAnalyzer (tables, columns, text boxes, images,
   header/footer text, fonts, tracked changes). A ParseResume Hangfire job runs the
   resume_parse AI task and saves resume_profiles. Endpoints per ApiDesign 4.4 including activate,
   delete (cascades), profile GET/PUT, confirmed skills. Status column + polling pattern (ApiDesign 3).
3. AI runner (SystemDesign section 9): IAiTaskRunner, PromptRegistry (prompt files in the repo),
   JSON-schema validation with one repair retry, domain validators, global concurrency semaphore,
   the BudgetService (check, call, atomic increment, bucket + period key Asia/Kolkata),
   ai_usage ledger, GET /usage. Implement IModelProvider with the official Anthropic .NET SDK
   only if a stable release exists; otherwise a thin HttpClient wrapper of the Messages API.
   Include FakeModelProvider with recorded fixtures. Model IDs only in ai_task_config.
4. Sweep job that marks stuck resources failed (TIMEOUT) after 30 minutes.
5. Frontend: sign-in/sign-up (match tailorly-auth-B.html), onboarding steps 1 and 2, the Résumé
   page, the Usage page, consent dialog, and error/empty/loading states for all of them.
6. Tests: unit tests for the budget service and docx extraction; docx tests with synthetic
   fixtures; integration tests for every endpoint above; the ALL-ENDPOINTS ISOLATION TEST
   harness (create resources as user A, call every user-owned route as user B, expect 404/empty)
   and make it automatically cover new routes later; upload-safety tests (macro file, zip bomb,
   PDF renamed to .docx); budget-exceeded paths.

Definition of done: Journey A works end to end through profile review with the fake provider and
with a real Anthropic key; isolation test passes; budget and upload-safety tests pass; no résumé
text appears in logs (add a test that scans captured logs). Do NOT start M2.
Report as described in CLAUDE.md.
```

### Prompt 2: M2 (preferences, sources, company linking)

```text
Read CLAUDE.md and the docs, then implement MILESTONE M2 (PRD section 15).

Scope: F3 preferences, F4 job sourcing, F14 compliance, F15 admin (sources/companies/coverage).
FIRST verify each source against its OFFICIAL documentation and real sample responses, and save
the samples as test fixtures. Where the docs differ from reality, follow reality and note it in
DECISIONS.md.
1. Preferences and watch list endpoints per ApiDesign 4.5 (concurrency token, validation, board
   URL allowlist, verification via the fixed board URL only, limit 25).
2. IJobSource adapters with a tolerant parser: Himalayas (search with country=IN plus
   worldwide=true; cursor pagination; map country/time-zone fields to eligibility), RemoteOK
   (skip the first terms element), Greenhouse (list without content, details only for new ids).
   Lever, Ashby, Jobicy and WWR are SHOULD: implement only if time allows after the MUSTs.
   Himalayas, Jobicy and WWR remain DISABLED by default (seed data).
3. FeedRunner (incremental fetch with stop rules and last_cursor/last_success_at in
   source_health), BoardPoller, per-source schedule (PollMinutes), resilience per SystemDesign
   section 8.1 (404/410 mark link not_found and never throw; 429/403 back off and open the circuit;
   one failure never stops the cycle), normalisation (HTML to plain text, salary parsing,
   work-mode mapping), dedupe with duplicate_of_job_id and the apply-URL rules of PRD 7.1a,
   expiry purge for sources with purge_on_expiry, retention sweeps (DatabaseDesign section 7).
4. Company linking stage 1 (PRD 7.3): parse apply URLs, lookup, watched list, seed loader for
   watched_companies.seed.json with an admin verify action. Never apply to Adzuna/Himalayas jobs.
   Slug guessing is NOT in this milestone.
5. Admin endpoints and pages for sources (TERMS_NOT_REVIEWED guard), companies, status and the
   coverage report (ApiDesign 4.11). Preferences page and the "companies to watch" UI.
6. Tests: adapter tests against saved fixtures; resilience tests (404, 429, timeout, malformed
   JSON, empty feed); dedupe tests including the Himalayas-vs-ATS rule; incremental fetch test
   (second run makes minimal requests and adds nothing); SSRF tests (no outbound call for a
   user-supplied URL); source-failure isolation test.

Definition of done: a scheduled fetch fills `jobs`; killing one source does not stop others; the
admin coverage report shows India-eligible jobs per day by source and role. After running it for
a week, review the coverage report with me before M3. Do NOT start M3.
```

### Prompt 3: M3 (enrichment, scoring, digest, shortlist)

```text
Read CLAUDE.md and the docs, then implement MILESTONE M3 (PRD section 15).

Scope: F5 enrichment, F6 scoring, F7 scheduled search and digest, the shortlist UI.
1. Enrichment on demand inside the per-user run (SystemDesign 8.5): raw-field prefilter, then
   job_enrichment get-or-create with Haiku, capped by Enrichment:MaxPerRun and the platform daily
   token ceiling. Eligibility = structured fields first, phrase rules next, then the model;
   hard rules win (SystemDesign 8.6).
2. Scoring: enriched prefilter, Scoring:MaxPerRun = 20, job_fit_score task using the cached
   profile summary and the job enrichment JSON (not the raw description), the salary rule
   exactly as in PRD 7.5, min score, eligibility display rule, ranking.
3. Digest dispatcher (every 5 minutes) with the SQL from DatabaseDesign section 7, claim by
   INSERT ... ON CONFLICT DO NOTHING RETURNING, PerUserRun, retries within the 4-hour window,
   manual "Run now" (3/day, no email), rule-based fallback when the Discovery budget is
   exhausted (digests.degraded = true), email via the SendEmail job with List-Unsubscribe and the
   signed one-click unsubscribe endpoints, attribution links in the email.
4. Endpoints per ApiDesign 4.6 (GET /jobs scoped to the user, GET /jobs/{id} visibility rule,
   digests, runs) and the Shortlist page (master-detail, keyboard shortcuts, filters, empty and
   hidden-below-min-score states) matching the prototype. Digest history page.
5. Tests: salary rule truth table; eligibility rules; claim idempotency (two dispatcher ticks and
   two instances create one digest); time-zone boundaries (IST, a DST zone, month rollover for
   budgets); fallback digest; email template renders attribution; unsubscribe flow.
6. Start Tailorly.Eval: labelled datasets (eligibility, fit scores) as JSON, a runner that calls
   the real model when a key is present, and a report comparing models. I will supply labels;
   create the schema and a small starter set.

Definition of done: a scheduled digest arrives at the user's local time, exactly once per day,
with explained matches. Do NOT start M4.
```

### Prompt 4: M4 (ATS analysis, tailoring, cover letter)

```text
Read CLAUDE.md and the docs, then implement MILESTONE M4 (PRD section 15). This is the core
differentiator, so favour correctness over speed.

Scope: F8 ATS match score and tailoring, F9 cover letter.
1. Tailorly.Core: AtsScoreCalculator with the formula and weights in PRD 8.2 (weights in config),
   pure and fully unit-tested.
2. Tailorly.Docx: EditApplier (SystemDesign 10.2: dominant run, locked prefix, never touch
   section properties, headers/footers, styles, numbering, theme; fingerprint check -> STALE_EDIT;
   OpenXmlValidator as a post-condition), DiffBuilder (DiffPlex word diff), edit validators
   (length ratio, number/entity guard against résumé text and confirmed skills, forbidden content,
   edit cap), flag detection (new_fact, length, formatting_simplified), CoverLetterWriter (.docx).
3. AI tasks: ats_analysis, resume_tailor (edit-only JSON per PRD Appendix B), cover_letter
   (tones "concise" and "confident" only, word limits per PRD 8.4), with prompts in files and
   untrusted-data delimiters.
4. Endpoints per ApiDesign 4.8: analysis, tailored-resumes, tailored-documents (decisions,
   finalize, download, content edit), cover-letters. Async status pattern, in-flight uniqueness,
   BUDGET_EXCEEDED, JOB_DESCRIPTION_REQUIRED (with paste flow), ANALYSIS_REQUIRED,
   NO_ACCEPTED_EDITS. PDF export only behind the pdf_export.enabled flag and the Gotenberg profile.
5. Frontend: the Tailoring workspace exactly as in the prototype (stepper, skill confirmation,
   edit list, diff, flags, accept/reject, ATS breakdown, finalize bar), the Cover letter tab.
6. FORMAT-PRESERVATION TESTS: with at least 10 real or realistic résumé fixtures (single and
   two-column, tables, bullets, bold lead-ins, multiple fonts, hyperlinks): apply no edits and
   sample edits, then assert styles, numbering and section properties are byte-for-byte
   unchanged except targeted paragraph text, that the output validates, and that LibreOffice (if
   available in CI) opens it. Add a hallucination test set: edits that add a number, employer or
   skill must be flagged or rejected. If I have not supplied real résumés yet, generate synthetic
   ones and tell me.
7. Extend Tailorly.Eval with a tailoring hallucination check (target: zero new facts).

Definition of done: PRD F8/F9 acceptance criteria met; format-preservation suite green; zero new
facts on the eval set. Do NOT start M5.
```

### Prompt 5: M5 (tracker, interview prep, add a job)

```text
Read CLAUDE.md and the docs, then implement MILESTONE M5 (PRD section 15).

Scope: F11 tracker, F10 interview prep, F12 bring-your-own job, usage polish.
1. user_jobs endpoints per ApiDesign 4.7 (save, list, get, patch with concurrency token, pasted
   description, delete) with the snapshot columns so cards survive job expiry; automatic move to
   "tailored" only from "saved"; free movement between statuses with status_history.
2. POST /jobs/byo (the URL is stored as text and NEVER fetched; add the test), byo_job_enrichment,
   score for the pasted job, and the "Add a job" page.
3. interview_prep task and endpoints; the Interview prep tab.
4. Tracker UI: board with drag and drop plus the status dropdown for keyboard users, list view,
   links into the tailoring workspace. Complete the Usage page (per-task breakdown from ai_usage).
5. Tests: tracker transitions and history, snapshot behaviour after the job is purged, BYO flow,
   no outbound request for a BYO URL, isolation test coverage for the new routes.

Definition of done: Journeys B, C and D from PRD section 3 work end to end. Do NOT start M6.
```

### Prompt 6: M6 (admin, hardening, launch readiness)

```text
Read CLAUDE.md and the docs, then implement MILESTONE M6 (PRD section 15).

1. Admin: status page (SystemDesign 15.2), AI config editor with the 60-second cache, flags,
   invite codes, user list (ids and counts only), suspend/unsuspend, audit logging of every admin
   action, TOTP/MFA for admin accounts if feasible with Identity.
2. Email alerts to the admin for: source failing 3x, digest failure or unsent digest > 1 hour,
   AI error rate > 10% over 15 minutes, platform token ceiling hit, disk > 80%, backup failure,
   certificate expiry.
3. Security hardening pass: review against the SystemDesign 14.2 threat table; dependency audit;
   upload-safety review; CSP without inline scripts; cookie flags; rate limits tuned; verify
   nothing sensitive is logged.
4. Operations: nightly pg_dump + documents sync scripts, restore-test script, deletion-log replay
   step, retention sweeps scheduled, Compose production profile, deployment instructions for a
   single VM (including how to run migrations first), CI deploy job.
5. Compliance checklist: attribution rendered for every source, per-source flags honoured,
   Himalayas enabled only if the admin has marked its terms reviewed.
6. Load and soak test: 100 simulated users on the same 18:00 slot; report the digest completion
   time and any bottleneck.
7. Documentation: operator runbook, restore runbook, on-call checklist, updated README.

Definition of done: the launch checklist in PRD section 17 is complete or each open item is
listed with an owner. Produce a final conformance report mapping every PRD feature F1-F15 to the
code and tests that satisfy it.
```

---

## B. Review prompts (run in a fresh session)

**Isolation audit (after M1, again after M3 and M5)**
```text
Act as a security reviewer. Enumerate every HTTP route and background job. For each user-owned
resource, confirm the EF global query filter applies, there is no IgnoreQueryFilters outside the
approved SystemQueries class, downloads check ownership, and the all-endpoints isolation test
covers the route. List any gap and fix it with tests.
```

**Log and privacy audit (after M1 and M4)**
```text
Search the codebase and run the app with verbose logging. Prove that no résumé text, job
description, prompt, model response, email address or token is written to any log, exception
message or audit_log row. Add or extend automated tests that capture logs during the main flows.
```

**Docs-conformance audit (after each milestone)**
```text
Compare the implementation with the milestone's PRD features, SystemDesign sections, the
DatabaseDesign DDL and the ApiDesign contract. List every deviation (schema, endpoint, error
code, behaviour). For each, either fix the code or add a proposed doc change to DECISIONS.md.
Run a diff between the generated OpenAPI document and ApiDesign and report mismatches.
```

**UI fidelity audit (after M3, M4, M5)**
```text
Open design/tailorly-app.html and the running web app side by side at 1360px and 390px, in light
and dark themes. List differences in colours, spacing, type, states and keyboard behaviour for
each screen built so far. Fix them. Check colour contrast of text and controls and report
any pair below WCAG AA.
```

**AI quality gate (after M3 and M4, and whenever a model or prompt changes)**
```text
Run Tailorly.Eval against the real model. Report eligibility accuracy, fit-score agreement, JSON
validity rate and the tailoring hallucination rate. Compare Haiku and Sonnet for job_enrichment
and job_fit_score. If a task misses its target in the PRD, recommend a model change in
ai_task_config and show the evidence. Do not change models without my approval.
```

---

## C. Tips
- Keep sessions focused: one milestone per session. Commit at the end of each milestone and tag it (`m0`, `m1`, …).
- If Claude Code proposes adding something listed in SDD §17.3, ask it to justify with a measured problem first.
- Keep your API key and database passwords in `.env` (ignored by git). Use a separate key with a low spend limit for development.
- When the docs and reality disagree (for example a source changes its API), have Claude Code record it in `DECISIONS.md` and update the fixtures, then update the docs yourself.
