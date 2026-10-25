# PRD — Tailorly Remote

**Product:** Tailorly Remote — *Your daily shortlist of remote roles, and the résumé to win them.*
**Version:** 1.5 (build-ready; detailed schema and API contract now live in `DatabaseDesign_TailorlyRemote.md` and `ApiDesign_TailorlyRemote.md`) **Date:** 2026-10-24 **Audience:** Claude Code / engineering team
**Code namespace / repo prefix:** `Tailorly` (e.g., `Tailorly.Api`)

---

## 0. How to use this document (instructions for the builder)

- Build in the order of **Section 15 (Milestones)**. Each milestone must compile, pass tests and run via Docker Compose before the next begins.
- Priorities: **MUST** (MVP blocker), **SHOULD** (build if time allows, architecture must not block it), **COULD** (later).
- **External API details (URLs, field names, rate limits, terms) in this document are written from memory/secondary sources. Verify each against the provider's official documentation before implementing its adapter.** Build adapters with tolerant parsers (unknown fields ignored, missing fields null) and check in real sample responses as test fixtures.
- Never put secrets in code. All secrets via environment variables (Appendix A).
- Keep every external dependency (AI, email, file storage, job sources) behind an interface so it can be swapped.
- Where this document says "configurable", store it in the database or `appsettings`, not hard-coded.
- **Engineering principle: the simplest thing that works.** Do not add infrastructure, tables, queues or abstractions beyond what this PRD and the System Design Document require. `SystemDesign_TailorlyRemote.md` §17.3 lists what is deliberately *not* built. If a simpler approach satisfies a requirement, use it.
- Where `SystemDesign_TailorlyRemote.md` and this PRD differ on *how* to build something, the System Design Document wins; this PRD wins on *what* to build.

---

## 1. Product overview

### 1.1 Problem
Job seekers in India who want **global remote** roles (and optionally India roles) waste hours searching many sites, judging whether a "remote" job actually accepts candidates from India, and rewriting their résumé for each posting so it passes Applicant Tracking Systems (ATS).

### 1.2 Solution
A web app where the user uploads a résumé once, sets preferences, and receives a **scheduled daily digest** of newly posted, India-eligible jobs ranked by fit with an explanation. For any job, the user can generate an **ATS-optimised, format-preserving tailored résumé** and a **cover letter**, get **interview prep**, and track the application on a **Kanban board**.

### 1.3 Differentiators (protect these in every trade-off)
1. **ATS Match Score + tailoring that never breaks résumé formatting and never fabricates facts.**
2. **India-eligibility classification** of remote jobs (most "remote" jobs are not open to India).
3. Explained matching (why this job fits, what is missing).

### 1.4 Target users
Software/IT/tech professionals based in India seeking global remote or India roles. Not targeting US-resident job seekers.

### 1.5 Goals (first 90 days)
- 10 active users; 5 regularly using tailoring.
- Digest delivered on schedule ≥ 99% of days.
- ≥ 80% of tailored résumés accepted by users with ≤ 20% of edits rejected (proxy for quality).
- Zero cross-user data exposure incidents.

### 1.6 Non-goals (MVP)
Auto-applying to jobs; scraping LinkedIn/Naukri or any site whose terms forbid automated access; mobile apps; multi-résumé per user; non-English résumés; PDF résumé *input*; WhatsApp/Telegram notifications; Gmail/Outlook email parsing.

---

## 2. Key decisions log

| # | Decision | Notes |
|---|---|---|
| D1 | No auto-apply. "Apply" opens the job's original apply link; user marks Applied manually. | Third parties cannot submit via ATS APIs; scraping/automation violates LinkedIn/Naukri terms. |
| D2 | Sourcing only from sources that allow API/feed access (see §7.1) plus **user-pasted jobs**. | LinkedIn/Naukri jobs enter via user paste. No scraping, no extension scraping LinkedIn. |
| D3 | Scope: India-based candidates; search scope preference = *India only / India + global remote / global remote only*. | |
| D4 | Résumé input **.docx only**; PDF export is SHOULD. | Format preservation requires editing the original OOXML. |
| D5 | Tailoring = JSON edits applied to a copy of the original docx via OpenXML SDK. LLM never regenerates the document. | |
| D6 | Company → ATS resolution pipeline with cache (§7.3), **staged**: apply-URL parsing + lookup + watched list in MVP; slug guessing in M7. | No public "list all companies" API exists for Greenhouse/Lever/Ashby. Guessing is the most complex part; build it once real data shows how many jobs remain with limited descriptions. |
| D7 | Token cap **2,000,000 tokens / user / month**, split into two budgets: **Discovery 600K** (Haiku) and **Documents 1.4M** (Sonnet). Resets 1st of each month 00:00 IST. | Prevents background scoring from starving the core tailoring features. |
| D8 | Cover-letter tones in MVP: **Concise** and **Confident** (user-selectable per generation). Default: Concise. Tone list is config-driven so Formal/Warm can be added later. | |
| D9 | Self-signup with email verification, signup rate-limiting, and an **invite-code feature flag** (off by default). | |
| D10 | Application status updates are **manual** in MVP. | |
| D11 | Encryption hardening (application-level) is Phase 2. Phase 1 still requires private storage, ownership checks, HTTPS, consent, real deletion. | |
| D12 | Model routing is **config-driven per task**; no model IDs hard-coded in business logic. | |

---

## 3. Personas & primary journeys

**Persona: Ravi, 29, backend engineer, Hyderabad.** Wants a US/EU-company remote role payable in USD, comfortable with 3–4 hours of US-morning overlap.

Journey A — Onboarding: sign up → verify email → accept consent → upload .docx → review parsed profile → set preferences → pick schedule (default 18:00 IST).
Journey B — Daily: receives email digest at 18:00 IST → opens a job → sees score, eligibility, salary label → clicks **Tailor** → reviews diff + ATS before/after → downloads docx + cover letter → clicks **Apply** (opens employer page) → marks Applied.
Journey C — Bring your own job: finds a role on LinkedIn manually → pastes description (+URL) → same scoring/tailoring flow.
Journey D — Prepare: opens a job in Interviewing → generates interview prep.

---

## 4. Functional requirements

### F1. Account & access (MUST)
- Email+password signup (ASP.NET Core Identity), email verification link (expires 24h), login, logout, password reset.
- Signup protections: per-IP and per-email rate limits, email verification required before any AI feature, optional invite-code requirement controlled by feature flag `Signup:RequireInviteCode`.
- Profile: display name, IANA time zone (default `Asia/Kolkata`), notification email.
- Consent: checkbox at signup/first upload with versioned privacy notice; store `consent_version`, `consented_at`, IP hash. Privacy notice must state that résumé and job text are sent to a third-party AI provider (Anthropic) for processing.
- **Delete account**: hard-deletes all user data (rows, files, derived documents) via background job; confirmation by email; audit-log entry (without PII).
- *Acceptance:* unverified user cannot upload or run AI features; deleted user's files are absent from storage within 24h; 6th signup attempt/hour/IP is rejected with 429.

