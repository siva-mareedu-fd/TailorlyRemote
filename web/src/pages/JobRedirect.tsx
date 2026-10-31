import { Navigate, useParams } from 'react-router';

/** Job detail lives in the Shortlist's detail pane; links to /jobs/:id open it there. */
export function JobRedirect() {
  const { id } = useParams();
  return <Navigate to={`/shortlist?job=${id ?? ''}`} replace />;
}
