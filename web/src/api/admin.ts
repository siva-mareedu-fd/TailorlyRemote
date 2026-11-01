import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, call, type Schemas } from './client';

export type AdminSource = Schemas['AdminSourceDto'];
export type AdminCompany = Schemas['AdminCompanyDto'];
export type CoverageReport = Schemas['CoverageReportDto'];
export type AdminStatus = Schemas['AdminStatusDto'];

export const adminKeys = {
  status: ['admin', 'status'] as const,
  sources: ['admin', 'sources'] as const,
  companies: (q: CompanyQuery) => ['admin', 'companies', q] as const,
  coverage: (from: string, to: string, roles: string) => ['admin', 'coverage', from, to, roles] as const,
};

export interface CompanyQuery {
  platform?: string;
  status?: string;
  origin?: string;
  q?: string;
  page: number;
}

/** Admin pages are operational: refetch on focus, no long cache. */
export function useAdminStatus() {
  return useQuery({
    queryKey: adminKeys.status,
    queryFn: () => call(() => api.GET('/api/v1/admin/status')),
    retry: false,
  });
}

export function useAdminSources() {
  return useQuery({
    queryKey: adminKeys.sources,
    queryFn: () => call(() => api.GET('/api/v1/admin/sources')),
    retry: false,
  });
}

export function useUpdateSource() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ key, body }: { key: string; body: Schemas['UpdateSourceRequest'] }) =>
      call(() => api.PUT('/api/v1/admin/sources/{key}', { params: { path: { key } }, body })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin'] }),
  });
}

export function useRunSource() {
  return useMutation({
    mutationFn: (key: string) => call(() => api.POST('/api/v1/admin/sources/{key}/run', { params: { path: { key } } })),
  });
}

export function useAdminCompanies(q: CompanyQuery) {
  return useQuery({
    queryKey: adminKeys.companies(q),
    queryFn: () =>
      call(() =>
        api.GET('/api/v1/admin/companies', {
          params: {
            query: {
              platform: q.platform || undefined,
              status: q.status || undefined,
              origin: q.origin || undefined,
              q: q.q || undefined,
              page: q.page,
              pageSize: 20,
            },
          },
        }),
      ),
    placeholderData: keepPreviousData,
    retry: false,
  });
}

export function useAddCompany() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['AddCompanyRequest']) => call(() => api.POST('/api/v1/admin/companies', { body })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'companies'] }),
  });
}

export function useSetCompanyStatus() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ linkId, status }: { linkId: string; status: string }) =>
      call(() => api.PUT('/api/v1/admin/companies/{linkId}', { params: { path: { linkId } }, body: { status } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'companies'] }),
  });
}

export function useVerifyCompany() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (linkId: string) =>
      call(() => api.POST('/api/v1/admin/companies/{linkId}/verify', { params: { path: { linkId } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'companies'] }),
  });
}

export function useLoadSeed() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => call(() => api.POST('/api/v1/admin/companies/seed/load')),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'companies'] }),
  });
}

export function useCoverage(from: string, to: string, roles: string) {
  return useQuery({
    queryKey: adminKeys.coverage(from, to, roles),
    queryFn: () =>
      call(() =>
        api.GET('/api/v1/admin/coverage', {
          params: { query: { from: from || undefined, to: to || undefined, roles: roles || undefined } },
        }),
      ),
    placeholderData: keepPreviousData,
    retry: false,
  });
}

// ---------- M6: AI config, flags, invite codes, users, usage ----------

export type AiTaskConfig = Schemas['AiTaskConfigDto'];
export type FeatureFlagRow = Schemas['FeatureFlagDto'];
export type InviteCode = Schemas['InviteCodeDto'];
export type AdminUser = Schemas['AdminUserRow'];
export type AdminUsage = Schemas['AdminUsageDto'];

export function useAiConfig() {
  return useQuery({
    queryKey: ['admin', 'ai-config'],
    queryFn: () => call(() => api.GET('/api/v1/admin/ai-config')),
    retry: false,
  });
}

/** Fields left out are unchanged. */
export function useUpdateAiConfig() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ task, body }: { task: string; body: Partial<Schemas['UpdateAiConfigRequest']> }) =>
      call(() =>
        api.PUT('/api/v1/admin/ai-config/{task}', {
          params: { path: { task } },
          body: {
            model: null,
            fallbackModel: null,
            maxTokens: null,
            temperature: null,
            bucket: null,
            enabled: null,
            ...body,
          },
        }),
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'ai-config'] }),
  });
}

export function useFlags() {
  return useQuery({
    queryKey: ['admin', 'flags'],
    queryFn: () => call(() => api.GET('/api/v1/admin/flags')),
    retry: false,
  });
}

export function useUpdateFlag() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: unknown }) =>
      call(() => api.PUT('/api/v1/admin/flags/{key}', { params: { path: { key } }, body: { value } })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['admin', 'flags'] });
      void qc.invalidateQueries({ queryKey: ['meta'] });
    },
  });
}

export function useInviteCodes() {
  return useQuery({
    queryKey: ['admin', 'invite-codes'],
    queryFn: () => call(() => api.GET('/api/v1/admin/invite-codes')),
    retry: false,
  });
}

export function useCreateInviteCode() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: Schemas['CreateInviteCodeRequest']) =>
      call(() => api.POST('/api/v1/admin/invite-codes', { body })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'invite-codes'] }),
  });
}

export function useDeleteInviteCode() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (code: string) =>
      call(() => api.DELETE('/api/v1/admin/invite-codes/{code}', { params: { path: { code } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'invite-codes'] }),
  });
}

export interface UserQuery {
  q?: string;
  status?: string;
  page: number;
}

export function useAdminUsers(q: UserQuery) {
  return useQuery({
    queryKey: ['admin', 'users', q],
    queryFn: () =>
      call(() =>
        api.GET('/api/v1/admin/users', {
          params: { query: { q: q.q || undefined, status: q.status || undefined, page: q.page, pageSize: 20 } },
        }),
      ),
    placeholderData: keepPreviousData,
    retry: false,
  });
}

export function useSetSuspended() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, suspended }: { id: string; suspended: boolean }) =>
      call(() =>
        suspended
          ? api.POST('/api/v1/admin/users/{id}/suspend', { params: { path: { id } } })
          : api.POST('/api/v1/admin/users/{id}/unsuspend', { params: { path: { id } } }),
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'users'] }),
  });
}

export function useAdminUsage(period: string) {
  return useQuery({
    queryKey: ['admin', 'usage', period],
    queryFn: () => call(() => api.GET('/api/v1/admin/usage', { params: { query: { period: period || undefined } } })),
    placeholderData: keepPreviousData,
    retry: false,
  });
}