### F2. Résumé management (MUST)
- Upload `.docx` ≤ 5 MB. Validate magic bytes/zip structure, reject macros (`.docm`), reject password-protected files. A `.pdf` upload returns a friendly message: "Please upload the Word (.docx) version."
- Store original privately (never public URLs); downloads go through an authorised API endpoint.
- **Parse once** into a structured profile (Sonnet, task `resume_parse`), stored as JSON: contact, summary, skills (with categories), experience (company, title, dates, bullets), education, certifications, total years of experience, domains. User can review and edit the parsed profile (skills add/remove, years, titles).
- Versioning: re-upload creates a new version; exactly one *active* résumé per user in MVP; old versions deletable.
- Layout analysis at upload time (deterministic): detect tables, multi-column sections, text boxes, images, headers/footers, unusual fonts → store `format_findings` for the ATS format-health score (F8).
- Delete résumé: deletes file, parsed profile, tailored copies derived from it.
- *Acceptance:* parsed profile available within 60 s for a typical 2-page résumé; round-trip (extract → apply zero edits → save) yields a docx whose `document.xml` styles are unchanged (see §14).

### F3. Job preferences (MUST)

| Field | Type / rules |
|---|---|
| `search_scope` | enum: `india_only`, `india_plus_global_remote` (**default**), `global_remote_only`. UI text for `india_only` states that it covers only company career boards with India locations (plus jobs the user pastes); onboarding copy frames the product as "remote roles open to India". |
| `target_roles` | list of strings (titles/keywords) |
| `locations` | list (cities in India) — used when scope includes India |
| `work_modes` | multi: remote, hybrid, onsite |
| `job_types` | multi: full_time, contract, part_time, internship |
| `hiring_models` | multi: employee, contractor (B2B/EOR) |
| `salary_min`, `salary_max`, `salary_currency` | currency `INR` or `USD`; annual. Default currency `INR`. |
| `experience_level` | optional |
| `min_match_score` | int 1–10, default 8 |
| `include_unspecified_salary` | bool, default true (only if score ≥ min_match_score) |
| `show_unclear_eligibility` | bool, default false in email digest; shown with warning badge in app |
| `timezone_overlap` | text/enum: e.g. "any", "up to 4h overlap with US East", etc.; used as soft preference in scoring |
| `excluded_companies`, `excluded_keywords` | lists |
| `watched_companies` | list of companies the user wants tracked (feeds company resolution) |
| `schedule_enabled`, `schedule_local_time` | default true, `18:00` in user's time zone |
| `digest_when_empty` | bool, default false |

### F4. Job sourcing (MUST) — see §7
Central (not per-user) scheduled fetching from source adapters; normalised `jobs` table; shared cache; deduplication; source attribution; per-source health tracking; failures never break a run.

### F5. India-eligibility classification & job enrichment (MUST) — see §7.4
One Haiku call per job, made **on demand** the first time any user's run needs it (§7.5), cached and shared by all users, **not charged to any user's budget**, producing a structured enrichment record.

### F6. Matching & scoring — **Job Fit Score** (MUST) — see §7.5
Per-user, Haiku, 1–10 with explanation, matched/missing skills; salary rule applied after scoring.

### F7. Scheduled search & daily digest (MUST)
- A dispatcher runs every 5 minutes, finds users whose local schedule time has passed and `last_digest_date < today(user tz)`, and enqueues a per-user run (idempotent).
- Run = prefilter → score (Discovery budget) → filter → build digest → send email → store digest.
- Email: ranked list, each item with title, company, location/eligibility badge, salary label (stated/estimated/not listed), fit score, 2-line reason, links: **Open in app**, **Apply (employer/source link)**, and **source attribution** with followed link.
- In-app: digest history, run history, **Run now** (max 3/day).
- If Discovery budget is exhausted: send a rule-based digest (no scores) with a notice; never fail silently.
- *Acceptance:* two dispatcher ticks never send two digests for the same user/day; a source outage still produces a digest from remaining sources.

### F8. ATS Match Score & résumé tailoring (MUST — core differentiator) — see §8
- **ATS Match Score (0–100)**: estimated compatibility of the résumé with the specific job, computed before and after tailoring. Labelled "estimated" in the UI (real ATS systems differ).
- Tailored résumé: format-preserving edits, diff view, per-change accept/reject, download `.docx`.
- Truthfulness guardrails (never invent skills/employers/dates/degrees/metrics).
- *Acceptance:* styles/fonts/spacing unchanged outside edited text; every accepted change traceable to a paragraph ID; no new number/entity appears unless in the original résumé or user-confirmed facts.

### F9. Cover letter (MUST)
- Generated from job enrichment + user profile (+ optional tailored résumé). Tones: **Concise** (≤ 180 words) and **Confident** (≤ 250 words, assertive, achievement-led). Editable in-app (rich text minimal), downloaded as `.docx` from a simple template.
- Same truthfulness guardrails as F8.

### F10. Interview prep (MUST)
- On demand per job (Sonnet): likely technical topics, behavioural questions with talking points drawn **only** from the user's real experience, skill gaps to prepare, questions to ask the interviewer. Stored per job, regenerable.

### F11. Application tracker (MUST)
- Kanban columns: `Saved, Tailored, Applied, Interviewing, Offer, Rejected, Withdrawn`.
- Card: company, title, fit score, ATS before→after, dates, notes, links to tailored files.
- Auto-move to **Tailored** when documents are generated (only from Saved); all other moves manual and unrestricted (drag & drop + status dropdown for accessibility). Status history kept.
- **Apply** button opens the original apply URL in a new tab (`rel="noopener"`); prompts "Did you apply?" on return (simple confirmation, no tracking pixels).

### F12. Bring-your-own job (MUST)
- User pastes description text (required) and optionally URL/company/title. Goes through enrichment (user-charged to Documents budget since user-initiated), scoring, tailoring, tracking. This is the supported route for LinkedIn/Naukri jobs.

### F13. Usage budgets & model routing (MUST) — see §6
Monthly 2M cap split Discovery 600K / Documents 1.4M, visible to the user (usage meter), warnings at 80%, graceful degradation at 100%.

### F14. Source compliance (MUST)
- Every job card/email item shows **"via {Source}"** with a normal followed link to the original listing where the source's terms require it (RemoteOK, Jobicy, WWR, Himalayas etc.). Per-source flags in `job_sources`: `requires_attribution`, `attribution_text`, `attribution_url`, `terms_reviewed_at`.
- Do not display or forward a source's data in ways its terms forbid. Per-source flags are in §7.1a; verified terms are summarised in Appendix D. Adzuna jobs show the "Jobs by Adzuna" label/logo; Himalayas and RemoteOK jobs link to the source's own job URL with a visible source name. Terms-review checklist is part of each adapter's Definition of Done.

### F15. Admin (MUST, minimal)
Role `Admin`. Pages: source health (last success/failure, counts), company-resolution table (view/override), AI task config (model per task), per-user usage, feature flags (invite-code), manual "run fetch now".

---

## 5. Glossary

| Term | Meaning |
|---|---|
| ATS | Applicant Tracking System used by employers (Greenhouse, Lever, Ashby, etc.) |
| Job Fit Score | 1–10 discovery score of how well a job suits the user (F6) |
| ATS Match Score | 0–100 estimated résumé-vs-job match, before/after tailoring (F8) |
| Eligibility | Whether a remote job accepts candidates from India: `worldwide`, `india_eligible`, `restricted`, `unclear` |
| BYO job | Bring-your-own job: user-pasted description (F12) |
| Discovery / Documents budget | The two monthly token budgets (§6.3) |
| Confirmed facts | Skills/experience the user explicitly confirmed, allowed in tailoring |
| Locked paragraph | Résumé paragraph the tailoring step must never change |

