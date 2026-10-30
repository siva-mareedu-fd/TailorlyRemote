import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { api, ApiError, call, ensureCsrf, uploadResume, type Schemas } from './client';

export type Me = Schemas['MeResponse'];
export type Resume = Schemas['ResumeDto'];
export type ResumeProfile = Schemas['ResumeProfileDto'];
export type ParsedProfile = Schemas['ParsedProfile'];
export type ProfileEdits = Schemas['ProfileEdits'];
export type Usage = Schemas['UsageResponse'];
export type ConfirmedSkill = Schemas['ConfirmedSkillDto'];
export type Preferences = Schemas['PreferencesDto'];
export type WatchedCompany = Schemas['WatchedCompanyDto'];
export type JobSummary = Schemas['JobSummaryDto'];
export type JobDetail = Schemas['JobDetailDto'];
export type Digest = Schemas['DigestDetailDto'];
export type DigestSummary = Schemas['DigestSummaryDto'];

/** Filters of `GET /jobs` the Shortlist uses (ApiDesign §4.6). */
export interface JobFilters {
  minScore?: number;
  eligibility?: string;
  q?: string;
  digestId?: string;
}

export const keys = {
  meta: ['meta'] as const,
  me: ['me'] as const,
  usage: ['usage'] as const,
  resumes: ['resumes'] as const,
  resume: (id: string) => ['resumes', id] as const,
  profile: (id: string) => ['resumes', id, 'profile'] as const,
  skills: ['confirmed-skills'] as const,
  preferences: ['preferences'] as const,
  watched: ['watched-companies'] as const,
  jobs: (f: JobFilters) => ['jobs', 'list', f] as const,
  job: (id: string) => ['jobs', 'detail', id] as const,
  digests: (page: number, kind?: string) => ['digests', 'list', page, kind ?? 'all'] as const,
  latestDigest: ['digests', 'latest'] as const,
  digest: (id: string) => ['digests', 'detail', id] as const,
};

/** `GET /api/v1/meta`: version, consent version, tones and limits for the SPA. */
export function useMeta() {
  return useQuery({
    queryKey: keys.meta,
    queryFn: () => call(() => api.GET('/api/v1/meta')),
    staleTime: 5 * 60_000,
  });
}

/** The signed-in user, or `null` when signed out (401). Refetched on focus so verifying in another tab shows up. */
export function useMe() {
  return useQuery({
    queryKey: keys.me,
    queryFn: async (): Promise<Me | null> => {
      try {
        return await call(() => api.GET('/api/v1/me'));
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) return null;
        throw e;
      }
    },
    retry: false,
    refetchOnWindowFocus: true,
    staleTime: 30_000,
  });
}

export function useUsage(enabled = true) {
  return useQuery({
    queryKey: keys.usage,
    queryFn: () => call(() => api.GET('/api/v1/usage')),
    enabled,
    staleTime: 30_000,
  });
}

export const isInFlight = (status: string | undefined) => status === 'queued' || status === 'running';

/**
 * Polling cadence for async resources (ApiDesign §3): every 1.5 s for the first 10 s, then every 4 s, up to 3 minutes,
 * and only while the resource is queued or running.
 */
export function pollInterval(query: { state: { data?: Resume } }, startedAt: number): number | false {
  if (!isInFlight(query.state.data?.parseStatus)) return false;
  const elapsed = Date.now() - startedAt;
  if (elapsed > 3 * 60_000) return false;
  return elapsed < 10_000 ? 1_500 : 4_000;
}

/** The same cadence for any async resource with a `status` (digest runs; later analyses and documents). */
export function pollStatus(status: string | undefined, startedAt: number): number | false {
  if (!isInFlight(status)) return false;
  const elapsed = Date.now() - startedAt;
  if (elapsed > 3 * 60_000) return false;
  return elapsed < 10_000 ? 1_500 : 4_000;
}

export function useResumes(enabled = true) {
  return useQuery({
    queryKey: keys.resumes,
    queryFn: () => call(() => api.GET('/api/v1/resumes')),
    enabled,
  });
}

