import { useState, type FormEvent } from 'react';
import { isApiError } from '@/api/client';
import {
  useAdminUsage,
  useAdminUsers,
  useAiConfig,
  useCreateInviteCode,
  useDeleteInviteCode,
  useFlags,
  useInviteCodes,
  useSetSuspended,
  useUpdateAiConfig,
  useUpdateFlag,
  type AiTaskConfig,
  type UserQuery,
} from '@/api/admin';
import { Switch } from '@/components/Controls';
import { Alert, LoadError, Skeleton } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';

const nf = new Intl.NumberFormat('en-IN');
const day = new Intl.DateTimeFormat('en-IN', { dateStyle: 'medium' });
const when = new Intl.DateTimeFormat('en-IN', { dateStyle: 'medium', timeStyle: 'short' });

/** Model ids offered in the picker; any other id can be typed (the API validates the shape, never the name). */
const KNOWN_MODELS = ['claude-haiku-5-5', 'claude-sonnet-5-5', 'claude-opus-5-5'];

const FLAG_LABELS: Record<string, [string, string]> = {
  'signup.require_invite_code': ['Require invite code at signup', 'Flag: signup.require_invite_code'],
  'digest.email_enabled': ['Send digest emails', 'Flag: digest.email_enabled'],
  'pdf_export.enabled': ['PDF export', 'Flag: pdf_export.enabled (needs the converter container)'],
};

function useErrorToast() {
  const say = useToast();
  return (e: unknown) => say(isApiError(e) ? `${e.code}: ${e.userMessage}` : 'Something went wrong');
}

// ---------- AI config (design: #admin "AI config") ----------

