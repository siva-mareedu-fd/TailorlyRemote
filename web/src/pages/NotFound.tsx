import { Link } from 'react-router';

export function NotFoundPage() {
  return (
    <div className="mx-auto w-full max-w-narrow px-4 pt-[26px] shell:px-8">
      <h1>Page not found</h1>
      <p className="mt-[3px] mb-[18px] text-[13px] text-muted">The page you asked for does not exist.</p>
      <Link to="/shortlist">Go to your shortlist</Link>
    </div>
  );
}
