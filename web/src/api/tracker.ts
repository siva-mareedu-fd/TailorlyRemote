import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError, call, type Schemas } from './client';
import { keys } from './queries';
import { isBusy, tailorKeys, type UserJob } from './tailoring';

export type InterviewPrep = Schemas['InterviewPrepDto'];
export type TrackerStatus = 'saved' | 'tailored' | 'applied' | 'interviewing' | 'offer' | 'rejected' | 'withdrawn';

/** Board columns in order (PRD F11). */
export const statuses: [TrackerStatus, string][] = [
  ['saved', 'Saved'],
  ['tailored', 'Tailored'],
  ['applied', 'Applied'],
  ['interviewing', 'Interviewing'],
  ['offer', 'Offer'],
  ['rejected', 'Rejected'],
  ['withdrawn', 'Withdrawn'],
];

export const statusLabel = (s: string) => statuses.find(([v]) => v === s)?.[1] ?? s;

export const trackerKeys = {
  list: ['user-jobs', 'list'] as const,
  prep: (id: string) => ['user-jobs', id, 'prep'] as const,
};

/** ApiDesign §3 polling: every 1.5 s for 10 s, then every 4 s, up to 3 minutes, only while something is busy. */
function poll(busy: boolean, startedAt: number): number | false {
  if (!busy) return false;
  const elapsed = Date.now() - startedAt;
  if (elapsed > 3 * 60_000) return false;
  return elapsed < 10_000 ? 1_500 : 4_000;
}

/** Every tracker entry of the caller (pages of 100 until all are read), most recently updated first. */
export function useUserJobs() {
  return useQuery({
    queryKey: trackerKeys.list,
    queryFn: async () => {
      const all: UserJob[] = [];
      for (let page = 1; ; page++) {
        const res = await call(() => api.GET('/api/v1/user-jobs', { params: { query: { page, pageSize: 100 } } }));
        all.push(...res.items);
        if (all.length >= res.total || res.items.length === 0) return all;
      }
    },
  });
}

/** One entry, polled while its pasted job is being enriched and scored (BYO). */
export function useUserJobPolling(id: string | null, startedAt: number) {
  return useQuery({
    queryKey: tailorKeys.userJob(id ?? ''),
    queryFn: () => call(() => api.GET('/api/v1/user-jobs/{id}', { params: { path: { id: id! } } })),
    enabled: !!id,
    retry: false,
    refetchInterval: (q) => poll(isBusy(q.state.data?.enrichmentStatus), startedAt),
  });
}

/**
 * Moves an entry or edits its notes. The concurrency token comes from the cached entry; when it is stale because the
 * server changed the entry meanwhile (a finished analysis records its score), a status move is retried once with the
 * fresh token, since the user's intent is unambiguous. Notes are never retried silently.
 */
export function useUpdateUserJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { entry: UserJob; status?: TrackerStatus; notes?: string }) => {
      const send = (token: string) =>
        call(() =>
          api.PATCH('/api/v1/user-jobs/{id}', {
            params: { path: { id: v.entry.id } },
            body: { status: v.status ?? null, notes: v.notes ?? null, concurrencyToken: token },
          }),
        );
      try {
        return await send(v.entry.concurrencyToken);
      } catch (e) {
        if (!(e instanceof ApiError && e.code === 'CONCURRENT_UPDATE') || v.notes !== undefined) throw e;
        const fresh = await call(() => api.GET('/api/v1/user-jobs/{id}', { params: { path: { id: v.entry.id } } }));
        return await send(fresh.concurrencyToken);
      }
    },
    onMutate: async (v) => {
      await qc.cancelQueries({ queryKey: trackerKeys.list });
      const previous = qc.getQueryData<UserJob[]>(trackerKeys.list);
      if (v.status)
        qc.setQueryData<UserJob[]>(trackerKeys.list, (l) =>
          l?.map((x) => (x.id === v.entry.id ? { ...x, status: v.status! } : x)),
        );
      return { previous };
    },
    onError: (_e, _v, context) => qc.setQueryData(trackerKeys.list, context?.previous),
    onSuccess: (uj) => {
      qc.setQueryData(tailorKeys.userJob(uj.id), uj);
      qc.setQueryData<UserJob[]>(trackerKeys.list, (l) => l?.map((x) => (x.id === uj.id ? uj : x)));
    },
  });
}

export function useDeleteUserJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => call(() => api.DELETE('/api/v1/user-jobs/{id}', { params: { path: { id } } })),
    onSuccess: (_r, id) => {
      qc.setQueryData<UserJob[]>(trackerKeys.list, (l) => l?.filter((x) => x.id !== id));
      qc.removeQueries({ queryKey: tailorKeys.userJob(id) });
      void qc.invalidateQueries({ queryKey: ['jobs'] });
    },
  });
}

/** `POST /jobs/byo`: the pasted job becomes a Saved entry, enriched and scored in the background. */
export function useAddByoJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { description: string; url: string | null; title: string | null; companyName: string | null }) =>
      call(() => api.POST('/api/v1/jobs/byo', { body })),
    onSuccess: (uj) => {
      qc.setQueryData(tailorKeys.userJob(uj.id), uj);
      void qc.invalidateQueries({ queryKey: trackerKeys.list });
      void qc.invalidateQueries({ queryKey: keys.usage });
    },
  });
}

/** The entry's interview prep; `null` when never generated (404). Polls while it runs. */
export function useInterviewPrep(userJobId: string, startedAt: number) {
  return useQuery({
    queryKey: trackerKeys.prep(userJobId),
    queryFn: async (): Promise<InterviewPrep | null> => {
      try {
        return await call(() =>
          api.GET('/api/v1/user-jobs/{id}/interview-prep', { params: { path: { id: userJobId } } }),
        );
      } catch (e) {
        if (e instanceof ApiError && e.status === 404 && e.code === 'NOT_FOUND') return null;
        throw e;
      }
    },
    retry: false,
    refetchInterval: (q) => poll(isBusy(q.state.data?.status), startedAt),
  });
}

export function useStartInterviewPrep(userJobId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      call(() => api.POST('/api/v1/user-jobs/{id}/interview-prep', { params: { path: { id: userJobId } } })),
    onSuccess: (p) => {
      qc.setQueryData(trackerKeys.prep(userJobId), p);
      void qc.invalidateQueries({ queryKey: keys.usage });
    },
  });
}
