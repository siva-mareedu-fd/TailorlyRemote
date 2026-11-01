import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, call } from './client';
import { keys } from './queries';

const tfKey = ['me', 'two-factor'] as const;

/** Two-step sign-in (TOTP). Enabling or disabling rotates the session, so `me` is refetched afterwards. */
export function useTwoFactor(enabled = true) {
  return useQuery({
    queryKey: tfKey,
    queryFn: () => call(() => api.GET('/api/v1/me/two-factor')),
    enabled,
  });
}

function useRefreshAfter() {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: tfKey });
    void qc.invalidateQueries({ queryKey: keys.me, exact: true });
  };
}

export function useSetupTwoFactor() {
  return useMutation({ mutationFn: () => call(() => api.POST('/api/v1/me/two-factor/setup')) });
}

export function useEnableTwoFactor() {
  const refresh = useRefreshAfter();
  return useMutation({
    mutationFn: (code: string) => call(() => api.POST('/api/v1/me/two-factor/enable', { body: { code } })),
    onSuccess: refresh,
  });
}

export function useDisableTwoFactor() {
  const refresh = useRefreshAfter();
  return useMutation({
    mutationFn: (password: string) => call(() => api.POST('/api/v1/me/two-factor/disable', { body: { password } })),
    onSuccess: refresh,
  });
}

export function useNewRecoveryCodes() {
  const refresh = useRefreshAfter();
  return useMutation({
    mutationFn: (password: string) =>
      call(() => api.POST('/api/v1/me/two-factor/recovery-codes', { body: { password } })),
    onSuccess: refresh,
  });
}
