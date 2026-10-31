import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError, call, type Schemas } from './client';
import { keys } from './queries';

export type UserJob = Schemas['UserJobDto'];
export type Analysis = Schemas['AnalysisDto'];
export type TailoredDocument = Schemas['TailoredDocumentDto'];
export type TailoredEdit = Schemas['TailoredEditDto'];
export type Decision = 'accepted' | 'rejected' | 'pending';

export const tailorKeys = {
  userJob: (id: string) => ['user-jobs', id] as const,
  analysis: (id: string) => ['user-jobs', id, 'analysis'] as const,
  documents: (id: string) => ['user-jobs', id, 'documents'] as const,
};

/** Queued, running or finalizing: still being worked on (ApiDesign §3; finalizing is the résumé's extra step). */
export const isBusy = (status: string | undefined | null) =>
  status === 'queued' || status === 'running' || status === 'finalizing';

/** ApiDesign §3 polling: every 1.5 s for 10 s, then every 4 s, up to 3 minutes, only while something is busy. */
function poll(busy: boolean, startedAt: number): number | false {
  if (!busy) return false;
  const elapsed = Date.now() - startedAt;
  if (elapsed > 3 * 60_000) return false;
  return elapsed < 10_000 ? 1_500 : 4_000;
}

export function useUserJob(id: string) {
  return useQuery({
    queryKey: tailorKeys.userJob(id),
    queryFn: () => call(() => api.GET('/api/v1/user-jobs/{id}', { params: { path: { id } } })),
    retry: false,
  });
}

/** The latest analysis; `null` when it has never run (404). Polls while it runs. */
export function useAnalysis(userJobId: string, startedAt: number) {
  return useQuery({
    queryKey: tailorKeys.analysis(userJobId),
    queryFn: async (): Promise<Analysis | null> => {
      try {
        return await call(() => api.GET('/api/v1/user-jobs/{id}/analysis', { params: { path: { id: userJobId } } }));
      } catch (e) {
        if (e instanceof ApiError && e.status === 404 && e.code === 'NOT_FOUND') return null;
        throw e;
      }
    },
    retry: false,
    refetchInterval: (q) => poll(isBusy(q.state.data?.status), startedAt),
  });
}

/** Every document of the tracked job (both kinds, newest first). Polls while the newest of either kind is busy. */
export function useTailoredDocuments(userJobId: string, startedAt: number) {
  return useQuery({
    queryKey: tailorKeys.documents(userJobId),
    queryFn: () =>
      call(() => api.GET('/api/v1/user-jobs/{id}/tailored-documents', { params: { path: { id: userJobId } } })),
    refetchInterval: (q) => {
      const docs = q.state.data ?? [];
      const latest = ['resume', 'cover_letter'].map((k) => docs.find((d) => d.kind === k));
      return poll(
        latest.some((d) => isBusy(d?.status)),
        startedAt,
      );
    },
  });
}

/** Tracks a job scored for the caller (idempotent: tracking it again returns the same entry). */
export function useTrackJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (jobId: string) => call(() => api.POST('/api/v1/user-jobs', { body: { jobId } })),
    onSuccess: (uj) => {
      qc.setQueryData(tailorKeys.userJob(uj.id), uj);
      void qc.invalidateQueries({ queryKey: ['jobs'] });
    },
  });
}

export function useStartAnalysis(userJobId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      call(() =>
        api.POST('/api/v1/user-jobs/{id}/analysis', { params: { path: { id: userJobId } }, body: { resumeId: null } }),
      ),
    onSuccess: (a) => qc.setQueryData(tailorKeys.analysis(userJobId), a),
  });
}

export function usePasteDescription(userJobId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (description: string) =>
      call(() =>
        api.PUT('/api/v1/user-jobs/{id}/description', { params: { path: { id: userJobId } }, body: { description } }),
      ),
    onSuccess: (uj) => qc.setQueryData(tailorKeys.userJob(userJobId), uj),
  });
}

