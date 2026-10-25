import { Link } from 'react-router';
import { useMeta } from '@/api/queries';
import { Alert } from '@/components/Feedback';

/**
 * Privacy notice the consent refers to (PRD F1). It must state that résumé and job text are sent to a third-party AI
 * provider. The wording still needs legal review before public launch (README, open items).
 */
export function PrivacyPage() {
  const meta = useMeta();
  return (
    <article className="grid gap-3 text-sm">
      <h2 className="text-[22px] tracking-[-0.02em]">Privacy Notice</h2>
      <p className="text-muted">Version {meta.data?.consentVersion ?? '…'}</p>
      <Alert tone="info">
        Your résumé text and the jobs you analyse are sent to an AI provider (Anthropic) to create matches and
        documents. Your résumé is never shown to other users.
      </Alert>
      <p>
        <b>What we store.</b> Your name, email, time zone, the résumé files you upload, the profile we read from them,
        your preferences, and how many AI tokens you use. Files are kept in private storage and are only downloaded
        through your signed-in account.
      </p>
      <p>
        <b>Who processes it.</b> Anthropic (AI processing of résumé and job text), our email provider (account and
        digest emails) and our hosting provider.
      </p>
      <p>
        <b>Your choices.</b> You can correct your profile, delete any résumé version, and delete your account at any
        time from Account. Deleting your account removes your files and data; backups roll off within 30 days.
      </p>
      <p>
        <Link to="/signup">Back</Link>
      </p>
    </article>
  );
}

export function TermsPage() {
  return (
    <article className="grid gap-3 text-sm">
      <h2 className="text-[22px] tracking-[-0.02em]">Terms</h2>
      <p>
        Tailorly Remote suggests jobs and edits to your résumé. You decide what to apply with: every change is shown to
        you before it is used, and scores are estimates. Use the service only with résumés and information that are
        yours to share.
      </p>
      <p>
        <Link to="/signup">Back</Link>
      </p>
    </article>
  );
}