export function AiConfigTab() {
  const config = useAiConfig();
  if (config.isPending) return <Skeleton lines={6} />;
  if (config.isError) return <LoadError error={config.error} onRetry={() => void config.refetch()} />;
  return (
    <>
      <p className="sub mb-3">Changes apply within 60 seconds. Model names are stored here only.</p>
      <div className="tablewrap">
        <table>
          <thead>
            <tr>
              <th>Task</th>
              <th>Model</th>
              <th>Max tokens</th>
              <th>Budget</th>
              <th>Enabled</th>
              <th>Changed</th>
            </tr>
          </thead>
          <tbody>
            {config.data.map((t) => (
              <AiConfigRow key={t.task} task={t} />
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function AiConfigRow({ task }: { task: AiTaskConfig }) {
  const update = useUpdateAiConfig();
  const say = useToast();
  const fail = useErrorToast();
  const [maxTokens, setMaxTokens] = useState(String(task.maxTokens));
  const models = KNOWN_MODELS.includes(task.model) ? KNOWN_MODELS : [task.model, ...KNOWN_MODELS];
  const save = (body: Parameters<typeof update.mutate>[0]['body'], message: string) =>
    update.mutate({ task: task.task, body }, { onSuccess: () => say(message), onError: fail });

  return (
    <tr>
      <td className="font-mono">{task.task}</td>
      <td>
        <select
          className="select"
          style={{ width: 'auto', padding: '3px 6px' }}
          aria-label={`Model for ${task.task}`}
          value={task.model}
          disabled={update.isPending}
          onChange={(e) =>
            save(
              { model: e.target.value },
              `Model for ${task.task} set to ${e.target.value}. Applies within 60 seconds.`,
            )
          }
        >
          {models.map((m) => (
            <option key={m}>{m}</option>
          ))}
        </select>
      </td>
      <td>
        <input
          className="input font-mono"
          style={{ width: 90, padding: '3px 6px' }}
          inputMode="numeric"
          aria-label={`Max tokens for ${task.task}`}
          value={maxTokens}
          onChange={(e) => setMaxTokens(e.target.value.replace(/\D/g, ''))}
          onBlur={() => {
            const n = Number(maxTokens);
            if (n && n !== task.maxTokens) save({ maxTokens: n }, `Max tokens for ${task.task} set to ${nf.format(n)}`);
            else setMaxTokens(String(task.maxTokens));
          }}
        />
      </td>
      <td>
        <span className="chip">{task.bucket}</span>
      </td>
      <td>
        <Switch
          label={`Enable ${task.task}`}
          checked={task.enabled}
          disabled={update.isPending}
          onChange={(v) => save({ enabled: v }, `${task.task} ${v ? 'enabled' : 'disabled'}`)}
        />
      </td>
      <td className="help">{when.format(new Date(task.updatedAt))}</td>
    </tr>
  );
}

// ---------- Flags and invite codes (design: #admin "Flags") ----------

export function FlagsTab() {
  const flags = useFlags();
  const update = useUpdateFlag();
  const say = useToast();
  const fail = useErrorToast();
  if (flags.isPending) return <Skeleton lines={4} />;
  if (flags.isError) return <LoadError error={flags.error} onRetry={() => void flags.refetch()} />;
  return (
    <>
      <div className="card">
        {flags.data.map((f) => {
          const [title, desc] = FLAG_LABELS[f.key] ?? [f.key, `Flag: ${f.key}`];
          const isBool = typeof f.value === 'boolean';
          return (
            <div key={f.key} className="setting">
              <div>
                <div className="t">{title}</div>
                <div className="d">{desc}</div>
              </div>
              {isBool ? (
                <Switch
                  label={title}
                  checked={f.value === true}
                  disabled={update.isPending}
                  onChange={(v) =>
                    update.mutate(
                      { key: f.key, value: v },
                      { onSuccess: () => say(`${title}: ${v ? 'on' : 'off'}`), onError: fail },
                    )
                  }
                />
              ) : (
                <span className="font-mono text-sm">{JSON.stringify(f.value)}</span>
              )}
            </div>
          );
        })}
      </div>
      <InviteCodes />
    </>
  );
}

function InviteCodes() {
  const codes = useInviteCodes();
  const create = useCreateInviteCode();
  const remove = useDeleteInviteCode();
  const say = useToast();
  const fail = useErrorToast();
  const [maxUses, setMaxUses] = useState('5');
  const [expires, setExpires] = useState('');
  const err = create.error && isApiError(create.error) ? create.error : null;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    create.mutate(
      {
        code: null,
        maxUses: Number(maxUses) || 1,
        // End of the chosen day, India time.
        expiresAt: expires ? new Date(`${expires}T23:59:59+05:30`).toISOString() : null,
      },
      { onSuccess: (c) => say(`Invite code ${c.code} created`) },
    );
  };

  return (
    <>
      <h3 className="k mt-[18px]">Invite codes</h3>
      <form className="row mb-3" onSubmit={submit} noValidate>
        <label className="row" style={{ gap: 6 }}>
          <span className="help">Uses</span>
          <input
            className="input font-mono"
            style={{ width: 70 }}
            inputMode="numeric"
            value={maxUses}
            onChange={(e) => setMaxUses(e.target.value.replace(/\D/g, ''))}
          />
        </label>
        <label className="row" style={{ gap: 6 }}>
          <span className="help">Expires (optional)</span>
          <input className="input" type="date" value={expires} onChange={(e) => setExpires(e.target.value)} />
        </label>
        <Button type="submit" variant="primary" disabled={create.isPending}>
          New code
        </Button>
      </form>
      {err && (
        <Alert tone="bad" className="mb-3">
          {err.field('maxUses') ?? err.field('expiresAt') ?? err.userMessage}
        </Alert>
      )}
      {codes.isPending ? (
        <Skeleton lines={3} />
      ) : codes.isError ? (
        <LoadError error={codes.error} onRetry={() => void codes.refetch()} />
      ) : codes.data.length === 0 ? (
        <div className="empty">No invite codes yet.</div>
      ) : (
        <div className="tablewrap">
          <table>
            <thead>
              <tr>
                <th>Code</th>
                <th>Uses</th>
                <th>Expires</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {codes.data.map((c) => {
                const expired = c.expiresAt && new Date(c.expiresAt) < new Date();
                return (
                  <tr key={c.code}>
                    <td className="font-mono select-all">{c.code}</td>
                    <td className="font-mono">
                      {c.usedCount} / {c.maxUses}
                    </td>
                    <td>
                      {c.expiresAt ? day.format(new Date(c.expiresAt)) : <span className="text-muted">never</span>}
                      {expired && <span className="chip warn ml-2">expired</span>}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <Button
                        size="sm"
                        variant="danger"
                        disabled={remove.isPending}
                        onClick={() =>
                          remove.mutate(c.code, {
                            onSuccess: () => say(`Invite code ${c.code} deleted`),
                            onError: fail,
                          })
                        }
                      >
                        Delete
                      </Button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

// ---------- Users (not in the prototype: ids and counts only) ----------

export function UsersTab({ selfId }: { selfId: string }) {
  const [query, setQuery] = useState<UserQuery>({ page: 1 });
  const [term, setTerm] = useState('');
  const users = useAdminUsers(query);
  const suspend = useSetSuspended();
  const say = useToast();
  const fail = useErrorToast();

  return (
    <>
      <p className="sub mb-3">
        Ids and counts only. Find one account by its exact email or id; emails are matched, never shown.
      </p>
      <form
        className="row mb-3"
        onSubmit={(e) => {
          e.preventDefault();
          setQuery({ ...query, q: term.trim(), page: 1 });
        }}
      >
        <input
          className="input"
          style={{ width: 300 }}
          aria-label="Exact email or user id"
          placeholder="Exact email or user id"
          value={term}
          onChange={(e) => setTerm(e.target.value)}
        />
        <select
          className="select"
          style={{ width: 'auto' }}
          aria-label="Status"
          value={query.status ?? ''}
          onChange={(e) => setQuery({ ...query, status: e.target.value, page: 1 })}
        >
          <option value="">All statuses</option>
          <option value="active">active</option>
          <option value="suspended">suspended</option>
          <option value="pending_deletion">pending_deletion</option>
        </select>
        <Button type="submit">Find</Button>
        {query.q && (
          <Button
            type="button"
            variant="ghost"
            onClick={() => {
              setTerm('');
              setQuery({ ...query, q: '', page: 1 });
            }}
          >
            Clear
          </Button>
        )}
      </form>
      {users.isPending ? (
        <Skeleton lines={6} />
      ) : users.isError ? (
        <LoadError error={users.error} onRetry={() => void users.refetch()} />
      ) : users.data.items.length === 0 ? (
        <div className="empty">No account matches.</div>
      ) : (
        <>
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>User id</th>
                  <th>Status</th>
                  <th>Flags</th>
                  <th>Joined</th>
                  <th>Last sign-in</th>
                  <th>Résumés</th>
                  <th>Tracked</th>
                  <th style={{ textAlign: 'right' }}>Tokens ({users.data.period})</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {users.data.items.map((u) => (
                  <tr key={u.id}>
                    <td className="font-mono text-[12px] select-all">{u.id}</td>
                    <td>
                      <span
                        className={`chip ${u.status === 'active' ? 'ok' : u.status === 'suspended' ? 'bad' : 'warn'}`}
                      >
                        {u.status}
                      </span>
                    </td>
                    <td className="help">
                      {[u.isAdmin && 'admin', u.twoFactorEnabled && '2-step', !u.emailVerified && 'unverified']
                        .filter(Boolean)
                        .join(' · ') || '—'}
                    </td>
                    <td className="help">{day.format(new Date(u.createdAt))}</td>
                    <td className="help">{u.lastSignInAt ? when.format(new Date(u.lastSignInAt)) : '—'}</td>
                    <td className="font-mono">{u.resumeCount}</td>
                    <td className="font-mono">{u.trackedJobs}</td>
                    <td className="font-mono" style={{ textAlign: 'right' }}>
                      {nf.format(u.tokensUsedThisPeriod)}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      {u.id !== selfId && (u.status === 'active' || u.status === 'suspended') && (
                        <Button
                          size="sm"
                          variant={u.status === 'active' ? 'danger' : 'default'}
                          disabled={suspend.isPending}
                          onClick={() =>
                            suspend.mutate(
                              { id: u.id, suspended: u.status === 'active' },
                              {
                                onSuccess: (r) =>
                                  say(r.status === 'suspended' ? 'Account suspended' : 'Account restored'),
                                onError: fail,
                              },
                            )
                          }
                        >
                          {u.status === 'active' ? 'Suspend' : 'Unsuspend'}
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="row between mt-3">
            <span className="help">
              {users.data.items.length} shown · {nf.format(users.data.total)} total
            </span>
            <div className="row">
              <Button size="sm" disabled={query.page <= 1} onClick={() => setQuery({ ...query, page: query.page - 1 })}>
                Previous
              </Button>
              <Button
                size="sm"
                disabled={query.page * users.data.pageSize >= users.data.total}
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

// ---------- Usage (PRD F15 "per-user usage") ----------

export function UsageTab() {
  const [period, setPeriod] = useState('');
  const usage = useAdminUsage(period);
  return (
    <>
      <div className="row between mb-3">
        <p className="sub">AI calls and tokens per task and per user (ids only) for one budget month (India time).</p>
        <label className="row" style={{ gap: 6 }}>
          <span className="help">Month</span>
          <input className="input" type="month" value={period} onChange={(e) => setPeriod(e.target.value)} />
        </label>
      </div>
      {usage.isPending ? (
        <Skeleton lines={6} />
      ) : usage.isError ? (
        <LoadError error={usage.error} onRetry={() => void usage.refetch()} />
      ) : (
        <>
          <div className="grid3 mb-[14px]">
            <div className="card">
              <div className="help">Tokens ({usage.data.period})</div>
              <div className="font-mono text-[24px] font-semibold">
                {nf.format(usage.data.byTask.reduce((s, t) => s + t.countedTokens, 0))}
              </div>
            </div>
            <div className="card">
              <div className="help">Platform (enrichment)</div>
              <div className="font-mono text-[24px] font-semibold">{nf.format(usage.data.platformTokens)}</div>
            </div>
            <div className="card">
              <div className="help">Estimated cost</div>
              <div className="font-mono text-[24px] font-semibold">
                ${usage.data.byTask.reduce((s, t) => s + Number(t.estCostUsd), 0).toFixed(2)}
              </div>
            </div>
          </div>
          <h3 className="k">By task and model</h3>
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>Task</th>
                  <th>Model</th>
                  <th>Budget</th>
                  <th>Calls</th>
                  <th>Errors</th>
                  <th style={{ textAlign: 'right' }}>Tokens</th>
                </tr>
              </thead>
              <tbody>
                {usage.data.byTask.length === 0 && (
                  <tr>
                    <td colSpan={6} className="text-muted">
                      No AI calls in this month.
                    </td>
                  </tr>
                )}
                {usage.data.byTask.map((t) => (
                  <tr key={t.task + t.model + t.bucket}>
                    <td className="font-mono">{t.task}</td>
                    <td className="font-mono">{t.model}</td>
                    <td>
                      <span className="chip">{t.bucket}</span>
                    </td>
                    <td className="font-mono">{nf.format(t.calls)}</td>
                    <td className="font-mono">{nf.format(t.errors)}</td>
                    <td className="font-mono" style={{ textAlign: 'right' }}>
                      {nf.format(t.countedTokens)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <h3 className="k mt-[18px]">By user (heaviest first)</h3>
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>User id</th>
                  <th>Calls</th>
                  <th style={{ textAlign: 'right' }}>Discovery</th>
                  <th style={{ textAlign: 'right' }}>Documents</th>
                </tr>
              </thead>
              <tbody>
                {usage.data.byUser.map((u) => (
                  <tr key={u.userId}>
                    <td className="font-mono text-[12px] select-all">{u.userId}</td>
                    <td className="font-mono">{nf.format(u.calls)}</td>
                    <td className="font-mono" style={{ textAlign: 'right' }}>
                      {nf.format(u.discoveryTokens)}
                    </td>
                    <td className="font-mono" style={{ textAlign: 'right' }}>
                      {nf.format(u.documentsTokens)}
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
