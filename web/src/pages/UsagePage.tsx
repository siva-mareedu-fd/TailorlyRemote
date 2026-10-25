import { useUsage, type Usage } from '@/api/queries';
import { Alert, LoadError, Meter, PageLoading } from '@/components/Feedback';

const nf = new Intl.NumberFormat('en-US');
const BUCKETS: Record<string, { title: string; sub: string }> = {
  discovery: { title: 'Discovery', sub: 'Daily matching and scoring' },
  documents: { title: 'Documents', sub: 'Résumé reading, analysis, tailoring, cover letters, prep' },
};

function monthLabel(periodKey: string) {
  const [y, m] = periodKey.split('-').map(Number);
  return new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(
    new Date(Date.UTC(y, m - 1, 1)),
  );
}

function resetLabel(resetsOn: string) {
  const [y, m, d] = resetsOn.split('-').map(Number);
  return new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(
    new Date(Date.UTC(y, m - 1, d)),
  );
}

/** Usage page (design: #usage): this month's two budgets (PRD F13). Per-task history joins in M5. */
export function UsagePage() {
  const usage = useUsage();
  if (usage.isPending) return <PageLoading />;
  if (usage.isError)
    return (
      <div className="page narrow">
        <LoadError error={usage.error} onRetry={() => void usage.refetch()} />
      </div>
    );
  const u = usage.data;

  return (
    <div className="page narrow">
      <div className="page-title">
        <div>
          <h1>Usage</h1>
          <div className="sub">
            {monthLabel(u.periodKey)} · resets on <b>{resetLabel(u.resetsOn)}</b> at 00:00 IST
          </div>
        </div>
      </div>
      <Warnings usage={u} />
      <div className="grid2 mb-[14px]">
        {u.buckets.map((b) => (
          <div key={b.bucket} className="card">
            <h2>{BUCKETS[b.bucket]?.title ?? b.bucket}</h2>
            <p className="sub">{BUCKETS[b.bucket]?.sub}</p>
            <div className="font-mono text-[26px] font-semibold">
              {nf.format(b.used)} <span className="text-sm text-muted">/ {nf.format(b.limit)}</span>
            </div>
            <div className="mt-2.5">
              <Meter percent={b.percentUsed} label={`${BUCKETS[b.bucket]?.title ?? b.bucket} budget used`} />
            </div>
            <div className="help mt-2">
              {b.percentUsed}% used · {nf.format(b.remaining)} tokens left
            </div>
          </div>
        ))}
      </div>
      <p className="help">
        Cached prompt tokens count at a lower rate. Shared job analysis is paid by the platform and is not counted here.
      </p>
    </div>
  );
}

function Warnings({ usage }: { usage: Usage }) {
  const w = new Set(usage.warnings);
  const resets = resetLabel(usage.resetsOn);
  return (
    <div className="stack mb-[14px]" style={{ gap: 10 }}>
      {w.has('documents_100_percent') && (
        <Alert tone="bad">
          Your Documents budget is used up. Tailoring, cover letters and interview prep pause until {resets}. Your daily
          shortlist keeps working.
        </Alert>
      )}
      {w.has('documents_80_percent') && (
        <Alert tone="warn">
          Your Documents budget is {usage.buckets.find((b) => b.bucket === 'documents')?.percentUsed}% used. At 100%,
          tailoring, cover letters and interview prep pause until it resets. Your daily shortlist keeps working.
        </Alert>
      )}
      {w.has('discovery_100_percent') && (
        <Alert tone="bad">
          Your Discovery budget is used up. Daily shortlists continue without scores until {resets}.
        </Alert>
      )}
      {w.has('discovery_80_percent') && (
        <Alert tone="warn">
          Your Discovery budget is {usage.buckets.find((b) => b.bucket === 'discovery')?.percentUsed}% used. At 100%,
          daily shortlists continue without scores until it resets.
        </Alert>
      )}
    </div>
  );
}