---

## 6. AI layer

### 6.1 Abstraction
- Define `IAiTaskRunner` with `RunAsync<TRequest,TResult>(taskName, request, userContext)`.
- Resolve **model, max_tokens, temperature, budget bucket** from table `ai_task_config` (cached 60 s). Implement `IModelProvider` with Anthropic's official .NET SDK **if a stable release exists at build time**; otherwise a thin `HttpClient` implementation of the Messages API (a few hundred lines, uses the standard resilience handler and exposes usage/cache fields directly). Community packages (e.g., `Anthropic.SDK`) are unofficial; use only if pinned and reviewed.
- All calls: structured JSON output with schema validation; one automatic retry with a repair prompt on invalid JSON; then fail gracefully with a user-facing message and **no token charge for the failed repair beyond actual usage**.
- Enable **prompt caching** for the stable prefix (instructions + parsed profile).
- Every call writes an `ai_usage` row (user, task, model, input/output/cache tokens, bucket, est. cost, success).

### 6.2 Default task → model routing (editable in DB)

| Task | Model (default) | Budget bucket | Charged to user? |
|---|---|---|---|
| `resume_parse` | `claude-sonnet-5-5` | Documents | Yes |
| `job_enrichment` (shared, per job) | `claude-haiku-5-5` | — (platform) | No (platform cost; global daily cap) |
| `job_fit_score` (per user/job) | `claude-haiku-5-5` | Discovery | Yes |
| `ats_analysis` | `claude-sonnet-5-5` | Documents | Yes |
| `resume_tailor` | `claude-sonnet-5-5` | Documents | Yes |
| `cover_letter` | `claude-sonnet-5-5` | Documents | Yes |
| `interview_prep` | `claude-sonnet-5-5` | Documents | Yes |
| `byo_job_enrichment` | `claude-haiku-5-5` | Documents | Yes |

**Quality policy:** anything the user reads as *their own document* (résumé edits, cover letter, interview prep, ATS analysis) runs on Sonnet-class or higher. Haiku is used only for high-volume extraction/classification/ranking. The `ats_analysis` and `resume_tailor` tasks must be switchable to Opus via config without code change; run the evaluation set (§14.3) to confirm Haiku is adequate for `job_enrichment` and `job_fit_score` **before launch**, and promote those tasks to Sonnet if the eval shows quality gaps.

### 6.3 Budgets
- Monthly per user: **Discovery 600,000** + **Documents 1,400,000** = **2,000,000** tokens. Values in config (`Budgets:DiscoveryTokens`, `Budgets:DocumentsTokens`).
- Counted tokens = `input_tokens + output_tokens + cache_creation_tokens + 0.1 × cache_read_tokens`.
- Reset: first day of month 00:00 `Asia/Kolkata`, implemented by period key `YYYY-MM` (no destructive reset job).
- Before each call, refuse if the bucket has no remaining tokens (app error `BUDGET_EXCEEDED`, HTTP 409 with bucket + reset date); after each call, atomically add the actual tokens used. A few concurrent in-flight calls may overshoot by at most their `max_tokens` (accepted; no reservation system).
- At ≥ 80% show warning banner; at 100% of Discovery: rule-based digest; at 100% of Documents: tailoring/cover letter/interview prep disabled with reset date shown.
- Global safety: platform-wide daily token ceiling for `job_enrichment` (config) and per-user rate limit on AI endpoints (e.g., 30 requests/hour).

### 6.4 Indicative token costs (for sizing, not enforcement)
| Operation | Approx tokens |
|---|---|
| Résumé parse | 4–6K |
| Job fit score (summary-based) | ~1K |
| ATS analysis | ~4–5K |
| Tailoring (edits JSON) | ~6–8K |
| Cover letter | ~3–4K |
| Interview prep | ~5–6K |
| Full package per job (analysis+tailor+re-analysis+letter+prep) | ~25–30K → **~45–55 full packages/month** within Documents budget |

---

## 7. Job pipeline

### 7.1 Sources (adapters implement `IJobSource`)

| Source | Priority | Purpose | Notes (verify against official docs) |
|---|---|---|---|
| Himalayas | MUST (conditional: confirm terms in writing, Appendix D) | Global remote with **structured** country/time-zone restrictions and salary | Free public API, no key. Cursor pagination, ≤ 20 jobs/request, ~60 requests/min, ≤ 5,000 results per search, data refreshed every 24 h; supports `country`, `worldwide`, time-zone filters. **Terms:** every Apply must use the API's `applicationLink`; visible source link; do not redistribute to other job boards/aggregators/search engines; cache but delete jobs after `expiresAt`. |
| RemoteOK | MUST | Global remote | Public JSON feed; first array element is a terms/licence notice — skip it. **Terms:** follow (not nofollow) backlink to the job's Remote OK URL and name Remote OK as source; do not use its logo; access is suspended if not followed. Not every item is truly remote; treat `remote` as unverified. |
| Jobicy | SHOULD | Global remote | Credit + link to the job URL. |
| We Work Remotely | SHOULD | Global remote | RSS feed; attribution required. |
| Adzuna (India) | **COULD (post-MVP, not built in MVP)** | India-local jobs | Excluded from the MVP: no public price list, commercial use needs a licence after a 14-day trial, "Jobs by Adzuna" logo required, and its terms forbid contacting third parties for its listings (never follow `redirect_url` or resolve its jobs to an ATS). Revisit if a workable licence is obtained; it would plug in as another Path A adapter. See Appendix D. |
| Greenhouse | MUST | Full descriptions via company boards | `GET boards-api.greenhouse.io/v1/boards/{token}/jobs?content=true`. Read-only; no auth. |
| Lever | SHOULD | same | `GET api.lever.co/v0/postings/{slug}?mode=json` |
| Ashby | SHOULD | same | `GET api.ashbyhq.com/posting-api/job-board/{slug}` |
| Remotive | **EXCLUDED** | — | Terms forbid building a job-search service on its data; capped calls. |
| LinkedIn, Naukri, Indeed | **EXCLUDED** | — | Automated access prohibited. Only via user-pasted jobs. |

Adapter contract:
```csharp
public interface IJobSource {
    string SourceKey { get; }
    Task<IReadOnlyList<RawJob>> FetchAsync(FetchContext ctx, CancellationToken ct);
}
```
`FetchContext` includes `since` timestamp, query hints (roles, scope), and a per-source rate limiter. Adapters only fetch and map to `RawJob`; normalisation is shared code.

### 7.1a Per-source compliance flags (columns on `job_sources`)

