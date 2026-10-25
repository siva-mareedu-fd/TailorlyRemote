import { useState, type FormEvent } from 'react';
import { isApiError } from '@/api/client';
import { useAddWatchedCompany, useMeta, useRemoveWatchedCompany, useWatchedCompanies } from '@/api/queries';
import { LoadError, Skeleton } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';

const platformName: Record<string, string> = { greenhouse: 'Greenhouse', lever: 'Lever', ashby: 'Ashby' };

/**
 * "Companies to watch" (design: #preferences; ApiDesign §4.5). Changes save immediately: the server checks the board
 * through its fixed public API, never the link the user typed.
 */
export function WatchedCompaniesCard() {
  const meta = useMeta();
  const list = useWatchedCompanies();
  const add = useAddWatchedCompany();
  const remove = useRemoveWatchedCompany();
  const say = useToast();
  const [url, setUrl] = useState('');
  const max = meta.data?.limits.watchlistMax ?? 25;
  const count = list.data?.length ?? 0;
  const error = add.error && isApiError(add.error) ? add.error : null;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!url.trim()) return;
    add.mutate(url.trim(), {
      onSuccess: (c) => {
        setUrl('');
        say(`${c.name} added. We check its board every few hours.`);
      },
    });
  };

  return (
    <div className="card">
      <h2>Companies to watch</h2>
      <p className="sub">Add a company career board (Greenhouse, Lever or Ashby). We read its public job list.</p>
      {list.isPending ? (
        <Skeleton lines={2} />
      ) : list.isError ? (
        <LoadError error={list.error} onRetry={() => void list.refetch()} />
      ) : (
        <div className="profchips mb-3">
          {count === 0 && <span className="help">No companies yet.</span>}
          {list.data.map((c) => (
            <span
              key={c.id}
              className="tagx"
              title={c.status === 'resolved' ? undefined : 'This board could not be found'}
            >
              {c.name} ({platformName[c.platform] ?? c.platform}){c.status !== 'resolved' && ' · not found'}
              <button
                type="button"
                aria-label={`Stop watching ${c.name}`}
                disabled={remove.isPending}
                onClick={() => remove.mutate(c.id, { onSuccess: () => say(`Stopped watching ${c.name}`) })}
              >
                ×
              </button>
            </span>
          ))}
        </div>
      )}
      <form className="row" style={{ flexWrap: 'nowrap' }} onSubmit={submit} noValidate>
        <input
          className="input"
          value={url}
          onChange={(e) => {
            setUrl(e.target.value);
            add.reset();
          }}
          placeholder="https://boards.greenhouse.io/company"
          aria-label="Board URL"
          aria-invalid={error ? true : undefined}
          aria-describedby="board-help"
          disabled={count >= max}
        />
        <Button type="submit" disabled={add.isPending || !url.trim() || count >= max}>
          {add.isPending ? 'Checking…' : 'Add'}
        </Button>
      </form>
      {error && (
        <div className="err" role="alert">
          {error.userMessage}
        </div>
      )}
      <div id="board-help" className="help">
        {count} of {max} used. We only open the board’s public job list, never the link you type.
      </div>
    </div>
  );
}
