import { useState } from 'react';
import { ApiError, isApiError } from '@/api/client';
import { usePreferences, useSavePreferences, type Preferences } from '@/api/queries';
import { useSignedInMe } from '@/app/signedIn';
import { ChipsInput, MultiSegmented, Segmented, Setting, Switch } from '@/components/Controls';
import { Alert, LoadError, PageLoading } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import {
  currencies,
  formatAnnual,
  hiringModels,
  jobTypes,
  overlapOptions,
  parseAmount,
  scopes,
  workModes,
  type Currency,
  type HiringModel,
  type JobType,
  type Scope,
  type WorkMode,
} from '@/features/preferences/options';
import { WatchedCompaniesCard } from '@/features/preferences/WatchedCompanies';

/** Preferences page (design: #preferences; PRD F3). Filters for the shortlist and the daily digest. */
export function PreferencesPage() {
  const prefs = usePreferences();
  const save = useSavePreferences();
  const say = useToast();
  const me = useSignedInMe();
  const [draft, setDraft] = useState<Preferences | null>(null);
  const [syncedToken, setSyncedToken] = useState<string | null>(null);

  // Edit a copy of the server's preferences; a newer server copy (after a save or a reload) replaces the draft.
  if (prefs.data && prefs.data.concurrencyToken !== syncedToken) {
    setSyncedToken(prefs.data.concurrencyToken);
    setDraft(prefs.data);
  }

  if (prefs.isPending || (prefs.data && !draft)) return <PageLoading />;
  if (prefs.isError)
    return (
      <div className="page narrow">
        <LoadError error={prefs.error} onRetry={() => void prefs.refetch()} />
      </div>
    );

  const p = draft!;
  const dirty = JSON.stringify(p) !== JSON.stringify(prefs.data);
  const set = (patch: Partial<Preferences>) => {
    save.reset();
    setDraft({ ...p, ...patch });
  };
  const err = save.error instanceof ApiError ? save.error : null;
  const conflict = isApiError(save.error, 'CONCURRENT_UPDATE');
  const field = (name: string) => err?.field(name);
  const atLeastOne = () => say('Keep at least one option selected');

  return (
    <div className="page narrow" style={{ paddingBottom: 0 }}>
      <div className="page-title">
        <div>
          <h1>Preferences</h1>
          <div className="sub">These filters apply to your shortlist and your daily digest.</div>
        </div>
      </div>

      <div className="stack">
        <div className="card">
          <h2>Where to look</h2>
          <p className="sub">Which jobs can appear.</p>
          <div className="field">
            <span className="lbl">Search scope</span>
            <Segmented<Scope>
              label="Search scope"
              value={p.searchScope as Scope}
              options={scopes}
              onChange={(v) => set({ searchScope: v })}
            />
            <span className="help">
              “India only” covers company career boards with India locations and jobs you add yourself.
            </span>
          </div>
          <ChipsInput
            label="Target roles"
            values={p.targetRoles}
            onChange={(v) => set({ targetRoles: v })}
            error={field('targetRoles')}
          />
          <ChipsInput
            label="Cities in India"
            values={p.locations}
            onChange={(v) => set({ locations: v })}
            error={field('locations')}
          />
          <div className="grid2">
            <div className="field">
              <span className="lbl">Work mode</span>
              <MultiSegmented<WorkMode>
                label="Work mode"
                values={p.workModes as WorkMode[]}
                options={workModes}
                onChange={(v) => set({ workModes: v })}
                onEmpty={atLeastOne}
              />
            </div>
            <div className="field">
              <span className="lbl">Job type</span>
              <MultiSegmented<JobType>
                label="Job type"
                values={p.jobTypes as JobType[]}
                options={jobTypes}
                onChange={(v) => set({ jobTypes: v })}
                onEmpty={atLeastOne}
              />
            </div>
          </div>
          <div className="field">
            <span className="lbl">Hiring model</span>
            <MultiSegmented<HiringModel>
              label="Hiring model"
              values={p.hiringModels as HiringModel[]}
              options={hiringModels}
              onChange={(v) => set({ hiringModels: v })}
              onEmpty={atLeastOne}
            />
          </div>
        </div>

        <div className="card">
          <h2>Pay</h2>
          <p className="sub">Annual amounts. Jobs in other currencies are shown with an approximate conversion.</p>
          <div className="field">
            <span className="lbl">Currency</span>
            <Segmented<Currency>
              label="Currency"
              value={p.salaryCurrency as Currency}
              options={currencies}
              onChange={(v) => set({ salaryCurrency: v })}
            />
          </div>
          <div className="grid2">
            <AmountField
              id="smin"
              label="Minimum"
              value={p.salaryMin}
              currency={p.salaryCurrency}
              error={field('salaryMin')}
              onChange={(v) => set({ salaryMin: v })}
            />
            <AmountField
              id="smax"
              label="Maximum"
              value={p.salaryMax}
              currency={p.salaryCurrency}
              error={field('salaryMax')}
              onChange={(v) => set({ salaryMax: v })}
            />
          </div>
          <Setting
            title="Show jobs that do not list a salary"
            description="Only when the match score is at or above your minimum."
          >
            <Switch
              label="Show jobs that do not list a salary"
              checked={p.includeUnspecifiedSalary}
              onChange={(v) => set({ includeUnspecifiedSalary: v })}
            />
          </Setting>
        </div>

        <div className="card">
          <h2>Match quality</h2>
          <p className="sub">Jobs below this score are hidden.</p>
          <div className="field">
            <div className="row between">
              <label htmlFor="minScore">Minimum match score</label>
              <span className="font-mono">{p.minMatchScore} / 10</span>
            </div>
            <input
              type="range"
              id="minScore"
              min={1}
              max={10}
              value={p.minMatchScore}
              onChange={(e) => set({ minMatchScore: Number(e.target.value) })}
            />
          </div>
          <Setting
            title="Show jobs with unclear eligibility in the email digest"
            description="They always appear in the app with a warning."
          >
            <Switch
              label="Show jobs with unclear eligibility in the email digest"
              checked={p.showUnclearEligibility}
              onChange={(v) => set({ showUnclearEligibility: v })}
            />
          </Setting>
          <div className="field mt-3">
            <label htmlFor="overlap">Time-zone overlap</label>
            <select
              className="select"
              id="overlap"
              value={p.timezoneOverlap ?? 'Any'}
              onChange={(e) => set({ timezoneOverlap: e.target.value === 'Any' ? null : e.target.value })}
            >
              {overlapOptions.map((o) => (
                <option key={o}>{o}</option>
              ))}
            </select>
          </div>
          <ChipsInput
            label="Exclude companies"
            values={p.excludedCompanies}
            onChange={(v) => set({ excludedCompanies: v })}
            error={field('excludedCompanies')}
          />
          <ChipsInput
            label="Exclude keywords"
            values={p.excludedKeywords}
            onChange={(v) => set({ excludedKeywords: v })}
            error={field('excludedKeywords')}
          />
        </div>

        <WatchedCompaniesCard />

        <div className="card">
          <h2>Daily digest</h2>
          <p className="sub">When your shortlist is prepared and emailed.</p>
          <Setting
            title="Send a daily digest"
            description="Turn off to stop emails. You can still run the search yourself."
          >
            <Switch
              label="Send a daily digest"
              checked={p.scheduleEnabled}
              onChange={(v) => set({ scheduleEnabled: v })}
            />
          </Setting>
          <div className="grid2 mt-3">
            <div className="field">
              <label htmlFor="time">Time</label>
              <input
                className="input font-mono"
                id="time"
                type="time"
                value={p.scheduleLocalTime}
                aria-invalid={field('scheduleLocalTime') ? true : undefined}
                onChange={(e) => set({ scheduleLocalTime: e.target.value })}
              />
              <span className="help">Your time zone: {me.timeZone}</span>
              {field('scheduleLocalTime') && <div className="err">{field('scheduleLocalTime')}</div>}
            </div>
          </div>
          <Setting title="Send a message when nothing new matches">
            <Switch
              label="Send a message when nothing new matches"
              checked={p.digestWhenEmpty}
              onChange={(v) => set({ digestWhenEmpty: v })}
            />
          </Setting>
        </div>
      </div>

      <div className="savebar">
        {conflict ? (
          <Alert
            tone="warn"
            className="flex-1"
            action={
              <Button
                size="sm"
                onClick={() => {
                  save.reset();
                  void prefs.refetch(); // the newer token replaces the draft (see above)
                }}
              >
                Reload
              </Button>
            }
          >
            Your preferences were changed in another tab. Reload to see them; your edits here will be lost.
          </Alert>
        ) : (
          <span className="help" role="status">
            {err ? err.userMessage : dirty ? 'You have unsaved changes.' : 'All changes saved.'}
          </span>
        )}
        <div className="row">
          <Button type="button" disabled={!dirty || save.isPending} onClick={() => setDraft(prefs.data!)}>
            Discard
          </Button>
          <Button
            type="button"
            variant="primary"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate(p, { onSuccess: () => say('Preferences saved') })}
          >
            {save.isPending ? 'Saving…' : 'Save preferences'}
          </Button>
        </div>
      </div>
    </div>
  );
}

function AmountField({
  id,
  label,
  value,
  currency,
  error,
  onChange,
}: {
  id: string;
  label: string;
  value: number | null;
  currency: string;
  error?: string;
  onChange: (v: number | null) => void;
}) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input
        className="input font-mono"
        id={id}
        inputMode="numeric"
        value={value ?? ''}
        aria-invalid={error ? true : undefined}
        aria-describedby={`${id}-hint`}
        onChange={(e) => onChange(parseAmount(e.target.value))}
      />
      <span className="help" id={`${id}-hint`}>
        {formatAnnual(value, currency)}
      </span>
      {error && (
        <div className="err" role="alert">
          {error}
        </div>
      )}
    </div>
  );
}
