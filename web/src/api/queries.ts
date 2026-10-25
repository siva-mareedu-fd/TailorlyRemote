import { useQuery } from '@tanstack/react-query';
import { api } from './client';

/** `GET /api/v1/meta`: version, consent version, tones and limits for the SPA. */
export function useMeta() {
  return useQuery({
    queryKey: ['meta'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/meta');
      if (error || !data) throw new Error('meta unavailable');
      return data;
    },
    staleTime: 5 * 60_000,
  });
}
