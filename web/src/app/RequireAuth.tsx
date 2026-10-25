import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useLogout, useMe, useResumes } from '@/api/queries';
import { Alert, LoadError, PageLoading } from '@/components/Feedback';
import { Button } from '@/components/ui/button';

/** Signed-in, active account required; otherwise to /signin with `?next=` (same-site only). */
export function RequireAuth({ children }: { children: ReactNode }) {
  const me = useMe();
  const logout = useLogout();
  const location = useLocation();

  if (me.isPending) return <PageLoading />;
  if (me.isError)
    return (
      <div className="page narrow">
        <LoadError error={me.error} onRetry={() => void me.refetch()} />
      </div>
    );
  if (!me.data) {
    const next = encodeURIComponent(location.pathname + location.search);
    return <Navigate to={`/signin?next=${next}`} replace />;
  }
  if (me.data.accountStatus !== 'active')
    return (
      <div className="page narrow">
        <Alert
          tone="warn"
          action={
            <Button size="sm" onClick={() => logout.mutate()}>
              Sign out
            </Button>
          }
        >
          <b>This account is not active.</b> It is being deleted or has been suspended.
        </Alert>
      </div>
    );
  return <>{children}</>;
}

/** `/`: new users go to onboarding until a résumé is parsed; everyone else to the shortlist. */
export function HomeRedirect() {
  const me = useMe();
  const verified = me.data?.emailVerified === true;
  const resumes = useResumes(verified);
  if (!verified) return <Navigate to="/onboarding" replace />;
  if (resumes.isPending) return <PageLoading />;
  const hasActive = resumes.data?.some((r) => r.isActive && r.parseStatus === 'ready');
  return <Navigate to={hasActive ? '/shortlist' : '/onboarding'} replace />;
}