/** One résumé; polls while its parse is in flight. `startedAt` anchors the back-off. */
export function useResume(id: string | null, startedAt: number) {
  return useQuery({
    queryKey: keys.resume(id ?? 'none'),
    queryFn: () => call(() => api.GET('/api/v1/resumes/{id}', { params: { path: { id: id! } } })),
    enabled: id !== null,
    refetchInterval: (q) => pollInterval(q, startedAt),
  });
}

export function useResumeProfile(id: string | null) {
  return useQuery({
    queryKey: keys.profile(id ?? 'none'),
    queryFn: () => call(() => api.GET('/api/v1/resumes/{id}/profile', { params: { path: { id: id! } } })),
    enabled: id !== null,
    retry: false,
  });
}

export function useConfirmedSkills(enabled = true) {
  return useQuery({
    queryKey: keys.skills,
    queryFn: () => call(() => api.GET('/api/v1/me/confirmed-skills')),
    enabled,
  });
}

// ---------- mutations ----------

export function useLogin() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; password: string }) => call(() => api.POST('/api/v1/auth/login', { body })),
    onSuccess: (me) => {
      qc.clear();
      qc.setQueryData(keys.me, me);
    },
  });
}

export function useRegister() {
  return useMutation({
    mutationFn: (body: Schemas['RegisterRequest']) => call(() => api.POST('/api/v1/auth/register', { body })),
  });
}

/**
 * Forget the signed-in user. `me` goes to null first, which notifies every `useMe` observer (so `RequireAuth`
 * redirects), and only then is the rest of the cache dropped. `qc.clear()` first would detach those observers.
 */
function signedOut(qc: QueryClient) {
  qc.setQueryData(keys.me, null);
  qc.removeQueries({ predicate: (q) => q.queryKey[0] !== keys.me[0] });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => call(() => api.POST('/api/v1/auth/logout')),
    onSettled: async () => {
      signedOut(qc);
      await ensureCsrf().catch(() => undefined);
    },
  });
}

export function useResendVerification() {
  return useMutation({ mutationFn: () => call(() => api.POST('/api/v1/auth/resend-verification')) });
}

export function useForgotPassword() {
  return useMutation({
    mutationFn: (email: string) => call(() => api.POST('/api/v1/auth/forgot-password', { body: { email } })),
  });
}

export function useResetPassword() {
  return useMutation({
    mutationFn: (body: { userId: string; token: string; newPassword: string }) =>
      call(() => api.POST('/api/v1/auth/reset-password', { body })),
  });
}

export function useVerifyEmail() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { userId: string; token: string }) =>
      call(() => api.POST('/api/v1/auth/verify-email', { body })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.me }),
  });
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      call(() => api.POST('/api/v1/auth/change-password', { body })),
  });
}

export function useUpdateMe() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { displayName: string; timeZone: string; notificationEmail: string }) =>
      call(() => api.PUT('/api/v1/me', { body })),
    onSuccess: (me) => qc.setQueryData(keys.me, me),
  });
}

export function useRecordConsent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (version: string) => call(() => api.POST('/api/v1/me/consent', { body: { version } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.me }),
  });
}

export function useRequestDeletion() {
  return useMutation({ mutationFn: () => call(() => api.DELETE('/api/v1/account')) });
}

export function useConfirmDeletion() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { userId: string; token: string }) =>
      call(() => api.POST('/api/v1/account/confirm-deletion', { body })),
    onSuccess: () => signedOut(qc),
  });
}

export function useUploadResume() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: uploadResume,
    onSuccess: ({ resume }) => {
      qc.setQueryData(keys.resume(resume.id), resume);
      void qc.invalidateQueries({ queryKey: keys.resumes, exact: true });
    },
  });
}

export function useActivateResume() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => call(() => api.POST('/api/v1/resumes/{id}/activate', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.resumes }),
  });
}

export function useDeleteResume() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => call(() => api.DELETE('/api/v1/resumes/{id}', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.resumes }),
  });
}

export function useUpdateProfile(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (userEdited: ProfileEdits | null) =>
      call(() => api.PUT('/api/v1/resumes/{id}/profile', { params: { path: { id } }, body: { userEdited } })),
    onSuccess: (profile) => qc.setQueryData(keys.profile(id), profile),
  });
}

