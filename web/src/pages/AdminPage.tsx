import { useState, type FormEvent } from 'react';
import { isApiError } from '@/api/client';
import {
  useAddCompany,
  useAdminCompanies,
  useAdminSources,
  useAdminStatus,
  useCoverage,
  useLoadSeed,
  useRunSource,
  useSetCompanyStatus,
  useUpdateSource,
  useVerifyCompany,
  type AdminSource,
  type CompanyQuery,
} from '@/api/admin';
import { useSignedInMe } from '@/app/signedIn';
import { Switch } from '@/components/Controls';
import { Alert, LoadError, Skeleton } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';

const TABS = [
  { id: 'status', label: 'Status' },
  { id: 'sources', label: 'Sources' },
  { id: 'companies', label: 'Companies' },
  { id: 'coverage', label: 'Coverage' },
  { id: 'ai', label: 'AI config' },
  { id: 'flags', label: 'Flags' },
] as const;
type Tab = (typeof TABS)[number]['id'];

const nf = new Intl.NumberFormat('en-IN');

/** PUT /admin/sources/{key} changes only the fields that are not null. */
const noChange = {
  enabled: null,
  pollIntervalMinutes: null,
  termsReviewedAt: null,
  clearTermsReview: null,
  attributionText: null,
  attributionUrl: null,
};
const when = new Intl.DateTimeFormat('en-IN', { dateStyle: 'medium', timeStyle: 'short' });

