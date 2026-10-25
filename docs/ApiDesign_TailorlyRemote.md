# API Design — Tailorly Remote

**Version:** 1.0 **Date:** 2026-10-24 **Companions:** `PRD_TailorlyRemote.md` (v1.4), `SystemDesign_TailorlyRemote.md` (v1.3), `DatabaseDesign_TailorlyRemote.md` (v1.0)
**Audience:** Claude Code / engineering team

This is the authoritative HTTP contract between the React SPA and the ASP.NET Core backend. The backend publishes an OpenAPI 3 document generated from the code; the SPA's TypeScript client is generated from it. Where PRD §11 differs, this document wins (differences in Section 11).

---

## 1. Conventions

| Topic | Rule |
|---|---|
| Base path | `/api/v1` (same origin as the SPA; no CORS). Health endpoints are at `/healthz` and `/readyz`. |
| Format | JSON (`application/json; charset=utf-8`), `camelCase` property names, enum values `lower_snake_case`. |
| Identifiers | UUIDs as strings. |
| Time | ISO-8601 UTC strings (`2026-10-24T12:30:00Z`). Local dates as `YYYY-MM-DD`. Times of day as `HH:mm`. |
| Auth | ASP.NET Identity cookie (`HttpOnly`, `Secure`, `SameSite=Lax`, `__Host-` prefix). |
| CSRF | Double-submit: server sets a readable `XSRF-TOKEN` cookie; the SPA sends it as header `X-XSRF-TOKEN` on every `POST/PUT/PATCH/DELETE`. Missing/invalid → `403 CSRF_INVALID`. |
| Content type for upload | `multipart/form-data` (résumé only). |
| Caching | `Cache-Control: no-store` on all authenticated responses and downloads. |
| Pagination | `page` (1-based, default 1) and `pageSize` (default 20, max 50). Response: `{ items, page, pageSize, total }`. |
| Sorting | `sort=field` or `sort=-field` (descending), allowed fields listed per endpoint. |
| Long operations | **202 Accepted + resource**: the response body is the resource with `status`; the client polls `GET` on that resource (see §3). |
| Duplicate submissions | A repeated request while one is in flight returns **200** with the existing resource (not an error). |
| Rate limiting | Headers `RateLimit-Limit`, `RateLimit-Remaining`, `Retry-After` on `429`. Buckets in §9. |
| Versioning | URL major version. Additive changes (new optional fields/endpoints) are non-breaking; clients ignore unknown fields. |
| Request IDs | Server returns `X-Correlation-Id`; it also appears in error bodies as `traceId`. |

### 1.1 Error format (RFC 7807)
```json
{
  "type": "https://tailorly.example/errors/budget-exceeded",
  "title": "Monthly AI budget used up",
  "status": 409,
  "code": "BUDGET_EXCEEDED",
  "detail": "Your Documents budget resets on 2026-11-01.",
  "traceId": "00-ab12...",
  "errors": { "field": ["message"] },
  "extra": { "bucket": "documents", "resetsOn": "2026-11-01" }
}
```
`code` is stable and machine-readable (catalogue in §10). `errors` appears only for validation failures (`400`/`422`).