export function useRemoveConfirmedSkill() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      call(() => api.DELETE('/api/v1/me/confirmed-skills/{id}', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.skills }),
  });
}

// ---------- preferences and watch list (ApiDesign §4.5) ----------

export function usePreferences(enabled = true) {
  return useQuery({
    queryKey: keys.preferences,
    queryFn: () => call(() => api.GET('/api/v1/preferences')),
    enabled,
    staleTime: 30_000,
  });
}

/** Full replacement with the concurrency token; `409 CONCURRENT_UPDATE` when another tab saved first. */
export function useSavePreferences() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: Preferences) => call(() => api.PUT('/api/v1/preferences', { body })),
    onSuccess: (saved) => qc.setQueryData(keys.preferences, saved),
  });
}

export function useWatchedCompanies(enabled = true) {
  return useQuery({
    queryKey: keys.watched,
    queryFn: () => call(() => api.GET('/api/v1/watched-companies')),
    enabled,
  });
}

export function useAddWatchedCompany() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (boardUrl: string) =>
      call(() =>
        api.POST('/api/v1/watched-companies', { body: { boardUrl, platform: null, boardToken: null, name: null } }),
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.watched }),
  });
}

// ---------- shortlist, digests and runs (ApiDesign §4.6) ----------

/** Jobs scored for the caller, filtered; at most 50 (one page), best fit first. */
export function useJobs(f: JobFilters, enabled = true) {
  return useQuery({
    queryKey: keys.jobs(f),
    queryFn: () =>
      call(() =>
        api.GET('/api/v1/jobs', {
          params: {
            query: {
              minScore: f.minScore,
              eligibility: f.eligibility ? [f.eligibility] : undefined,
              q: f.q || undefined,
              digestId: f.digestId,
              pageSize: 50,
            },
          },
        }),
      ),
    enabled,
    placeholderData: (previous) => previous,
  });
}

export function useJob(id: string | null) {
  return useQuery({
    queryKey: keys.job(id ?? 'none'),
    queryFn: () => call(() => api.GET('/api/v1/jobs/{id}', { params: { path: { id: id! } } })),
    enabled: id !== null,
    retry: false,
  });
}

export function useDigests(page: number, kind?: string) {
  return useQuery({
    queryKey: keys.digests(page, kind),
    queryFn: () => call(() => api.GET('/api/v1/digests', { params: { query: { page, pageSize: 20, kind } } })),
    placeholderData: (previous) => previous,
  });
}

/** The most recent run of either kind (the Shortlist header shows when it ran). */
export function useLatestDigest(enabled = true) {
  return useQuery({
    queryKey: keys.latestDigest,
    queryFn: async () =>
      (await call(() => api.GET('/api/v1/digests', { params: { query: { pageSize: 1 } } }))).items[0] ?? null,
    enabled,
  });
}

/** One digest; polls while a run is queued or running. `startedAt` anchors the back-off. */
export function useDigest(id: string | null, startedAt = 0) {
  return useQuery({
    queryKey: keys.digest(id ?? 'none'),
    queryFn: () => call(() => api.GET('/api/v1/digests/{id}', { params: { path: { id: id! } } })),
    enabled: id !== null,
    retry: false,
    refetchInterval: (q) => pollStatus(q.state.data?.status, startedAt),
  });
}

/** "Run now": 202 with the new run, or 200 with the run already in flight. */
export function useStartRun() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => call(() => api.POST('/api/v1/runs')),
    onSuccess: (digest) => {
      qc.setQueryData(keys.digest(digest.id), digest);
      void qc.invalidateQueries({ queryKey: ['digests', 'list'] });
    },
  });
}

/** One-click unsubscribe from the digest email's link (no sign-in needed). */
export function useUnsubscribe() {
  return useMutation({
    mutationFn: (token: string) => call(() => api.POST('/api/v1/email/unsubscribe', { params: { query: { token } } })),
  });
}

export function useVerifyNotificationEmail() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (token: string) => call(() => api.POST('/api/v1/me/notification-email/verify', { body: { token } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.me }),
  });
}

export function useRemoveWatchedCompany() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => call(() => api.DELETE('/api/v1/watched-companies/{id}', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.watched }),
  });
}