function ago(iso: string | null | undefined): string {
  if (!iso) return '—';
  const minutes = Math.round((Date.now() - new Date(iso).getTime()) / 60_000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  if (minutes < 48 * 60) return `${Math.round(minutes / 60)} h ago`;
  return when.format(new Date(iso));
}

function useErrorToast() {
  const say = useToast();
  return (e: unknown) => say(isApiError(e) ? `${e.code}: ${e.userMessage}` : 'Something went wrong');
}

/** Admin console (design: #admin; PRD F15). Counts and ids only, never résumé or job content. */
export function AdminPage() {
  const me = useSignedInMe();
  const [tab, setTab] = useState<Tab>('status');

  if (!me.roles.includes('Admin'))
    return (
      <div className="page">
        <Alert tone="warn">This page is for administrators.</Alert>
      </div>
    );

  return (
    <div className="page">
      <div className="page-title">
        <div>
          <h1>Admin console</h1>
          <div className="sub">Operations view. Shows counts and ids only, never résumé or job content.</div>
        </div>
        <span className="chip warn">admin only</span>
      </div>
      <div className="tabs" role="tablist">
        {TABS.map((t) => (
          <button key={t.id} role="tab" type="button" aria-selected={tab === t.id} onClick={() => setTab(t.id)}>
            {t.label}
          </button>
        ))}
      </div>
      <div role="tabpanel">
        {tab === 'status' && <StatusTab />}
        {tab === 'sources' && <SourcesTab />}
        {tab === 'companies' && <CompaniesTab />}
        {tab === 'coverage' && <CoverageTab />}
        {(tab === 'ai' || tab === 'flags') && (
          <div className="empty">
            This tab is built in <span className="font-mono">M6</span>.
          </div>
        )}
      </div>
    </div>
  );
}

function StatusTab() {
  const status = useAdminStatus();
  if (status.isPending) return <Skeleton lines={6} />;
  if (status.isError) return <LoadError error={status.error} onRetry={() => void status.refetch()} />;
  const s = status.data;
  const tiles: [string, string][] = [
    ['Jobs first seen (24 h)', nf.format(s.jobs.firstSeenLast24h)],
    ['Open listings (unique)', nf.format(s.jobs.openCanonical)],
    ['Failed background jobs', s.queue ? nf.format(s.queue.failed) : '—'],
    ['Disk used', s.disk ? `${s.disk.usedPercent}%` : '—'],
  ];
  return (
    <>
      <div className="grid3 mb-[14px]">
        {tiles.map(([label, value]) => (
          <div key={label} className="card">
            <div className="help">{label}</div>
            <div className="font-mono text-[24px] font-semibold">{value}</div>
          </div>
        ))}
      </div>
      <h3 className="label-k">Source health</h3>
      <div className="tablewrap">
        <table>
          <thead>
            <tr>
              <th>Source</th>
              <th>Enabled</th>
              <th>Last success</th>
              <th>Last failure</th>
              <th>Failures in a row</th>
              <th>Circuit</th>
            </tr>
          </thead>
          <tbody>
            {s.sources.map((src) => {
              const open = src.health.circuitOpenUntil && new Date(src.health.circuitOpenUntil) > new Date();
              return (
                <tr key={src.key}>
                  <td>{src.displayName}</td>
                  <td>{src.enabled ? <span className="chip ok">on</span> : <span className="chip">off</span>}</td>
                  <td className="text-muted">{ago(src.health.lastSuccessAt)}</td>
                  <td className="text-muted" title={src.health.lastError ?? undefined}>
                    {ago(src.health.lastFailureAt)}
                    {src.health.lastError && <div className="help font-mono">{src.health.lastError}</div>}
                  </td>
                  <td className="font-mono">{src.health.consecutiveFailures}</td>
                  <td>
                    {open ? (
                      <span className="chip bad">open until {when.format(new Date(src.health.circuitOpenUntil!))}</span>
                    ) : (
                      <span className="text-muted">closed</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <Alert tone="info" className="mt-[14px]">
        Boards: <b>{s.boards.resolved}</b> resolved, {s.boards.notFound} not found, {s.boards.blocked} blocked. Listings
        open with limited descriptions: {nf.format(s.jobs.limited)} (full: {nf.format(s.jobs.full)}). Stuck resources:{' '}
        {s.stuckResources}. Platform enrichment today: {nf.format(s.platformEnrichmentTokensToday)}
        {s.platformEnrichmentCeiling ? ` of ${nf.format(s.platformEnrichmentCeiling)}` : ''} tokens.
      </Alert>
      {s.ai.length > 0 && (
        <>
          <h3 className="label-k mt-[18px]">AI calls (24 h)</h3>
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>Task</th>
                  <th>Model</th>
                  <th>Calls</th>
                  <th>Errors</th>
                  <th style={{ textAlign: 'right' }}>Tokens</th>
                </tr>
              </thead>
              <tbody>
                {s.ai.map((a) => (
                  <tr key={a.task + a.model}>
                    <td className="font-mono">{a.task}</td>
                    <td className="font-mono">{a.model}</td>
                    <td className="font-mono">{a.calls}</td>
                    <td className="font-mono">{a.errors}</td>
                    <td className="font-mono" style={{ textAlign: 'right' }}>
                      {nf.format(a.tokens)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  );
}

function SourcesTab() {
  const sources = useAdminSources();
  const update = useUpdateSource();
  const run = useRunSource();
  const say = useToast();
  const fail = useErrorToast();
  if (sources.isPending) return <Skeleton lines={6} />;
  if (sources.isError) return <LoadError error={sources.error} onRetry={() => void sources.refetch()} />;

  const setReviewed = (s: AdminSource, reviewed: boolean) =>
    update.mutate(
      {
        key: s.key,
        body: reviewed
          ? { ...noChange, termsReviewedAt: new Date().toISOString() }
          : { ...noChange, clearTermsReview: true },
      },
      { onSuccess: () => say(reviewed ? 'Terms marked as reviewed' : 'Terms review cleared'), onError: fail },
    );
  const setEnabled = (s: AdminSource, enabled: boolean) =>
    update.mutate(
      { key: s.key, body: { ...noChange, enabled } },
      { onSuccess: () => say(`${enabled ? 'Enabled' : 'Disabled'} ${s.displayName}`), onError: fail },
    );

  return (
    <>
      <p className="sub mb-3">
        A source cannot be enabled until its terms are marked as reviewed. Himalayas stays off until it confirms in
        writing that a personal job-matching service is allowed.
      </p>
      <div className="tablewrap">
        <table>
          <thead>
            <tr>
              <th>Source</th>
              <th>Kind</th>
              <th>Poll (min)</th>
              <th>Open listings</th>
              <th>Terms reviewed</th>
              <th>Enabled</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {sources.data
              .filter((s) => s.kind !== 'user')
              .map((s) => (
                <tr key={s.key}>
                  <td>
                    {s.displayName}
                    {!s.hasAdapter && <div className="help">not built yet</div>}
                    {s.termsUrl && (
                      <div className="help">
                        <a href={s.termsUrl} target="_blank" rel="noopener noreferrer">
                          terms
                        </a>
                      </div>
                    )}
                  </td>
                  <td>
                    <span className="chip">{s.kind}</span>
                  </td>
                  <td className="font-mono">{s.pollIntervalMinutes}</td>
                  <td className="font-mono">{nf.format(s.openJobs)}</td>
                  <td>
                    <input
                      className="cb"
                      type="checkbox"
                      aria-label={`Terms reviewed for ${s.displayName}`}
                      checked={s.termsReviewedAt !== null}
                      disabled={update.isPending}
                      onChange={(e) => setReviewed(s, e.target.checked)}
                    />
                    {s.termsReviewedAt && <div className="help">{when.format(new Date(s.termsReviewedAt))}</div>}
                  </td>
                  <td>
                    <Switch
                      label={`Enable ${s.displayName}`}
                      checked={s.enabled}
                      disabled={update.isPending}
                      onChange={(v) => setEnabled(s, v)}
                    />
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    <Button
                      size="sm"
                      disabled={!s.enabled || !s.hasAdapter || run.isPending}
                      onClick={() =>
                        run.mutate(s.key, { onSuccess: () => say(`Fetch of ${s.displayName} queued`), onError: fail })
                      }
                    >
                      Run now
                    </Button>
                  </td>
                </tr>
              ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function CompaniesTab() {
  const [query, setQuery] = useState<CompanyQuery>({ page: 1 });
  const companies = useAdminCompanies(query);
  const seed = useLoadSeed();
  const verify = useVerifyCompany();
  const setStatus = useSetCompanyStatus();
  const say = useToast();
  const fail = useErrorToast();
  const [adding, setAdding] = useState(false);

  return (
    <>
      <div className="row between mb-3">
        <div className="row">
          <input
            className="input"
            style={{ width: 220 }}
            placeholder="Search name or token"
            aria-label="Search companies"
            value={query.q ?? ''}
            onChange={(e) => setQuery({ ...query, q: e.target.value, page: 1 })}
          />
          <select
            className="select"
            style={{ width: 'auto' }}
            aria-label="Platform"
            value={query.platform ?? ''}
            onChange={(e) => setQuery({ ...query, platform: e.target.value, page: 1 })}
          >
            <option value="">All platforms</option>
            <option value="greenhouse">Greenhouse</option>
            <option value="lever">Lever</option>
            <option value="ashby">Ashby</option>
          </select>
          <select
            className="select"
            style={{ width: 'auto' }}
            aria-label="Status"
            value={query.status ?? ''}
            onChange={(e) => setQuery({ ...query, status: e.target.value, page: 1 })}
          >
            <option value="">All statuses</option>
            <option value="resolved">resolved</option>
            <option value="not_found">not_found</option>
            <option value="blocked">blocked</option>
          </select>
        </div>
        <div className="row">
          <Button
            disabled={seed.isPending}
            onClick={() =>
              seed.mutate(undefined, {
                onSuccess: (r) => say(`Seed list loaded: ${r.added} added, ${r.skipped} already known. Verifying…`),
                onError: fail,
              })
            }
          >
            Load seed list
          </Button>
          <Button variant="primary" onClick={() => setAdding((a) => !a)}>
            Add board
          </Button>
        </div>
      </div>
      {adding && <AddBoardForm onDone={() => setAdding(false)} />}
      {companies.isPending ? (
        <Skeleton lines={6} />
      ) : companies.isError ? (
        <LoadError error={companies.error} onRetry={() => void companies.refetch()} />
      ) : (
        <>
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>Company</th>
                  <th>Board</th>
                  <th>Platform</th>
                  <th>Status</th>
                  <th>Origin</th>
                  <th>Open jobs</th>
                  <th>Watchers</th>
                  <th>Checked</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {companies.data.items.map((c) => (
                  <tr key={c.id}>
                    <td>{c.name}</td>
                    <td className="font-mono">{c.boardToken}</td>
                    <td>{c.platform}</td>
                    <td>
                      <select
                        className="select"
                        style={{ width: 'auto', padding: '3px 6px' }}
                        aria-label={`Status of ${c.boardToken}`}
                        value={c.status}
                        onChange={(e) =>
                          setStatus.mutate(
                            { linkId: c.id, status: e.target.value },
                            { onSuccess: () => say(`Status set to ${e.target.value}`), onError: fail },
                          )
                        }
                      >
                        <option>resolved</option>
                        <option>not_found</option>
                        <option>blocked</option>
                      </select>
                    </td>
                    <td>
                      <span className="chip">{c.origin}</span>
                    </td>
                    <td className="font-mono">{c.openJobsCount ?? '—'}</td>
                    <td className="font-mono">{c.watchers}</td>
                    <td className="text-muted">
                      {ago(c.lastCheckedAt)}
                      {c.failureCount > 0 && <div className="help">{c.failureCount} failures</div>}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <Button
                        size="sm"
                        disabled={verify.isPending}
                        onClick={() =>
                          verify.mutate(c.id, {
                            onSuccess: (r) => say(`${r.boardToken}: ${r.status}, ${r.openJobsCount ?? 0} open jobs`),
                            onError: fail,
                          })
                        }
                      >
                        Verify
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="row between mt-3">
            <span className="help">
              {companies.data.items.length} shown · {nf.format(companies.data.total)} total
            </span>
            <div className="row">
              <Button size="sm" disabled={query.page <= 1} onClick={() => setQuery({ ...query, page: query.page - 1 })}>
                Previous
              </Button>
              <Button
                size="sm"
                disabled={query.page * companies.data.pageSize >= companies.data.total}
                onClick={() => setQuery({ ...query, page: query.page + 1 })}
              >
                Next
              </Button>
            </div>
          </div>
        </>
      )}
    </>
  );
}

function AddBoardForm({ onDone }: { onDone: () => void }) {
  const add = useAddCompany();
  const say = useToast();
  const [name, setName] = useState('');
  const [platform, setPlatform] = useState('greenhouse');
  const [token, setToken] = useState('');
  const [domain, setDomain] = useState('');
  const err = add.error && isApiError(add.error) ? add.error : null;
  const submit = (e: FormEvent) => {
    e.preventDefault();
    add.mutate(
      { name, platform, boardToken: token, websiteDomain: domain || null },
      {
        onSuccess: (c) => {
          say(`${c.name} added and verified (${c.openJobsCount ?? 0} open jobs)`);
          onDone();
        },
      },
    );
  };
  return (
    <form className="card mb-3" onSubmit={submit} noValidate>
      <h2>Add a board</h2>
      <div className="grid2 mt-3">
        <div className="field">
          <label htmlFor="ab-name">Company name</label>
          <input
            id="ab-name"
            className="input"
            value={name}
            maxLength={200}
            onChange={(e) => setName(e.target.value)}
          />
          {err?.field('name') && <div className="err">{err.field('name')}</div>}
        </div>
        <div className="field">
          <label htmlFor="ab-domain">Website domain (optional)</label>
          <input
            id="ab-domain"
            className="input"
            value={domain}
            placeholder="acme.com"
            onChange={(e) => setDomain(e.target.value)}
          />
          {err?.field('websiteDomain') && <div className="err">{err.field('websiteDomain')}</div>}
        </div>
        <div className="field">
          <label htmlFor="ab-platform">Platform</label>
          <select id="ab-platform" className="select" value={platform} onChange={(e) => setPlatform(e.target.value)}>
            <option value="greenhouse">Greenhouse</option>
            <option value="lever">Lever</option>
            <option value="ashby">Ashby</option>
          </select>
        </div>
        <div className="field">
          <label htmlFor="ab-token">Board token</label>
          <input
            id="ab-token"
            className="input font-mono"
            value={token}
            placeholder="acme"
            onChange={(e) => setToken(e.target.value.toLowerCase())}
          />
          {err?.field('boardToken') && <div className="err">{err.field('boardToken')}</div>}
        </div>
      </div>
      {err && !err.field('name') && !err.field('boardToken') && !err.field('websiteDomain') && (
        <Alert tone="bad" className="mb-3">
          {err.userMessage}
        </Alert>
      )}
      <div className="row">
        <Button type="submit" variant="primary" disabled={add.isPending || !name || !token}>
          {add.isPending ? 'Checking board…' : 'Add and verify'}
        </Button>
        <Button type="button" onClick={onDone}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

function CoverageTab() {
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [roles, setRoles] = useState('');
  const [applied, setApplied] = useState({ from: '', to: '', roles: '' });
  const coverage = useCoverage(applied.from, applied.to, applied.roles);

  return (
    <>
      <p className="sub mb-3">
        India-eligible jobs per day by source and by role keyword (unique listings, by the day they were first seen,
        India time). Use this to decide whether coverage is good enough before M3.
      </p>
      <form
        className="row mb-3"
        onSubmit={(e) => {
          e.preventDefault();
          setApplied({ from, to, roles });
        }}
      >
        <label className="row" style={{ gap: 6 }}>
          <span className="help">From</span>
          <input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className="row" style={{ gap: 6 }}>
          <span className="help">To</span>
          <input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <input
          className="input"
          style={{ width: 280 }}
          aria-label="Roles"
          placeholder="backend, frontend, data, devops, mobile"
          value={roles}
          onChange={(e) => setRoles(e.target.value)}
        />
        <Button type="submit">Update</Button>
      </form>
      {coverage.isPending ? (
        <Skeleton lines={6} />
      ) : coverage.isError ? (
        <LoadError error={coverage.error} onRetry={() => void coverage.refetch()} />
      ) : (
        <CoverageTable report={coverage.data} />
      )}
    </>
  );
}

function CoverageTable({ report }: { report: NonNullable<ReturnType<typeof useCoverage>['data']> }) {
  const sources = report.sources.map((s) => s.sourceKey);
  const open = (s: { worldwide: number; indiaEligible: number }) => s.worldwide + s.indiaEligible;
  return (
    <>
      <div className="grid3 mb-[14px]">
        <div className="card">
          <div className="help">India-eligible per day (average)</div>
          <div className="font-mono text-[24px] font-semibold">{report.openToIndiaPerDay}</div>
        </div>
        <div className="card">
          <div className="help">India-eligible in range</div>
          <div className="font-mono text-[24px] font-semibold">
            {nf.format(report.openToIndia)} <span className="text-sm text-muted">/ {nf.format(report.total)}</span>
          </div>
        </div>
        <div className="card">
          <div className="help">Range</div>
          <div className="font-mono text-[16px] font-semibold">
            {report.from} → {report.to}
          </div>
        </div>
      </div>
      <div className="tablewrap">
        <table>
          <thead>
            <tr>
              <th>Date</th>
              {sources.map((s) => (
                <th key={s}>{s}</th>
              ))}
              {report.roles.map((r) => (
                <th key={r} className="capitalize">
                  {r}
                </th>
              ))}
              <th>All (eligible / total)</th>
            </tr>
          </thead>
          <tbody>
            {[...report.days].reverse().map((d) => (
              <tr key={d.date}>
                <td className="font-mono">{d.date}</td>
                {sources.map((s) => {
                  const row = d.sources.find((x) => x.sourceKey === s);
                  return (
                    <td key={s} className="font-mono">
                      {row ? open(row) : 0}
                    </td>
                  );
                })}
                {report.roles.map((r) => (
                  <td key={r} className="font-mono">
                    {d.roles[r] ?? 0}
                  </td>
                ))}
                <td className="font-mono">
                  {d.openToIndia} / {d.total}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <h3 className="label-k mt-[18px]">Eligibility by source (whole range)</h3>
      <div className="tablewrap">
        <table>
          <thead>
            <tr>
              <th>Source</th>
              <th>Worldwide</th>
              <th>India eligible</th>
              <th>Restricted</th>
              <th>Unclear</th>
              <th>Total</th>
            </tr>
          </thead>
          <tbody>
            {report.sources.map((s) => (
              <tr key={s.sourceKey}>
                <td>{s.sourceKey}</td>
                <td className="font-mono">{s.worldwide}</td>
                <td className="font-mono">{s.indiaEligible}</td>
                <td className="font-mono">{s.restricted}</td>
                <td className="font-mono">{s.unclear}</td>
                <td className="font-mono">{s.total}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="help mt-[10px]">
        Eligibility here comes from the sources’ structured fields and phrase rules (no AI yet; enrichment arrives in
        M3), so “unclear” is large for feeds without location data.
      </p>
    </>
  );
}