### 1.2 Status codes used
`200` OK · `201` Created · `202` Accepted · `204` No Content · `400` malformed · `401` not signed in · `403` forbidden/CSRF/not verified · `404` not found *(also used for other users' resources, to avoid leaking existence)* · `409` conflict/budget/concurrency · `413` too large · `415` unsupported media · `422` validation · `429` rate limited · `503` dependency unavailable (e.g., AI provider down).

---

## 2. Shared types

```ts
type Uuid = string;
type Status = 'queued' | 'running' | 'ready' | 'failed';          // simple async resources
type Eligibility = 'worldwide' | 'india_eligible' | 'restricted' | 'unclear';

interface Page<T> { items: T[]; page: number; pageSize: number; total: number }

interface SourceInfo {                  // ALWAYS present on any job returned (attribution, PRD F14)
  key: string; name: string;
  attributionText: string | null;       // e.g. "Source: Remote OK"
  attributionUrl: string | null;        // normal follow link
  sourceUrl: string;                    // listing page on the source
}

interface Salary {
  min: number | null; max: number | null;
  currency: string | null;              // ISO 4217
  period: 'year';                       // normalised to annual
  originalPeriod: string | null;        // hour|day|week|month|year as published
  kind: 'stated' | 'estimated' | 'unknown';
  approxInr: { min: number | null; max: number | null } | null;   // "≈" conversion for display
}

interface JobSummary {
  id: Uuid; title: string; companyName: string; locationText: string | null;
  workMode: 'remote'|'hybrid'|'onsite'|null; employmentType: string | null;
  eligibility: Eligibility; eligibilityEvidence: string | null;
  salary: Salary; salaryLabel: 'stated'|'estimated'|'not_listed';
  descriptionCompleteness: 'full'|'limited';
  postedAt: string | null; applyUrl: string; source: SourceInfo;
  alsoOn: { sourceKey: string; sourceName: string; sourceUrl: string }[];
  score: { fitScore: number; reasons: string[]; matchedSkills: string[]; missingSkills: string[]; confidence: 'high'|'medium'|'low' } | null;
  tracked: { userJobId: Uuid; status: string } | null;
}

interface JobDetail extends JobSummary {
  description: string | null;           // plain text; null if purged/expired
  summary: string | null;               // enrichment summary
  skillsRequired: string[]; skillsPreferred: string[];
  seniority: string | null; timezoneRequirement: string | null;
}
```

Rules:
- `applyUrl` is **always the URL the source supplied** when the source's `require_source_apply_url` flag is set (Himalayas, RemoteOK, Jobicy, WWR), and never an ATS link substituted by us.
- The SPA must render `source.attributionText` as a link to `source.attributionUrl` next to every job (and the email template does the same). Links have no `nofollow`.
- Salary: `approxInr` is present only when the stated currency is not INR and an FX rate is available; the UI shows it as "≈".
- Enrichment-derived fields (`eligibility`, skills, summary) are `unclear`/empty/null until enrichment exists; jobs in a user's list always have enrichment because scoring requires it.

---

## 3. Async resource pattern

Slow work (résumé parse, ATS analysis, tailoring, cover letter, interview prep, manual run, BYO enrichment) follows one pattern:

1. `POST …` validates input and budget, creates the resource row (`status: "queued"`), enqueues a background job, and returns **`202`** with `Location: /api/v1/<resource>/<id>` and the resource JSON.
2. The SPA polls `GET <resource>/<id>` every 1.5 s for the first 10 s, then every 4 s, up to 3 minutes. It stops at `ready`, `proposed` (tailored résumé), or `failed`.
3. `failed` resources carry `errorCode` (see §10) and can be retried by posting again (a new row replaces/creates per the endpoint rules).
4. A request that would duplicate an in-flight job returns **`200`** with the existing resource.
5. Stuck resources are marked `failed` with `errorCode: "TIMEOUT"` by the sweep job after 30 minutes.

```json
{ "id": "…", "status": "running", "errorCode": null, "createdAt": "…", "updatedAt": "…" }
```

---

## 4. Endpoint catalogue

Legend — **Auth:** `public`, `user` (signed in, email verified), `user*` (signed in, verification not required), `admin`. **Cost:** AI work charged to a budget bucket (D = Discovery, Doc = Documents, P = platform).

### 4.1 Health and meta

| Method & path | Auth | Description |
|---|---|---|
| `GET /healthz` | public | Process alive (`200`). |
| `GET /readyz` | public | DB reachable (`200`/`503`). |
| `GET /api/v1/meta` | public | `{ version, consentVersion, requireInviteCode, tones:["concise","confident"], limits:{ resumeMaxMb:5, watchlistMax:25 } }` for the SPA to configure itself. |

### 4.2 Authentication and account

| Method & path | Auth | Body → Response | Notes |
|---|---|---|---|
| `GET /auth/csrf` | public | → `204`, sets `XSRF-TOKEN` cookie | Call once on SPA load. |
| `POST /auth/register` | public | `{ email, password, displayName, timeZone, consentVersion, inviteCode? }` → `201 { userId }` | Rate-limited per IP/email. Sends verification email. Errors: `INVITE_CODE_REQUIRED`, `INVITE_CODE_INVALID`, `CONSENT_REQUIRED`, `EMAIL_TAKEN` is **not** revealed (always `201`; existing users get a "you already have an account" email). |
| `POST /auth/verify-email` | public | `{ userId, token }` → `204` | Token from email link (SPA route calls this). |
| `POST /auth/resend-verification` | user* | → `204` | Max 3/hour. |
| `POST /auth/login` | public | `{ email, password }` → `200 { user }` + auth cookie | Lockout after repeated failures → `429 ACCOUNT_LOCKED`. Unverified users can sign in but get `403 EMAIL_NOT_VERIFIED` on protected actions. |
| `POST /auth/logout` | user* | → `204` | |
| `POST /auth/forgot-password` | public | `{ email }` → `204` (always) | Uniform response. |
| `POST /auth/reset-password` | public | `{ userId, token, newPassword }` → `204` | |
| `POST /auth/change-password` | user | `{ currentPassword, newPassword }` → `204` | |
| `DELETE /account` | user | → `202 { status:"confirmation_sent" }` | Sends signed confirmation link (1 h). |
| `POST /account/confirm-deletion` | public | `{ userId, token }` → `202` | Sets `pending_deletion`, logs out, enqueues purge. |

### 4.3 Profile and consent

| Method & path | Auth | Description |
|---|---|---|
| `GET /me` | user* | `{ id, email, emailVerified, displayName, timeZone, notificationEmail, consent:{version,at}, accountStatus, roles[] }` |
| `PUT /me` | user | `{ displayName, timeZone (IANA, validated), notificationEmail }` → `200` |
| `POST /me/consent` | user | `{ version }` → `204` (re-consent when the notice version changes) |

### 4.4 Résumés

| Method & path | Auth / Cost | Description |
|---|---|---|
| `POST /resumes` | user / Doc | `multipart/form-data` field `file` (`.docx`, ≤ 5 MB). Validates extension, magic bytes, zip limits, no macros/OLE/external links. → `202 Resume` (`parseStatus: queued`). Errors: `UNSUPPORTED_FILE_TYPE` (PDF → message "upload the Word version"), `FILE_TOO_LARGE`, `FILE_UNSAFE`, `CONSENT_REQUIRED`, `BUDGET_EXCEEDED`. The new version becomes active when its parse is `ready` (previous active is deactivated). |
| `GET /resumes` | user | List versions (newest first). |
| `GET /resumes/{id}` | user | Resume with `parseStatus`, `errorCode`, `formatFindings`, `isActive`, `version`, `createdAt`. **Poll here.** |
| `GET /resumes/{id}/download` | user | Original file; `Content-Disposition: attachment`. |
| `POST /resumes/{id}/activate` | user | Make this ready version the active one → `200`. |
| `DELETE /resumes/{id}` | user | Deletes file, profile, derived analyses and tailored documents → `204`. Deleting the only active résumé disables scheduled digests until a new one is ready. |
| `GET /resumes/{id}/profile` | user | `{ parsed, userEdited, effective }` where `effective` = edited merged over parsed. |
| `PUT /resumes/{id}/profile` | user | `{ userEdited }` (validated against the profile schema) → `200`. |
| `GET /me/confirmed-skills` | user | `[{ id, skill, createdAt }]` |
| `POST /me/confirmed-skills` | user | `{ skill }` → `201` (idempotent on `skill_key`) |
| `DELETE /me/confirmed-skills/{id}` | user | → `204` |

```ts
interface Resume {
  id: Uuid; version: number; originalFilename: string; sizeBytes: number; isActive: boolean;
  parseStatus: Status; errorCode: string | null;
  formatFindings: { hasTables: boolean; hasColumns: boolean; hasTextBoxes: boolean; hasImages: boolean;
                    fonts: string[]; wordCount: number; trackedChanges: boolean;
                    lockedParagraphs: number; editableParagraphs: number } | null;
  createdAt: string;
}
```

### 4.5 Preferences and watch list

| Method & path | Auth | Description |
|---|---|---|
| `GET /preferences` | user | Current preferences (defaults if never saved). Includes `concurrencyToken`. |
| `PUT /preferences` | user | Full replacement. Validation: `salaryMax ≥ salaryMin`, `minMatchScore` 1–10, `scheduleLocalTime` `HH:mm`, array sizes ≤ 25, string lengths ≤ 100. Stale `concurrencyToken` → `409 CONCURRENT_UPDATE`. |
| `GET /watched-companies` | user | `[{ id, name, platform, boardToken, status, openJobsCount, origin }]` for the user's watch list. |
| `POST /watched-companies` | user | `{ boardUrl }` **or** `{ platform, boardToken, name? }`. The server parses the URL against an allowlist (`boards.greenhouse.io`, `job-boards.greenhouse.io`, `jobs.lever.co`, `jobs.ashbyhq.com`), builds the fixed API URL from the validated token, verifies the board returns jobs → `201`. Errors: `INVALID_BOARD_URL`, `BOARD_NOT_FOUND`, `WATCHLIST_LIMIT` (25). The user-supplied URL itself is never fetched. |
| `DELETE /watched-companies/{id}` | user | Removes from the user's list (the shared link stays). |

```ts
interface Preferences {
  searchScope: 'india_only' | 'india_plus_global_remote' | 'global_remote_only';
  targetRoles: string[]; locations: string[];
  workModes: ('remote'|'hybrid'|'onsite')[]; jobTypes: ('full_time'|'contract'|'part_time'|'internship')[];
  hiringModels: ('employee'|'contractor')[];
  salaryMin: number | null; salaryMax: number | null; salaryCurrency: 'INR' | 'USD';
  experienceLevel: string | null; minMatchScore: number;
  includeUnspecifiedSalary: boolean; showUnclearEligibility: boolean;
  timezoneOverlap: string | null; excludedCompanies: string[]; excludedKeywords: string[];
  scheduleEnabled: boolean; scheduleLocalTime: string; digestWhenEmpty: boolean;
  concurrencyToken: string;
}
```

### 4.6 Jobs, digests and runs

| Method & path | Auth / Cost | Description |
|---|---|---|
| `GET /jobs` | user | Jobs **already scored for this user** (never the raw shared pool). Query: `digestId`, `minScore`, `eligibility` (repeatable), `workMode` (repeatable), `salary` (`stated\|estimated\|not_listed`), `q` (title/company text), `savedOnly`, `sort` (`-score`\|`-posted`), `page`, `pageSize`. → `Page<JobSummary>` |
| `GET /jobs/{id}` | user | `JobDetail`. `404` if the job is not visible to the user (not scored for them and not tracked by them) or has been purged. |
| `POST /jobs/byo` | user / Doc | `{ description (required, ≤ 30,000 chars), url?, companyName?, title? }` → `202 UserJob` (`enrichmentStatus: queued`). Creates a tracker entry with `isByo: true`. The URL is stored as text and **never fetched**. Optional `url` must be `http/https`. |
| `GET /digests` | user | `Page<DigestSummary>`; filter `kind`. |
| `GET /digests/{id}` | user | `{ id, kind, localDate, status, degraded, jobsConsidered, jobsScored, startedAt, finishedAt, emailedAt, items: [{ rank, job: JobSummary }] }`. **Poll here** for manual runs. |
| `POST /runs` | user / D+P | Manual run ("Run now"). → `202 Digest(kind=manual)`. Max 3/day (`RUN_LIMIT_REACHED`); needs an active ready résumé (`RESUME_REQUIRED`); returns the in-flight run with `200` if one exists. Manual runs do **not** send email. |

**Visibility rule for `GET /jobs/{id}`:** user may read a job if a `user_job_scores` row exists for them or a `user_jobs` row references it. This prevents enumerating the shared jobs table.

### 4.7 Tracker (saved jobs)

| Method & path | Auth | Description |
|---|---|---|
| `POST /user-jobs` | user | `{ jobId }` → `201 UserJob` (snapshots title/company/apply URL/source; copies the fit score). Idempotent per `(user, job)` → `200` existing. |
| `GET /user-jobs` | user | Query: `status` (repeatable), `q`, `sort` (`-updated`\|`-score`), `page`, `pageSize` → `Page<UserJob>`. |
| `GET /user-jobs/{id}` | user | Full `UserJob` with documents summary. |
| `PATCH /user-jobs/{id}` | user | `{ status?, notes?, concurrencyToken }` → `200`. Any status → any status; appends `statusHistory`. |
| `PUT /user-jobs/{id}/description` | user | `{ description }` (≤ 30,000 chars) — for limited descriptions or expired jobs. Stores `userDescription`, clears `userEnrichment`, and schedules enrichment on next AI action → `200`. |
| `DELETE /user-jobs/{id}` | user | Deletes the entry and its analysis, tailored documents and prep → `204`. |

```ts
interface UserJob {
  id: Uuid; jobId: Uuid | null; isByo: boolean;
  title: string; companyName: string; applyUrl: string | null;
  source: { key: string; name: string; attributionUrl: string | null } | null;
  status: 'saved'|'tailored'|'applied'|'interviewing'|'offer'|'rejected'|'withdrawn';
  notes: string | null; fitScore: number | null; atsBefore: number | null; atsAfter: number | null;
  hasUserDescription: boolean; descriptionAvailable: boolean;      // false if job purged and no pasted text
  enrichmentStatus: Status | null;                                 // for BYO / pasted descriptions
  statusHistory: { status: string; at: string }[];
  documents: { analysisStatus: Status | null; resumeStatus: string | null; coverLetterStatus: string | null; interviewPrepStatus: Status | null };
  concurrencyToken: string; createdAt: string; updatedAt: string;
}
```

### 4.8 ATS analysis, tailoring, cover letters, interview prep

All of these operate on a tracker entry (`/user-jobs/{id}`), so BYO jobs and saved jobs behave the same.

**Preconditions common to all AI endpoints**
- Signed in, email verified, consent recorded, an active `ready` résumé (`RESUME_REQUIRED`).
- Job description available: `description_completeness = full` **or** a user-pasted description exists; otherwise `422 JOB_DESCRIPTION_REQUIRED` with `extra.action = "paste_description"` (the SPA then calls `PUT /user-jobs/{id}/description`).
- Documents budget has remaining tokens (`BUDGET_EXCEEDED`, `409`).

| Method & path | Cost | Description |
|---|---|---|
| `POST /user-jobs/{id}/analysis` | Doc | Body `{ resumeId? }` (default active résumé). Runs ATS analysis of the **original** résumé against the job. → `202 Analysis`. Re-posting after `ready` re-runs it (charged) and replaces the result; while in flight returns `200` existing. |
| `GET /user-jobs/{id}/analysis` | — | Latest `Analysis` (`404` if never run). **Poll here.** |
| `POST /user-jobs/{id}/tailored-resumes` | Doc | Body `{ resumeId? }`. Requires a `ready` analysis for that résumé (`422 ANALYSIS_REQUIRED`). Uses the user's confirmed skills. → `202 TailoredDocument(kind=resume)`; ends at status `proposed` (edits awaiting decisions). |
| `GET /user-jobs/{id}/tailored-documents` | — | List documents for the tracker entry (both kinds), newest first. |
| `GET /tailored-documents/{id}` | — | `TailoredDocument` (see below). **Poll here.** |
| `POST /tailored-documents/{id}/decisions` | — | Body `{ decisions: [{ editId, decision: "accepted"\|"rejected" }] }`. Only in status `proposed`. Edits flagged `new_fact` default to `rejected` until the user explicitly accepts. → `200 TailoredDocument`. |
| `POST /tailored-documents/{id}/finalize` | Doc | Applies accepted edits to a copy of the original file, re-scores. Pending edits count as rejected. `422 NO_ACCEPTED_EDITS` if none accepted. `409 STALE_EDIT` if the résumé changed since proposal (a paragraph fingerprint no longer matches; the user must regenerate). → `202` (`status: finalizing → ready`). |
| `GET /tailored-documents/{id}/download` | — | Query `format=docx` (default) or `pdf` (only if `pdf_export.enabled`; otherwise `404 FORMAT_UNAVAILABLE`). Only when `status = ready`. `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`. |
| `DELETE /tailored-documents/{id}` | — | → `204` (deletes the stored file). |
| `POST /user-jobs/{id}/cover-letters` | Doc | Body `{ tone: "concise"\|"confident", notes?, resumeId? }` (`notes` ≤ 500 chars, e.g., referral). → `202 TailoredDocument(kind=cover_letter)`. Does not require analysis. |
| `PUT /tailored-documents/{id}/content` | — | Cover letters only, status `ready`: `{ paragraphs: string[] }` (≤ 12 paragraphs, ≤ 3,000 chars total) → `200`; the stored `.docx` is regenerated. |
| `POST /user-jobs/{id}/interview-prep` | Doc | → `202 InterviewPrep`. Re-posting regenerates (charged). |
| `GET /user-jobs/{id}/interview-prep` | — | `InterviewPrep` (`404` if never run). **Poll here.** |

```ts
interface Analysis {
  id: Uuid; userJobId: Uuid; resumeId: Uuid; status: Status; errorCode: string | null;
  atsScore: number | null;                                    // 0-100, estimated
  components: { keywordCoverage: number; requirementsMatch: number; titleSeniority: number;
                experienceRelevance: number; formatHealth: number } | null;   // each 0-1
  requirements: { text: string; type: 'required'|'preferred'; status: 'met'|'partial'|'missing'; evidenceParagraphIds: string[] }[];
  gaps: { skill: string; likelyHas: boolean; confirmed: boolean }[];           // confirmed = in user's confirmed skills
  findings: { code: string; severity: 'info'|'warn'; message: string; suggestion: string | null }[];
  disclaimer: string;                                          // "Estimated; real ATS systems vary."
  updatedAt: string;
}

interface TailoredDocument {
  id: Uuid; userJobId: Uuid; kind: 'resume' | 'cover_letter';
  status: 'queued'|'running'|'proposed'|'finalizing'|'ready'|'failed'; errorCode: string | null;
  sourceResumeVersion: number;
  atsBefore: number | null; atsAfter: number | null; atsBreakdown: Analysis['components'];
  edits: {                                                     // résumé only
    id: string; paragraphId: string; section: string;
    original: string; proposed: string; rationale: string;
    diff: { type: 'equal'|'insert'|'delete'; text: string }[];
    flags: ('new_fact'|'length'|'formatting_simplified')[];
    decision: 'pending'|'accepted'|'rejected';
  }[];
  unaddressedGaps: string[];
  tone: 'concise' | 'confident' | null;                        // cover letter only
  content: { paragraphs: string[]; wordCount: number } | null; // cover letter only
  downloadable: boolean; formats: ('docx'|'pdf')[];
  createdAt: string; updatedAt: string; finalizedAt: string | null;
}

interface InterviewPrep {
  id: Uuid; status: Status; errorCode: string | null;
  content: {
    technicalTopics: { topic: string; why: string }[];
    behavioural: { question: string; talkingPointsFromResume: string[] }[];
    gapsToPrepare: string[]; questionsToAsk: string[];
  } | null;
  updatedAt: string;
}
```

`formatting_simplified` flags a paragraph whose inline formatting (e.g., a bold keyword mid-sentence) will be flattened to the paragraph's dominant style if the edit is accepted (SDD §10.2). The SPA shows a visible notice next to such edits.

### 4.9 Usage

| Method & path | Auth | Description |
|---|---|---|
| `GET /usage` | user | Current budgets. |

```json
{
  "periodKey": "2026-10",
  "resetsOn": "2026-11-01",
  "buckets": [
    { "bucket": "discovery", "limit": 600000,  "used": 212340, "remaining": 387660, "percentUsed": 35 },
    { "bucket": "documents", "limit": 1400000, "used": 1130000, "remaining": 270000, "percentUsed": 81 }
  ],
  "warnings": ["documents_80_percent"]
}
```

### 4.10 Email links (public, signed tokens)

| Method & path | Auth | Description |
|---|---|---|
| `GET /email/unsubscribe?token=…` | public | Never changes state. Redirects (`302`) to the SPA page `/unsubscribe?token=…` showing a confirmation. |
| `POST /email/unsubscribe?token=…` | public (token) | One-click unsubscribe (RFC 8058, used by the `List-Unsubscribe-Post` header and the SPA page). Sets `schedule_enabled = false` → `200 { done: true }`. Token is a signed, purpose-bound Data Protection token; no CSRF header needed. |

### 4.11 Admin (`admin` role; every call audited)

| Method & path | Description |
|---|---|
| `GET /admin/status` | One-page operational view: per-source health, Hangfire counts (enqueued/failed), stuck resources, AI calls/tokens/errors (last 24 h, by task and model), platform enrichment tokens today vs ceiling, last digest runs, disk usage. |
| `GET /admin/sources` | All `job_sources` with `source_health`. |
| `PUT /admin/sources/{key}` | `{ enabled?, pollIntervalMinutes?, termsReviewedAt?, attributionText?, attributionUrl? }`. Enabling a source whose `termsReviewedAt` is null returns `409 TERMS_NOT_REVIEWED` (guard rail). |
| `POST /admin/sources/{key}/run` | Run one fetch now → `202 { runId }`. |
| `GET /admin/companies` | Query `platform`, `status`, `origin`, `q`, `page`. Company ATS links with health (`failureCount`, `openJobsCount`, `lastCheckedAt`). |
| `POST /admin/companies` | `{ name, websiteDomain?, platform, boardToken }` → verifies the fixed board URL; `201`. |
| `PUT /admin/companies/{linkId}` | `{ status }` (override `resolved`/`not_found`/`blocked`). |
| `POST /admin/companies/{linkId}/verify` | Re-check a board → `200`. |
| `POST /admin/companies/seed/load` | Load `watched_companies.seed.json` → `202 { added, skipped }` (idempotent). |
| `GET /admin/coverage` | Query `from`, `to` (dates), `roles` (comma list of keywords, default `backend,frontend,data,devops,mobile`). Returns per day × source × eligibility counts and per-role counts of India-eligible jobs (the M2 one-week coverage report). |
| `GET /admin/ai-config` / `PUT /admin/ai-config/{task}` | `{ model, fallbackModel?, maxTokens, temperature, bucket, enabled }`; takes effect within 60 s (cache). |
| `GET /admin/usage` | Query `period`. Totals per task/model and per user (ids and counts only, never content). |
| `GET /admin/users` | Query `q`, `status`, `page` → `{ id, email, status, createdAt, lastLoginAt, resumeCount, tokensUsed }`. No résumé or job content. |
| `POST /admin/users/{id}/suspend` · `…/unsuspend` | SHOULD. Sets `account_status`; suspended users cannot sign in. |
| `GET /admin/flags` · `PUT /admin/flags/{key}` | Feature flags. |
| `GET/POST/DELETE /admin/invite-codes` | Manage invite codes. |

The Hangfire dashboard is served at `/admin/jobs` (not part of this API) under the same admin policy.

---

## 5. Authorization matrix and isolation rules

| Class | Rule |
|---|---|
| `public` | Health, meta, CSRF, register, verify, login, forgot/reset password, confirm deletion, unsubscribe. |
| `user*` | Signed in; email verification not required (resend verification, logout, `GET /me`). |
| `user` | Signed in **and** email verified **and** `account_status = active`. |
| `admin` | Role `Admin`. MFA recommended. |

**Isolation:** every `{id}` in a user-owned path is resolved through the user-scoped data layer (global query filter). A resource belonging to another user returns **`404`**, never `403`. The all-endpoints isolation test (SDD §14.1) creates resources as user A and calls every user-owned route as user B, expecting `404` (or an empty list).

**Mass assignment:** endpoints bind to explicit request DTOs; server-controlled fields (`userId`, `status`, `ownerId`, token counts, timestamps) are never accepted from the client.

---

## 6. Rate limit buckets

| Bucket | Applies to | Limit (initial) |
|---|---|---|
| `auth-login` | `POST /auth/login` | 10/min/IP, plus account lockout |
| `auth-register` | `POST /auth/register` | 5/hour/IP and 3/hour/email |
| `auth-mail` | forgot-password, resend-verification | 5/hour/email |
| `ai-start` | `POST` resumes, analysis, tailored-resumes, finalize, cover-letters, interview-prep, `jobs/byo`, `runs` | 30/hour/user (plus budget and in-flight protections) |
| `manual-run` | `POST /runs` | 3/day/user |
| `api-default` | everything else (user) | 120/min/user |
| `download` | file downloads | 60/min/user |
| `admin` | `/admin/*` | 60/min/user |

Limits are configuration, enforced with ASP.NET Core rate-limiting middleware (partitioned by user id, falling back to IP). `429` responses carry `Retry-After`.

---

## 7. Behaviour details that matter

| Topic | Rule |
|---|---|
| Digest visibility | A user only ever receives/queries jobs that passed *their own* scoring, filtering and salary rule. The API never exposes unscored shared jobs. |
| Salary rule | Already applied in `GET /jobs`/digests; `salaryLabel = not_listed` jobs appear only when `includeUnspecifiedSalary` and `fitScore ≥ minMatchScore`. |
| Unclear eligibility | Hidden from the email digest unless `showUnclearEligibility`; in the app they appear with `eligibility: "unclear"` so the SPA shows a warning badge. |
| Expiry | After a source's expiry/purge, `GET /jobs/{id}` is `404` and tracker cards fall back to their snapshot (`descriptionAvailable: false` unless the user pasted text). |
| Resume version | Scores and analyses record the résumé version used; uploading a new active résumé does not rewrite history. |
| Downloads | Served through the API (never direct storage URLs); filename is generated (`Resume - {Company} - {Title}.docx`) with unsafe characters removed. |
| Large text | Request bodies limited to 1 MB (JSON) and 5 MB (upload). Text fields have explicit max lengths; excess → `422 VALIDATION_FAILED`. |
| Idempotent re-posts | `POST /user-jobs` (same job), `POST /me/confirmed-skills` (same skill), in-flight AI posts → `200` existing resource. |
| Concurrency | `PUT /preferences`, `PATCH /user-jobs/{id}` carry `concurrencyToken`; mismatch → `409 CONCURRENT_UPDATE`. |
| Account state | `pending_deletion` or `suspended` users get `403 ACCOUNT_DISABLED` on every authenticated endpoint. |

---

## 8. Typical client flows

**Onboarding:** `GET /auth/csrf` → `POST /auth/register` → email link → `POST /auth/verify-email` → `POST /auth/login` → `POST /me/consent` (if needed) → `POST /resumes` → poll `GET /resumes/{id}` → `GET/PUT /resumes/{id}/profile` → `PUT /preferences` → `GET /watched-companies` (optional adds).

**Daily:** email digest links to the SPA → `GET /digests/{id}` → `GET /jobs/{id}` → `POST /user-jobs {jobId}` (Save) or straight to tailoring.

**Tailoring:** `POST /user-jobs/{id}/analysis` → poll → show gaps → user confirms skills (`POST /me/confirmed-skills`) → `POST /user-jobs/{id}/tailored-resumes` → poll until `proposed` → `POST /tailored-documents/{id}/decisions` → `POST …/finalize` → poll until `ready` → `GET …/download`. Cover letter: `POST /user-jobs/{id}/cover-letters {tone}` → poll → optional `PUT …/content` → download.

**Bring your own job:** `POST /jobs/byo` → poll `GET /user-jobs/{id}` until `enrichmentStatus = ready` → analysis/tailoring as above.

**Apply:** the SPA opens `applyUrl` in a new tab (`rel="noopener"`, no `nofollow` on attribution links), then offers "Mark as applied" → `PATCH /user-jobs/{id} { status: "applied" }`.

---

## 9. Example payloads

**`GET /jobs?minScore=8&eligibility=india_eligible&sort=-score`**
```json
{
  "items": [{
    "id": "0192d9c0-7f3a-7c21-9a1e-5b6d2f0c1a11",
    "title": "Senior Backend Engineer (.NET)",
    "companyName": "Acme Cloud",
    "locationText": "Remote - APAC",
    "workMode": "remote", "employmentType": "full_time",
    "eligibility": "india_eligible", "eligibilityEvidence": "Open to candidates in APAC time zones",
    "salary": { "min": 60000, "max": 90000, "currency": "USD", "period": "year", "originalPeriod": "year",
                "kind": "stated", "approxInr": { "min": 5000000, "max": 7500000 } },
    "salaryLabel": "stated", "descriptionCompleteness": "full", "postedAt": "2026-10-23T08:00:00Z",
    "applyUrl": "https://himalayas.app/companies/acme-cloud/jobs/senior-backend-engineer",
    "source": { "key": "himalayas", "name": "Himalayas", "attributionText": "Source: Himalayas",
                "attributionUrl": "https://himalayas.app",
                "sourceUrl": "https://himalayas.app/companies/acme-cloud/jobs/senior-backend-engineer" },
    "alsoOn": [],
    "score": { "fitScore": 9, "reasons": ["Strong .NET and PostgreSQL match", "Seniority aligns with 6 years"],
               "matchedSkills": ["C#", ".NET", "PostgreSQL"], "missingSkills": ["Kubernetes"], "confidence": "high" },
    "tracked": null
  }],
  "page": 1, "pageSize": 20, "total": 1
}
```

**`POST /user-jobs/{id}/tailored-resumes` → `202`**
```json
{ "id": "0192d9c1-…", "userJobId": "…", "kind": "resume", "status": "queued", "errorCode": null,
  "sourceResumeVersion": 2, "edits": [], "unaddressedGaps": [], "tone": null, "content": null,
  "downloadable": false, "formats": [], "createdAt": "2026-10-24T12:30:00Z", "updatedAt": "2026-10-24T12:30:00Z",
  "finalizedAt": null, "atsBefore": 58, "atsAfter": null, "atsBreakdown": null }
```

**A proposed edit inside `GET /tailored-documents/{id}`**
```json
{ "id": "e7", "paragraphId": "p14", "section": "experience",
  "original": "Built REST APIs for the payments team.",
  "proposed": "Built REST APIs in .NET and PostgreSQL for the payments team.",
  "rationale": "Adds the job's required stack terms already present in your profile.",
  "diff": [ { "type": "equal", "text": "Built REST APIs " }, { "type": "insert", "text": "in .NET and PostgreSQL " },
            { "type": "equal", "text": "for the payments team." } ],
  "flags": [], "decision": "pending" }
```

---

## 10. Error code catalogue

| Code | HTTP | Meaning / client behaviour |
|---|---|---|
| `VALIDATION_FAILED` | 422 | Field errors in `errors` |
| `BAD_REQUEST` | 400 | Malformed JSON / parameters |
| `UNAUTHENTICATED` | 401 | Sign in |
| `CSRF_INVALID` | 403 | Refresh CSRF cookie and retry once |
| `EMAIL_NOT_VERIFIED` | 403 | Show "verify your email" with resend |
| `CONSENT_REQUIRED` | 403 | Show consent dialog |
| `ACCOUNT_DISABLED` | 403 | Pending deletion or suspended |
| `ACCOUNT_LOCKED` | 429 | Too many failed logins; `Retry-After` |
| `FORBIDDEN` | 403 | Admin only, etc. |
| `NOT_FOUND` | 404 | Also used for other users' resources |
| `INVITE_CODE_REQUIRED` / `INVITE_CODE_INVALID` | 422 | Signup |
| `UNSUPPORTED_FILE_TYPE` | 415 | Message tells the user to upload the Word (.docx) version |
| `FILE_TOO_LARGE` | 413 | > 5 MB |
| `FILE_UNSAFE` | 422 | Macros, external links, embedded objects, password protection, archive limits exceeded |
| `RESUME_REQUIRED` | 409 | Upload/activate a résumé first |
| `JOB_DESCRIPTION_REQUIRED` | 422 | Ask the user to paste the full description (`extra.action = paste_description`) |
| `ANALYSIS_REQUIRED` | 422 | Run the analysis first |
| `NO_ACCEPTED_EDITS` | 422 | Accept at least one change |
| `STALE_EDIT` | 409 | Résumé changed since the proposal; regenerate |
| `BUDGET_EXCEEDED` | 409 | `extra.bucket`, `extra.resetsOn` |
| `RUN_LIMIT_REACHED` | 429 | Manual runs: 3/day |
| `RATE_LIMITED` | 429 | `Retry-After` |
| `CONCURRENT_UPDATE` | 409 | Reload and retry |
| `INVALID_BOARD_URL` | 422 | Not an allowed ATS board URL/token |
| `BOARD_NOT_FOUND` | 422 | Board does not exist or has no public jobs |
| `WATCHLIST_LIMIT` | 409 | 25 companies per user |
| `FORMAT_UNAVAILABLE` | 404 | PDF not enabled |
| `TERMS_NOT_REVIEWED` | 409 | Admin: set `termsReviewedAt` before enabling a source |
| `AI_UNAVAILABLE` | 503 | Provider failing; the operation is retried in the background; show "taking longer than usual" |
| `TIMEOUT` | — | Resource `errorCode` after the stuck-work sweep |
| `INTERNAL_ERROR` | 500 | Include `traceId` in the message shown to the user |

---

## 11. Differences from PRD §11 (this document wins)

| # | Change | Reason |
|---|---|---|
| API1 | Cover letters and tailored résumés share `/tailored-documents/{id}` (no `/cover-letters/{id}`) | One resource type, matches DB (`tailored_documents`) |
| API2 | `analyze` → `POST/GET /user-jobs/{id}/analysis`; `tailor` → `POST /user-jobs/{id}/tailored-resumes` | Noun-based resources, pollable |
| API3 | `POST /resumes/{id}/facts` → `/me/confirmed-skills` | Skills persist across résumé versions |
| API4 | Added `GET /resumes/{id}` (polling), `POST /resumes/{id}/activate`, `GET /api/v1/meta` | Needed by the SPA |
| API5 | Added `/watched-companies` | Path B feeder 2 |
| API6 | Added `PUT /user-jobs/{id}/description` | Pasted descriptions for limited/expired jobs |
| API7 | Added `GET/POST /email/unsubscribe` | One-click unsubscribe |
| API8 | `POST /runs` creates a **manual** digest, never emails; scheduled digests are created only by the dispatcher | Matches DB `digests.kind` |
| API9 | Admin: added `/admin/status`, `/admin/coverage`, `/admin/companies/seed/load`, `TERMS_NOT_REVIEWED` guard | Operability and the M2 coverage check |
| API10 | `GET /jobs` returns only jobs scored for the caller | Data minimisation and source-terms safety |

---

## 12. Implementation and testing notes

- **OpenAPI:** generate with Swashbuckle or NSwag; commit `openapi.json` and fail CI if it changes unexpectedly (contract snapshot). Generate the SPA's TypeScript client from it (e.g., `openapi-typescript`).
- **Validation:** FluentValidation (or data annotations) per request DTO; shared limits from configuration; consistent `422` shape.
- **Contract tests (integration, Testcontainers):** each endpoint's happy path, auth failure, validation failure, and (for user-owned routes) cross-user `404`.
- **Async tests:** resource transitions `queued → running → ready/failed`; duplicate in-flight POST returns `200`; stuck-resource sweep sets `TIMEOUT`.
- **Budget tests:** `BUDGET_EXCEEDED` paths per bucket; digest falls back to rule-based ranking when Discovery is exhausted.
- **Attribution tests:** every job DTO has a populated `source`; email and SPA render the attribution link; Himalayas `applyUrl` is never an ATS URL.
- **Security tests:** CSRF required on mutating routes; file upload rejects macros/zip bombs/PDF renamed to `.docx`; downloads set `nosniff` and `attachment`.
- **No server-side fetch of user URLs:** a test asserts that `POST /jobs/byo` with a URL makes zero outbound HTTP calls.

---

*End of document.*