| Flag | Meaning | Himalayas | Adzuna | RemoteOK | Greenhouse/Lever/Ashby |
|---|---|---|---|---|---|
| `require_source_apply_url` | Apply button must use the URL supplied by the source; never replace with an employer/ATS link | **yes** | yes (its `redirect_url`) | yes | n/a (employer's own) |
| `forbid_third_party_resolution` | Never follow the source's links or call third parties to enrich its jobs | yes (not needed: full text supplied) | **yes** | no | n/a |
| `purge_on_expiry` | Delete job text when the source's expiry passes | **yes** (`expiresAt`) | no | no | no (job removed from board → mark closed) |
| `requires_logo_attribution` | Label/logo with link on every displayed job | no | **yes** ("Jobs by Adzuna") | no (name + follow link; no logo) | no |
| `poll_interval_minutes` | Fetch cadence | 1440 (data refreshes daily) | per quota | 360 | 360 |

Rules that follow from the flags:
- **Dedupe vs apply link:** if a job record has a source with `require_source_apply_url`, its apply URL is never replaced. If the same job also exists on an employer ATS board, show **only the ATS record** (suppress the aggregator copy); do not merge aggregator content into it.
- **Company resolution (§7.3)** runs only for sources without `forbid_third_party_resolution` and without full text (i.e., RemoteOK, Jobicy, WWR).
- Attribution links are normal follow links (no `nofollow`).

### 7.1b Job discovery paths

Jobs enter the system through two automated paths plus one user-driven path. All three produce the same normalised `JobPosting` and go through the same dedupe, enrichment, scoring and digest pipeline.

| Path | Source type | Needs a company list? | Sources |
|---|---|---|---|
| **A. Feed sources** | Newest jobs across all companies | No | Himalayas (MUST), RemoteOK (MUST), Jobicy (SHOULD), We Work Remotely (SHOULD) |
| **B. Company boards** | One board per company | Yes | Greenhouse (MUST), Lever and Ashby (SHOULD) |
| **C. Bring-your-own job** | User pastes text and optional URL | No | Jobs from LinkedIn, Naukri or anywhere else |

Adzuna is not a discovery path in the MVP (§7.1, Appendix D).

#### Path A: feed sources
**Principle:** fetch incrementally and stop as soon as we reach jobs we already have.

```text
FetchFeed(source):
  cursor = source_health.last_cursor or null
  since  = source_health.last_success_at ?? (now - Fetch:FirstRunBackfillDays)   // first run: short backfill only
  loop:
    page = adapter.GetNewestPage(cursor)                  // newest first
    for job in page: upsert job (dedupe key)
    stop if: page contains a job older than `since`
          or every job on the page already exists
          or page limit reached (Fetch:MaxPagesPerRun)
    cursor = page.next
  save last_success_at = run start time; save last_cursor
```

| Source | Mechanism | Limits and notes |
|---|---|---|
| Himalayas | Browse endpoint (newest first, cursor pagination) or search endpoint with filters (`country`, `worldwide`, time zone, seniority). Prefer **search with `country=IN` plus a second search with `worldwide=true`**, which pre-filters for India eligibility. | ≈ 20 jobs/request, ≈ 60 requests/min, ≤ 5,000 results per search. Data refreshes **daily**, so poll once a day. Delete jobs after `expiresAt`. Apply uses its `applicationLink`. |
| RemoteOK | Public JSON feed; optional tag filter | First array element is a terms notice (skip it). Poll every 6 h. Follow (not nofollow) backlink to the job's RemoteOK URL. Treat `remote` as unverified. |
| Jobicy | Latest-jobs API (**verify**) | Attribution required. Poll every 6 h. |
| We Work Remotely | RSS feeds by category (**verify**) | Attribution required. Poll every 6 h. |

**Eligibility for feed jobs:** Himalayas supplies structured fields (country restrictions, time zones, worldwide flag): worldwide or India in the list → `india_eligible`, otherwise `restricted`. RemoteOK, Jobicy and WWR have little structure, so use phrase rules plus Haiku enrichment (§7.4), which returns `worldwide | india_eligible | restricted | unclear` with evidence.

#### Path B: company boards
There is **no "list all jobs" endpoint** on Greenhouse, Lever or Ashby and no public list of company tokens, so each known board is polled separately.

**Where the company list comes from (three feeders):**
1. **Seed list:** ≥ 100 remote-friendly companies' board tokens shipped with the MVP, each verified by an admin tool (`GET` the board; confirm it returns jobs).
2. **User watch list:** companies users add (company name plus board URL or token).
3. **Discovered from feeds:** when a RemoteOK/Jobicy/WWR job's apply URL already points at a Greenhouse/Lever/Ashby board, extract platform, token and job id and add the company to `company_ats_links`. **Never** for Himalayas or Adzuna jobs (§7.1a).

**Polling:**
- One poll per board every 6 hours, shared by all users.
- Fetch the **job list without descriptions**, compare ids with stored jobs, and fetch details only for new ids (full-content lists can be ≈ 2 MB for large boards).
- Respect crawl delays (Lever ≈ 1 request/second, **verify**); rate-limit globally.
- Board returns 404/410 → mark link `not_found`, retry weekly, never throw. Removed job ids → mark the job closed and hide it.

**Per-platform limits:** Greenhouse has no structured salary field (use enrichment from the text) and EU-hosted boards (`job-boards.eu.greenhouse.io`) are not served by the public API host. Ashby compensation requires `includeCompensation=true` (**verify**).

**Deferred to M7:** guessing board tokens from company names (slug variants plus validation, §7.3 stage 2).

#### Path C: bring-your-own job
User pastes the description (required) and optionally URL, company and title. It is enriched (charged to the user's Documents budget) and flows into scoring, tailoring and the tracker. The URL is stored as text and **never fetched** by the server.

#### How the paths combine
- **Dedupe:** same job via several paths → prefer the record with a full description, then the employer's ATS record. A record from a source with `require_source_apply_url` (Himalayas) keeps its own apply link; if an ATS copy exists, show only the ATS record and suppress the aggregator copy (§7.1a).
- **Schedule:** per-source `PollMinutes`, timed so fresh data is cached before users' digest times (default 18:00 IST).
- **Enrichment:** on demand inside each user's run, cached and shared (§7.5).
- **Failure isolation:** each source and each board runs in its own try/catch and updates `source_health`; one failure never stops the cycle or a digest.

**Configuration:**
```text
Sources__Himalayas__PollMinutes=1440
Sources__RemoteOk__PollMinutes=360
Sources__Jobicy__PollMinutes=360
Sources__Wwr__PollMinutes=360
Sources__Boards__PollMinutes=360
Fetch__MaxPagesPerRun=50
Fetch__FirstRunBackfillDays=14
Sources__Jobicy__PollMinutes=360
Sources__Wwr__PollMinutes=360
Sources__Boards__PollMinutes=360
Fetch__MaxPagesPerRun=50
Fetch__FirstRunBackfillDays=14
```

**Acceptance criteria:**
- A second fetch immediately after the first adds zero jobs and makes only the minimum requests (stops at the first all-known page).
- Killing one source (HTTP 500s or a changed schema) does not stop the others and is recorded in `source_health`.
- A Himalayas job always opens its own `applicationLink`; no Adzuna or Himalayas job is ever sent to an ATS lookup.
- A job pasted via Path C is never fetched from its URL.
- Expired Himalayas jobs have their text deleted after `expiresAt`.

**Coverage check (first week of M2):** run Paths A and B for 7 days, then report unique **India-eligible** jobs per day for sample roles (backend, frontend, data, DevOps, mobile). If numbers are thin, add boards to the seed list before building further.

### 7.2 Resilience rules (apply to ALL external HTTP)
- Use `Microsoft.Extensions.Http.Resilience` (Polly): timeout, retry with jitter on 5xx/timeouts, **circuit breaker per source**, concurrency limit.
- **404/410** on a company board → mark `company_ats_links.status = 'not_found'`, increment failure counter, continue. Never throw to the caller.
- **429/403** → honour `Retry-After`, back off, open the circuit for that source for a configurable window; run continues with other sources.
- **402 or any unexpected 4xx** → treat as "source unavailable for this run", log, record in `source_health`.
- A single source failing must never fail the fetch cycle or a user's digest. Each source run is isolated in try/catch and recorded.
- Central fetch runs on a **per-source schedule** (`Sources:{key}:PollMinutes`; default 360, Himalayas 1440 because its data refreshes daily), timed so fresh data is in the cache before users' digest times. User digests only *read* the cache. ATS adapters poll the board's job **list** (no description) and fetch per-job details only for ids not yet stored (full-content list responses can be ~2 MB for large boards); respect documented crawl delays (e.g., Lever ~1 request/second).

### 7.3 Company → ATS resolution pipeline (run once per company, cached) — **staged**
> MVP implements steps 1, 2, 4, 5 and 6 plus the watched-company list. **Step 3 (slug guessing) is M7.** See System Design §8.4.
Input: a job from an aggregator/remote board (company name, optional domain/website, apply URL).
1. **Parse apply URL.** If host/path matches Greenhouse/Lever/Ashby patterns (e.g., `boards.greenhouse.io/{token}/jobs/{id}`, `job-boards.greenhouse.io/...`, `jobs.lever.co/{slug}/{id}`, `jobs.ashbyhq.com/{slug}/{id}`), extract platform, board token and job ID. Confidence = high.
2. **Lookup `companies` / `company_ats_links`** by website domain (preferred), then normalised name (lowercase, strip legal suffixes: inc, llc, ltd, pvt, gmbh…).
3. **Guess slugs** if unresolved: generate variants of the normalised name (`acme`, `acme-corp`, `acmeinc`, `acmehq`, dashes removed…) and probe each platform's list endpoint. **Validate** a hit: the board's company name or at least one job title must fuzzy-match the source job (≥ 0.85 name similarity or exact title match); otherwise discard. Rate-limit probing (max N probes/minute globally).
4. **Persist** result (including negative: `status='not_found'`, `next_retry_at = now + 30d`). Admin can override.
5. Once resolved, fetch the matching job's full description from the ATS and merge (ATS description wins; the source's apply URL and attribution are kept per §7.1a). Watched companies (F3) are resolved eagerly.
6. If unresolved, keep the aggregator record with `description_completeness = limited`.

### 7.4 Normalisation, dedupe, enrichment
- Normalised `JobPosting`: `source, external_id, title, company, company_id?, location_text, work_mode, employment_type, hiring_model?, salary_min/max/currency/period, salary_kind (stated|estimated|unknown), description_text (HTML stripped), description_completeness (full|limited), apply_url, source_url, posted_at, fetched_at, content_hash`.
- **Dedupe key:** hash of normalised `(company, title, location/remote-flag)`; on collision prefer record with `description_completeness = full`, then the employer ATS source; mark suppressed duplicates with `duplicate_of_job_id` ("also on" is a query on the dedupe key); apply the §7.1a rules for `require_source_apply_url` sources.
- **Enrichment (Haiku, once per job, on demand, shared):** performed inside the per-user run for jobs that pass the raw-field prefilter and have no stored enrichment; strict JSON (schema in §6/Appendix B):
  - `skills_required[]`, `skills_preferred[]`, `skill_aliases{}`, `seniority`, `years_experience_min/max`, `employment_type`, `work_mode`
  - `eligibility`: `worldwide | india_eligible | restricted | unclear`, `eligibility_evidence` (short quote), `restricted_regions[]`, `timezone_requirement`
  - `hiring_model_hint`, `salary_extracted` (if present in text), `summary` (≤ 60 words)
- **Deterministic pre-rules before the LLM:** structured fields from sources (Himalayas location/timezone restrictions) and phrases ("US only", "must reside in", "EU only", "authorised to work in the US") set `eligibility` hints passed to the LLM; if source says worldwide and no contrary phrase is found, mark `worldwide`.
- Salary: if a source gives an estimated (not employer-stated) salary, `salary_kind = estimated` and the UI shows "estimated". Currency conversion uses a daily-cached public FX rate (provider behind `IFxRateProvider`); converted values are shown as "≈".

### 7.5 Per-user pipeline (Job Fit Score)
1. **Raw-field prefilter:** from title, company, location text, source flags and the start of the description, keep jobs plausibly matching the user's roles/skills, scope, excluded companies/keywords and recency (since last digest).
   **Enrich on demand:** for kept jobs without a stored enrichment (best raw-relevance first, at most `Enrichment:MaxPerRun`, default 60), run `job_enrichment` (shared, cached, platform cost).
   **Enriched prefilter:** apply eligibility vs scope, work mode, job type, hiring model, location (India jobs). Cap the shortlist at `Scoring:MaxPerRun` (default **20**; ≈ 1K tokens per scoring × 20 × 30 days ≈ the 600K Discovery budget; configurable per plan).
2. **Haiku scoring** using cached user profile summary + job enrichment JSON (not the raw description) → `fit_score (1–10)`, `reasons[]` (≤ 3, ≤ 20 words each), `matched_skills[]`, `missing_skills[]`, `confidence` (lower when description is limited). Persist in `user_job_scores`; never re-score the same (user, job, resume_version).
3. **Salary rule (after scoring):**
   - salary stated/estimated and within user range (after currency conversion) → keep
   - salary stated and outside range → drop
   - salary unknown → keep only if `include_unspecified_salary` and `fit_score ≥ min_match_score`; tag "salary not listed"
4. Apply `min_match_score` to everything; apply eligibility display rule (`show_unclear_eligibility`).
5. Rank by `fit_score` desc, then recency.

---

## 8. ATS scoring & résumé tailoring (core spec)

### 8.1 Two different scores — do not conflate
- **Job Fit Score (1–10):** "Is this job worth the user's time?" (discovery; Haiku).
- **ATS Match Score (0–100, estimated):** "How well does this résumé match this job's requirements as an ATS/recruiter keyword screen would see it?" (documents; deterministic components + Sonnet analysis). Shown before/after tailoring.

### 8.2 ATS Match Score formula
`ats = 0.35·keyword_coverage + 0.25·requirements_match + 0.10·title_seniority + 0.15·experience_relevance + 0.15·format_health` (weights in config)

- `keyword_coverage` (deterministic): job's required (weight 1.0) and preferred (0.5) skills/tools, expanded with `skill_aliases`; check presence in résumé text (case-insensitive, light stemming).
- `requirements_match` (LLM evidence, deterministic scoring): Sonnet returns per-requirement status `met | partial | missing` with `evidence_paragraph_ids`; score computed from statuses (met=1, partial=0.5, missing=0).
- `title_seniority`: alignment of the user's most recent titles/years vs job's seniority and years range (rules + LLM label).
- `experience_relevance`: LLM label (0–1) with one-line justification.
- `format_health` (deterministic, from layout analysis): penalise tables/columns/text boxes/images, headers/footers holding contact info, non-standard headings, unusual fonts, missing contact details, extremely long résumé. Provide a list of findings with fix suggestions (informational; **do not auto-restructure** the user's layout).
- UI shows score, per-component breakdown, missing keywords, and findings. Always label "estimated; real ATS vary".

### 8.3 Tailoring flow
1. User opens job → **Analyse** (`ats_analysis`) → original ATS score + gaps (missing requirements with whether the user plausibly has them based on profile).
2. **Skill confirmation step:** for each missing required skill not in the profile, ask "Do you have experience with X?" Confirmed answers are saved as *user-confirmed facts* (allowed for use); unconfirmed skills are never added.
3. Extract paragraphs from the original docx with IDs, section labels, and flags:
   - `editable`: summary, experience bullets, skills lines, projects
   - **locked**: name/contact, section headings, company names, job titles, dates, degree/institution lines, certifications titles
   - `locked_prefix` for lines starting with a bold label (e.g., "Languages:") which must remain.
4. Call `resume_tailor` with: job enrichment + description, paragraphs (id, section, text, editable, locked_prefix), user-confirmed facts, ATS gaps, constraints. Output: edits only.
5. **Deterministic validation** of each edit (reject/flag):
   - paragraph exists and is editable; `new_text` length ≤ original × `Tailor:MaxLengthRatio` (default 1.15)
   - every number/percentage/currency/date token in `new_text` exists in the original résumé or confirmed facts
   - new proper nouns/tech terms not in original résumé/profile/confirmed facts → flagged "not in your résumé"; default **rejected** until user confirms
   - no first-person fluff, no emoji, no markdown
6. **Apply** accepted edits to a **copy** of the original docx using `DocumentFormat.OpenXml`: replace text in the paragraph while keeping the dominant run's properties (font, size, bold/italic, colour); preserve `locked_prefix` run; preserve paragraph properties (style, numbering, indent, spacing); never touch section properties, headers/footers, styles part, numbering part, or theme.
7. Present **diff** (original vs proposed per paragraph; word-level highlights) with accept/reject per change and "accept all safe changes".
8. On finalisation re-run ATS scoring (deterministic parts + `ats_analysis` requirement statuses) to display **before → after**.
9. Download `.docx` (SHOULD: PDF export via headless LibreOffice in a container; if unavailable the button is hidden).
10. Store tailored version linked to (user, job, resume_version, edits JSON) for audit and reuse; same job + same résumé version returns the stored result unless user clicks **Regenerate** (charged again).

*Optional (COULD):* `reorder` operation to move bullets within the same role by relevance.

### 8.4 Cover letter flow
Inputs: enrichment, profile, (tailored résumé highlights), tone (`concise`|`confident`), optional user notes ("mention referral by X"). Rules: no invented facts; opening line specific to the company/role; ≤ word limit per tone; no clichés list (config); output plain paragraphs. Save, edit, download `.docx`.

---

## 9. Architecture & tech stack

### 9.1 Stack
| Layer | Choice |
|---|---|
| Backend | ASP.NET Core Web API on the latest .NET LTS, C#, EF Core + Npgsql |
| Background jobs | Hangfire with PostgreSQL storage (dashboard restricted to Admin) |
| DB | PostgreSQL 16+ (`jsonb` for AI outputs) |
| Frontend | React + TypeScript + Vite, TanStack Query, React Router, Tailwind; drag-and-drop via `dnd-kit` |
| Auth | ASP.NET Core Identity; HttpOnly secure cookie auth + antiforgery; SPA served same-origin behind reverse proxy |
| Docx | `DocumentFormat.OpenXml` |
| AI | Anthropic .NET SDK / `Microsoft.Extensions.AI` behind `IAiTaskRunner` |
| HTTP resilience | `Microsoft.Extensions.Http.Resilience` |
| Email | `IEmailSender` (SMTP/Resend/SES/Brevo implementation chosen by config) |
| File storage | `IFileStore` (local disk volume for MVP; S3-compatible implementation later) |
| Logging | Serilog (structured, PII-scrubbed), health checks `/healthz` |
| Tests | xUnit, FluentAssertions, Testcontainers (Postgres), WireMock.Net for sources, Vitest + Playwright (UI smoke) |

### 9.2 Solution layout
```
/src
  Tailorly.Core     // pure rules and models: salary rule, eligibility, ATS calculator, budget policy, dedupe key, ports
  Tailorly.Docx     // all OpenXML work: extract, analyse, apply edits, diff, cover-letter writer
  Tailorly.App      // API, auth, EF Core, Hangfire jobs, sources, AI runner, email, file store (feature folders)
/tests
  Tailorly.Tests    // unit, integration, format-preservation
  Tailorly.Eval     // prompt/model evaluation harness
/web                // React app
/deploy             // docker-compose.yml, Caddyfile, .env.example, restore-test.sh
```

### 9.3 Deployment (MVP)
- Single VM (2–4 GB RAM) running Docker Compose: `caddy` (TLS + reverse proxy), `app` (API + Hangfire server in one process), `postgres`; optional Gotenberg container for PDF export (Compose profile `pdf`, off by default). Nightly `pg_dump` + file-volume backup to object storage.
- No GPU/model hosting. Outbound HTTPS only to AI, job sources, email, FX provider.
- CI: build, test, container image; deploy via `docker compose pull && up -d`.

### 9.4 Scheduled jobs (Hangfire)
| Job | Schedule |
|---|---|
| `FetchSources` (per source, isolated; includes company linking) | per source (`PollMinutes`), staggered |
| `DigestDispatcher` (enqueues due users idempotently) | every 5 minutes |
| `PerUserRun` (prefilter → enrich on demand → score → digest) | enqueued by dispatcher / "Run now" |
| `SendEmail` (retried by Hangfire) | on demand |
| `SweepStuckJobs` (marks resources stuck in `running` > 30 min as failed) | every 15 minutes |
| `PurgeDeletedAccounts` | hourly |
| `ExpireOldJobs` | daily (drop jobs older than 60 days without user references) |

---

## 10. Data model

**The authoritative schema is `DatabaseDesign_TailorlyRemote.md`** (PostgreSQL DDL, constraints, seed data, key queries, retention, deletion order). This section lists only what the PRD requires of it.

Tables: `user_profiles`, `invite_codes`, `feature_flags`, `audit_log` · `resumes`, `resume_profiles`, `user_confirmed_skills` · `user_preferences`, `user_watched_companies` · `job_sources`, `source_health`, `companies`, `company_ats_links`, `jobs`, `job_enrichment` · `user_job_scores`, `digests`, `digest_items` · `user_jobs`, `ats_analyses`, `tailored_documents`, `interview_preps` · `ai_task_config`, `user_budget_counters`, `ai_usage` (plus ASP.NET Identity tables and Hangfire's own schema).

Requirements the schema must satisfy:
- Every user-owned table carries `user_id`; every query is scoped to the authenticated user (EF global query filter) and verified by the all-endpoints isolation test.
- `jobs` is one row per **source listing**; duplicates across sources are marked, not merged, so each source's apply URL and attribution stay intact (§7.1a).
- Long-running resources carry `status` (`queued|running|ready|failed`, plus `proposed|finalizing` for tailored résumés) and `error_code`; partial unique indexes prevent duplicate in-flight work; `digests` has a unique `(user_id, local_date)` for scheduled digests.
- Tracker entries (`user_jobs`) keep a minimal snapshot (title, company, apply URL, source) so they survive job expiry/purge; pasted jobs live there with no `jobs` row.
- Retention and deletion: jobs 60 days (sooner for sources with `purge_on_expiry`), digests/audit 12 months, `ai_usage` aggregated after 13 months; account deletion cascades from the user row after files are removed.

---

## 11. API surface

**The authoritative contract is `ApiDesign_TailorlyRemote.md`** (endpoints, request/response types, status and error codes, rate limits, authorization matrix, examples). Key conventions the PRD requires:
- REST under `/api/v1`, JSON, cookie auth with CSRF header, RFC 7807 errors with stable `code`.
- Endpoints that start AI work return `202 Accepted` with the resource they will fill; the client polls `GET` on that resource until `status` is `ready`, `proposed` or `failed`. A repeated request while one is in flight returns the existing resource.
- Every job returned by the API carries its source attribution and the source-supplied apply URL.
- Users can only reach jobs that were scored for them or that they track; shared tables are never exposed directly.
- Cover letters and tailored résumés share one resource type (`/tailored-documents/{id}`).

---

## 12. Frontend pages
Landing/auth · Onboarding wizard (consent → upload → review parsed profile → preferences → schedule) · Dashboard (today's digest, usage meter) · Jobs list (filters, eligibility/salary badges, source attribution) · Job detail (fit score + reasons, ATS analysis, actions: Tailor / Cover letter / Interview prep / Apply / Save) · Tailoring review (diff, accept/reject, ATS before→after, skill confirmation dialog) · Tracker (Kanban + list view) · Bring-your-own job · Settings (profile, résumé versions, preferences, schedule, delete account) · Usage · Admin (sources, companies, AI config, usage, flags).
Accessibility: keyboard-operable Kanban alternative, WCAG AA contrast, responsive down to 360 px.

---

## 13. Non-functional requirements

**Security & privacy**
- Private file storage; no public URLs; authorised downloads only; ownership checks on every resource (integration test per endpoint).
- HTTPS only, HSTS, secure cookies, CSRF protection, strict CORS (same-origin), input validation, upload scanning for type/size/macros, output encoding.
- Résumé content and job descriptions never written to logs; Serilog destructuring policies scrub PII; AI request/response bodies are not logged (only usage metadata).
- Deletion is real: DB rows, files, derived documents; backups roll off within the retention window (document it).
- Rate limiting: auth endpoints, signup, AI endpoints, Run now.
- Consent captured and versioned; privacy notice discloses AI-provider processing. Obtain legal review for India's DPDP Act obligations before public launch. *(Phase 2 Epic: application-level encryption of résumé files and parsed profiles with managed keys.)*
- Dependency scanning in CI; secrets only via environment/secret store.

**Reliability:** idempotent jobs; per-source isolation; circuit breakers; digest dispatcher safe against double-run; graceful degradation when AI or a source is down (rule-based digest, clear UI message).

**Performance:** page loads < 2 s on broadband; résumé parse < 60 s; tailoring end-to-end < 90 s; digest generation for one user < 3 min.

**Observability (MVP):** structured logs with correlation IDs; an admin status page (source health, failed jobs, tokens per task/user, last digest runs, disk); email alerts to the admin on consecutive source failures, digest failures or unsent digests, AI error spikes, platform token ceiling, low disk, backup failure.

---

## 14. Testing & evaluation

### 14.1 Automated tests
- Unit: salary rule, budget accounting, dedupe key, slug generation/validation, eligibility pre-rules, ATS formula, edit validators.
- Adapter tests with recorded real-response fixtures + resilience tests (404, 429, timeout, malformed JSON, empty feed).
- Docx: **format-preservation test** — take ≥ 10 diverse real-world résumé fixtures (single/two column, tables, bullets, bold lead-ins, multiple fonts); apply no-op and sample edits; assert styles/numbering/section properties unchanged and only targeted paragraph text differs; open result in LibreOffice headless to confirm it opens.
- Integration (Testcontainers): signup→verify→upload→parse (AI stubbed)→preferences→run→digest; cross-user access denial for every endpoint; budget exhaustion paths; deletion removes files.

### 14.2 Idempotency & scheduling tests
Dispatcher double-tick, time-zone boundaries (IST, DST zones), month rollover of budgets.

### 14.3 AI evaluation harness (`Tailorly.Eval`)
- Maintain a labelled set of ≥ 50 (résumé, job) pairs and ≥ 100 job descriptions for eligibility labels.
- Metrics: eligibility accuracy (target ≥ 90%, with `unclear` allowed), fit-score agreement with human labels (Spearman), tailoring hallucination rate (**target 0 new facts**), JSON validity rate (≥ 99%), edit acceptance proxy.
- Run the harness when changing model routing/prompts; compare Haiku vs Sonnet (and Opus for tailoring) and record results in the repo. Promote task models in `ai_task_config` based on results.

---

## 15. Milestones (build order)

| M | Scope | Exit criteria |
|---|---|---|
| M0 | Repo, solution layout, Docker Compose, CI, Identity skeleton, health checks | `docker compose up` runs API+DB+web; CI green |
| M1 | F1 account/consent, F2 résumé upload/parse/profile edit, `IFileStore`, `IAiTaskRunner`, `ai_usage`, budgets (§6) | Journey A up to profile review works with stubbed + real AI |
| M2 | F3 preferences; F4 adapters per §7.1b (Himalayas, RemoteOK, Greenhouse; Jobicy/WWR/Lever/Ashby as SHOULD; **no Adzuna**), resilience, `source_health`, dedupe, attribution + §7.1a flags, company linking (apply-URL parse, lookup) and a **seed list of ≥ 100 remote-friendly companies' ATS board tokens** (verified by an admin tool) | Per-source scheduled fetch fills `jobs`; failures isolated; admin sources page; **one-week coverage report** (§7.1b) reviewed before M3 starts |
| M3 | F5 enrichment, F6 scoring, salary rule, F7 dispatcher/digest/email, in-app jobs list | Scheduled digest at 18:00 IST; idempotent; budget fallbacks |
| M4 | F8 ATS analysis + tailoring + diff UI + docx applier + F9 cover letter | Format-preservation tests pass; eval hallucination rate = 0 on set |
| M5 | F11 tracker, F10 interview prep, F12 BYO job, usage page | Journeys B–D end-to-end |
| M6 | F15 admin, F14 compliance review of each source, security hardening, backups, load/soak test, docs | Launch checklist (Section 17) complete |
| M7 (SHOULD) | Slug-guessing company resolution, Lever/Ashby/Jobicy/WWR adapters, PDF export, reorder op | — |

---

## 16. Cost model (estimates; recompute with current price list at build time)

Assumed list prices per million tokens (input/output): Haiku 5.5 $0.10/$0.50, Sonnet 5.5 $2/$10, Opus 5.5 $4/$20.

| Item | Estimate |
|---|---|
| Worst-case per user per month at full 2M cap (0.6M Haiku + 1.4M Sonnet, 75/25 in/out) | ≈ $0.12 + ≈ $5.6 ≈ **$6** |
| 10 users worst case | ≈ **$60** |
| Typical usage (30–50% of cap) | ≈ $20–30 for 10 users |
| Shared job enrichment (≈ 500 new jobs/day, Haiku) | ≈ $6/month (halve with Batch API if used) |
| Infra (single VM 2–4 GB, backups, domain) | ≈ $15–30/month |
| Email | free/low tier |
| Remote board and ATS board APIs | free (respect rate limits) |

If tailoring moves to Opus: Documents worst case ≈ $11/user/month.

---

## 17. Risks, assumptions, open items

**Risks & mitigations**
| Risk | Mitigation |
|---|---|
| A source changes/withdraws API or terms | Adapter isolation, health monitoring, ≥ 3 independent sources, terms-review checklist |
| "Remote" jobs misclassified as India-eligible | Evidence quote shown to user, `unclear` bucket, eval target ≥ 90%, user feedback button ("not eligible") feeding the eval set |
| LLM invents résumé content | Truthfulness rules + deterministic validators + user diff approval + zero-hallucination eval gate |
| docx formatting breakage on unusual résumés | Locked-structure edits, no-op round-trip test, fall back to "edit text only in paragraph" and warn on exotic layouts |
| Token overruns | Pre-flight budget checks, per-bucket caps, rate limits, platform ceiling |
| Himalayas terms may not permit a per-user matching service (Appendix D) | Written confirmation before launch; design is source-agnostic so any source can be disabled without code change; seed list of ATS boards and RemoteOK provide fallback coverage |
| Legal/privacy (DPDP, third-party AI processing) | Consent + notice, deletion, legal review before public launch |
| Self-signup abuse | Email verification, rate limits, caps, invite-code flag |

**Assumptions to confirm (defaults applied unless changed)**
1. Default salary currency is INR (USD selectable); non-INR job salaries shown in original currency with ≈INR.
2. "Unclear" eligibility jobs are excluded from the email digest by default and shown with a warning badge in-app.
3. Cover-letter tones available in MVP are exactly **Concise** and **Confident** (default Concise).
4. Budget split 600K Discovery / 1.4M Documents is acceptable; adjust after 30 days of real usage data.
5. PDF export is SHOULD (not MVP-blocking).

**Launch checklist:** written confirmation from Himalayas that a personal job-matching/alert service is permitted (and from any other source whose terms are unclear) · terms reviewed for every enabled source and attribution rendered · eval thresholds met · format-preservation suite green · cross-user isolation tests green · backups restore-tested · privacy notice + legal review · alerts configured · admin account secured (MFA recommended).

---

## Appendix A — Configuration / environment variables (examples)
```
ConnectionStrings__Default
Anthropic__ApiKey
Email__Provider, Email__Smtp__*, Email__From
Storage__Provider=Local|S3, Storage__RootPath, Storage__S3__*
Fx__Provider, Fx__ApiKey?
Signup__RequireInviteCode=false
Budgets__DiscoveryTokens=600000
Budgets__DocumentsTokens=1400000
Scoring__MaxPerRun=20
Tailor__MaxLengthRatio=1.15
Sources__Himalayas__PollMinutes=1440
Sources__RemoteOk__PollMinutes=360
Ai__PlatformDailyEnrichmentTokenCeiling=
Admin__BootstrapEmail
```

## Appendix B — JSON contracts (abridged; validate with JSON Schema)

**Job enrichment**
```json
{
  "skills_required": ["C#", ".NET", "PostgreSQL"],
  "skills_preferred": ["Kubernetes"],
  "skill_aliases": {"PostgreSQL": ["Postgres", "psql"]},
  "seniority": "senior",
  "years_experience_min": 5, "years_experience_max": 8,
  "employment_type": "full_time", "work_mode": "remote",
  "eligibility": "india_eligible",
  "eligibility_evidence": "Open to candidates in APAC time zones",
  "restricted_regions": [],
  "timezone_requirement": "4h overlap with CET",
  "hiring_model_hint": "contractor",
  "salary_extracted": {"min": 60000, "max": 90000, "currency": "USD", "period": "year"},
  "summary": "…≤60 words…"
}
```

**Job fit score**
```json
{"fit_score": 8, "reasons": ["…","…"], "matched_skills": ["…"], "missing_skills": ["…"], "confidence": "high|medium|low"}
```

**ATS analysis**
```json
{
  "requirements": [{"text": "5+ years C#", "type": "required", "status": "met|partial|missing", "evidence_paragraph_ids": ["p12"]}],
  "title_seniority_label": 0.8,
  "experience_relevance": {"score": 0.7, "justification": "…"},
  "gaps": [{"skill": "Kubernetes", "likely_has_based_on_profile": false}]
}
```

**Resume tailor (output)**
```json
{
  "edits": [{"paragraph_id": "p14", "new_text": "…", "rationale": "…", "uses_confirmed_fact": false}],
  "unaddressed_gaps": ["Kubernetes"]
}
```
Hard rules in the system prompt: only edit paragraphs flagged `editable`; keep `locked_prefix` untouched; do not add skills, employers, titles, dates, degrees or numbers not present in the input résumé or the user's confirmed skills (`user_confirmed_skills`); keep length within ratio; plain text only; output JSON only.

**Cover letter (output)**
```json
{"paragraphs": ["…","…"], "word_count": 172, "tone": "concise"}
```

**Interview prep (output)**
```json
{
  "technical_topics": [{"topic": "…", "why": "…"}],
  "behavioural": [{"question": "…", "talking_points_from_resume": ["…"]}],
  "gaps_to_prepare": ["…"],
  "questions_to_ask": ["…"]
}
```

## Appendix C — Prompt-writing conventions
Each task has: a system prompt file in `Tailorly.App/Ai/Prompts/{task}.md` (git is the version history; the file carries a version string), a JSON schema file, a validator class, and an eval case set. The prompt version string is stored in `ai_usage` for traceability. Treat all user-supplied or fetched text (résumé, job description) as **untrusted data**: wrap in delimiters, instruct the model to ignore instructions inside it, and never let model output trigger actions other than writing validated JSON fields.

## Appendix D — Source terms review (2026-10-24)

Verification level: **A** = read in a source's own published terms (as quoted by a secondary page); **B** = secondary description only; **C** = not verified. Re-verify against the provider's official pages before each adapter ships.

| Source | Level | Findings that affect the design |
|---|---|---|
| Himalayas | A/B | Public API, no key; commercial integrations allowed on conditions: use its `applicationLink` for every Apply; visible source link; **do not redistribute jobs to other job boards, aggregators or job search engines** (unclear whether a per-user matching service counts → ask `hi@himalayas.app`); cache and delete after `expiresAt`; data refreshed every 24 h; ≤ 20 jobs/request; ~60 req/min; ≤ 5,000 results/search; country/worldwide/time-zone filters provide structured eligibility. |
| Adzuna | A | Commercial use subject to a 14-day trial (licence expected afterwards); "Jobs by Adzuna" label with logo on each displayed advert; all queries must go via Adzuna and contacting third parties for listing content is a breach; multiple accounts forbidden. Free key ≈ 1,000 calls/month. |
| RemoteOK | A | Follow backlink to the job's Remote OK URL + name as source or API access is suspended; no logo use. |
| Greenhouse | A/B | Job Board GET endpoints are public and unauthenticated; intended for career pages and syndication; no structured salary field; no company enumeration; EU-hosted boards are not served by the public API host. |
| Lever | B | Postings are described as public and may be read by third parties; documented ~1 request/second crawl delay. |
| Ashby | B | Public posting API; compensation only with `includeCompensation=true`; terms silent on third-party reading. |
| Jobicy, We Work Remotely | C | Attribution required per secondary sources; verify terms before enabling. |
| LinkedIn, Naukri, Indeed | — | Excluded (automated access prohibited). |