function upsert(list: TailoredDocument[] | undefined, doc: TailoredDocument): TailoredDocument[] {
  const rest = (list ?? []).filter((d) => d.id !== doc.id);
  return [doc, ...rest].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

export function useStartTailoring(userJobId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      call(() =>
        api.POST('/api/v1/user-jobs/{id}/tailored-resumes', {
          params: { path: { id: userJobId } },
          body: { resumeId: null },
        }),
      ),
    onSuccess: (d) => qc.setQueryData<TailoredDocument[]>(tailorKeys.documents(userJobId), (l) => upsert(l, d)),
  });
}

/** Accept, reject or reset edits; the list is updated at once and corrected by the server's answer. */
export function useDecide(userJobId: string, documentId: string) {
  const qc = useQueryClient();
  const key = tailorKeys.documents(userJobId);
  return useMutation({
    mutationFn: (decisions: { editId: string; decision: Decision }[]) =>
      call(() =>
        api.POST('/api/v1/tailored-documents/{id}/decisions', {
          params: { path: { id: documentId } },
          body: { decisions },
        }),
      ),
    onMutate: async (decisions) => {
      await qc.cancelQueries({ queryKey: key });
      const previous = qc.getQueryData<TailoredDocument[]>(key);
      qc.setQueryData<TailoredDocument[]>(key, (list) =>
        list?.map((d) =>
          d.id !== documentId
            ? d
            : {
                ...d,
                edits: d.edits.map((e) => {
                  const change = decisions.find((x) => x.editId === e.id);
                  return change ? { ...e, decision: change.decision } : e;
                }),
              },
        ),
      );
      return { previous };
    },
    onError: (_e, _d, context) => qc.setQueryData(key, context?.previous),
    onSuccess: (d) => qc.setQueryData<TailoredDocument[]>(key, (l) => upsert(l, d)),
  });
}

export function useFinalize(userJobId: string, documentId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      call(() => api.POST('/api/v1/tailored-documents/{id}/finalize', { params: { path: { id: documentId } } })),
    onSuccess: (d) => {
      qc.setQueryData<TailoredDocument[]>(tailorKeys.documents(userJobId), (l) => upsert(l, d));
      void qc.invalidateQueries({ queryKey: tailorKeys.userJob(userJobId) });
    },
  });
}

export function useStartCoverLetter(userJobId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { tone: string; notes: string | null }) =>
      call(() =>
        api.POST('/api/v1/user-jobs/{id}/cover-letters', {
          params: { path: { id: userJobId } },
          body: { ...body, resumeId: null },
        }),
      ),
    onSuccess: (d) => qc.setQueryData<TailoredDocument[]>(tailorKeys.documents(userJobId), (l) => upsert(l, d)),
  });
}

export function useSaveLetter(userJobId: string, documentId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (paragraphs: string[]) =>
      call(() =>
        api.PUT('/api/v1/tailored-documents/{id}/content', {
          params: { path: { id: documentId } },
          body: { paragraphs },
        }),
      ),
    onSuccess: (d) => qc.setQueryData<TailoredDocument[]>(tailorKeys.documents(userJobId), (l) => upsert(l, d)),
  });
}

/** Saves a skill to the user's confirmed skills (the only way a new skill may enter a tailored résumé). */
export function useConfirmSkill(userJobId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (skill: string) => call(() => api.POST('/api/v1/me/confirmed-skills', { body: { skill } })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.skills });
      void qc.invalidateQueries({ queryKey: tailorKeys.analysis(userJobId) });
    },
  });
}

/** Same-origin download link (cookie auth); the server sets the file name and `Content-Disposition: attachment`. */
export const downloadUrl = (documentId: string, format: 'docx' | 'pdf' = 'docx') =>
  `/api/v1/tailored-documents/${documentId}/download${format === 'pdf' ? '?format=pdf' : ''}`;
