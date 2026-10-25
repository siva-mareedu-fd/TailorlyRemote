import { BrandMark } from '@/components/BrandMark';
import { Alert } from '@/components/Feedback';

/**
 * Router error element: replaces React Router's developer screen. Shows no error details (they can contain user
 * data); the browser console still has them. Links are plain anchors so the app reloads with a fresh state.
 */
export function ErrorPage() {
  return (
    <div className="mx-auto w-full max-w-narrow px-4 pt-[26px] shell:px-8">
      <BrandMark tone="page" className="mb-6 text-[17px]" />
      <h1>Something went wrong</h1>
      <p className="mt-[3px] mb-[18px] text-[13px] text-muted">
        This page hit an unexpected problem. Your data is safe.
      </p>
      <Alert
        tone="bad"
        action={
          <a className="text-[13px] font-medium text-accent hover:underline" href={window.location.href}>
            Reload
          </a>
        }
      >
        Try reloading the page, or <a href="/">go to the start page</a>.
      </Alert>
    </div>
  );
}
