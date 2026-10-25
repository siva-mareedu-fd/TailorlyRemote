/** Only same-site paths are followed after sign-in (no open redirect via `?next=`). */
export function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/\\') ? next : '/';
}
