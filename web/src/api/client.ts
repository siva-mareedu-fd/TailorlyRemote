import createClient, { type Middleware } from 'openapi-fetch';
import type { components, paths } from './schema';

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

/** RFC 7807 body returned by the API for every error (ApiDesign §1.1). */
export type ApiProblem = components['schemas']['ApiProblem'];
export type Schemas = components['schemas'];

/** An API error with its stable `code` (ApiDesign §10). */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly detail: string | null;
  readonly fieldErrors: Record<string, string[]>;
  readonly extra: Record<string, unknown>;
  readonly traceId: string | null;

  constructor(status: number, problem: Partial<ApiProblem> | undefined) {
    super(problem?.title ?? `Request failed (${status})`);
    this.status = status;
    this.code = problem?.code ?? (status >= 500 ? 'INTERNAL_ERROR' : 'BAD_REQUEST');
    this.detail = problem?.detail ?? null;
    this.fieldErrors = (problem?.errors as Record<string, string[]> | null | undefined) ?? {};
    this.extra = (problem?.extra as Record<string, unknown> | null | undefined) ?? {};
    this.traceId = problem?.traceId ?? null;
  }

  /** The sentence to show the user: the server's detail, else its title, with the trace id for 5xx. */
  get userMessage(): string {
    const text = this.detail ?? this.message;
    return this.status >= 500 && this.traceId ? `${text} (trace ${this.traceId})` : text;
  }

  field(name: string): string | undefined {
    return this.fieldErrors[name]?.[0];
  }
}

export function isApiError(e: unknown, code?: string): e is ApiError {
  return e instanceof ApiError && (code === undefined || e.code === code);
}

interface FetchResult<T> {
  data?: T;
  error?: unknown;
  response: Response;
}

/**
 * Runs a typed call, returns its data or throws {@link ApiError}. A `CSRF_INVALID` answer refreshes the token and
 * retries once, as ApiDesign §10 prescribes.
 */
export async function call<T>(run: () => Promise<FetchResult<T>>): Promise<T> {
  let result = await run();
  if (!result.response.ok && (result.error as Partial<ApiProblem> | undefined)?.code === 'CSRF_INVALID') {
    await ensureCsrf();
    result = await run();
  }
  if (!result.response.ok) throw new ApiError(result.response.status, result.error as Partial<ApiProblem>);
  return result.data as T;
}

/** `POST /api/v1/resumes` (multipart). openapi-fetch does not build FormData, so this one uses fetch directly. */
export async function uploadResume(file: File): Promise<{ resume: Schemas['ResumeDto']; existing: boolean }> {
  const send = () => {
    const form = new FormData();
    form.append('file', file);
    const headers: Record<string, string> = {};
    const token = readCookie('XSRF-TOKEN');
    if (token) headers['X-XSRF-TOKEN'] = token;
    return fetch('/api/v1/resumes', { method: 'POST', body: form, headers, credentials: 'same-origin' });
  };
  let res = await send();
  if (res.status === 403) {
    const problem = (await res.clone().json()) as Partial<ApiProblem>;
    if (problem.code === 'CSRF_INVALID') {
      await ensureCsrf();
      res = await send();
    }
  }
  const body: unknown = await res.json().catch(() => undefined);
  if (!res.ok) throw new ApiError(res.status, body as Partial<ApiProblem>);
  return { resume: body as Schemas['ResumeDto'], existing: res.status === 200 };
}
