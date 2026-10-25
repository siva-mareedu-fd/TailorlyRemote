import createClient, { type Middleware } from 'openapi-fetch';
import type { paths } from './schema';

const UNSAFE = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function readCookie(name: string): string | null {
  const match = document.cookie.split('; ').find((c) => c.startsWith(name + '='));
  return match ? decodeURIComponent(match.slice(name.length + 1)) : null;
}

/** Double-submit CSRF: echo the readable XSRF-TOKEN cookie in X-XSRF-TOKEN on unsafe requests (ApiDesign §1). */
const csrf: Middleware = {
  onRequest({ request }) {
    if (UNSAFE.has(request.method)) {
      const token = readCookie('XSRF-TOKEN');
      if (token) request.headers.set('X-XSRF-TOKEN', token);
    }
    return request;
  },
};

/** Typed client generated from the backend OpenAPI document (`npm run gen:api`). Same origin, cookie auth. */
export const api = createClient<paths>({ baseUrl: '', credentials: 'same-origin' });
api.use(csrf);

/** Issues the CSRF cookie. Call once on load and again after sign-in (tokens are bound to the user). */
export async function ensureCsrf() {
  await api.GET('/api/v1/auth/csrf');
}

/** RFC 7807 body returned by the API for every error. */
export interface ApiProblem {
  type: string;
  title: string;
  status: number;
  code: string;
  detail?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  extra?: Record<string, unknown>;
}
