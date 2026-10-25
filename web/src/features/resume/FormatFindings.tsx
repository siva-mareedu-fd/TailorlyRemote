import type { Resume } from '@/api/queries';
import { Alert } from '@/components/Feedback';

type Findings = NonNullable<Resume['formatFindings']>;

/**
 * Layout findings as plain advice (PRD F2/F8 format health). Informational only: we never restructure the layout.
 * `compact` shows only the warnings (onboarding step 2).
 */
export function FormatFindingsList({ findings, compact = false }: { findings: Findings; compact?: boolean }) {
  const items: { tone: 'warn' | 'info' | 'ok'; title: string; text: string }[] = [];
  if (findings.trackedChanges)
    items.push({
      tone: 'warn',
      title: 'Tracked changes found.',
      text: 'Accept or reject all changes in Word (Review → Accept all), save, and upload again so we read the final text.',
    });
  if (findings.hasColumns)
    items.push({
      tone: 'warn',
      title: 'Two-column layout.',
      text: 'Some ATS tools read columns out of order. A single column is safer.',
    });
  if (findings.hasTables)
    items.push({ tone: 'warn', title: 'Tables found.', text: 'Some ATS tools skip or scramble text inside tables.' });
  if (findings.headerFooterContact)
    items.push({
      tone: 'warn',
      title: 'Contact details in the header or footer.',
      text: 'Many ATS tools ignore headers and footers. Put your email and phone in the main body.',
    });
  if (findings.hasTextBoxes)
    items.push({ tone: 'info', title: 'Text box found.', text: 'Text in boxes is read for scoring but never edited.' });
  if (findings.hasImages)
    items.push({
      tone: 'info',
      title: 'Images found.',
      text: 'ATS tools cannot read images; keep important text out of them.',
    });
  if (!compact)
    items.push({
      tone: 'ok',
      title: '',
      text: `${findings.editableParagraphs} paragraphs can be tailored. ${findings.lockedParagraphs} are locked (headings, dates, company names, contact details).`,
    });

  if (items.length === 0) return compact ? null : <p className="help">No layout issues found.</p>;
  return (
    <div className="stack">
      {items.map((i) => (
        <Alert key={i.title || i.text} tone={i.tone}>
          {i.title && <b>{i.title} </b>}
          {i.text}
        </Alert>
      ))}
    </div>
  );
}
