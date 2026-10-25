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
