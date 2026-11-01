import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { api, call } from '@/api/client';
import { tailorKeys } from '@/api/tailoring';
import { trackerKeys } from '@/api/tracker';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { errorText } from '@/features/tailoring/messages';

/** What the user applied to: a tracker entry, or a Shortlist job that is tracked first. */
export interface ApplyTarget {
  title: string;
  companyName: string;
  userJobId?: string | null;
  jobId?: string | null;
}

/**
 * PRD F11 "Apply": the link opens the source's apply URL in a new tab (the caller's `<a target="_blank"
 * rel="noopener noreferrer">`); when the user comes back to this tab we ask "Did you apply?" and, on Yes, move the entry
 * to Applied (tracking the job first if needed). A plain confirmation: nothing is tracked on the other site.
 */
export function useApplyPrompt(): { start: (t: ApplyTarget) => void; dialog: ReactNode } {
  const qc = useQueryClient();
  const say = useToast();
  const [pending, setPending] = useState<ApplyTarget | null>(null);
  const [asking, setAsking] = useState<ApplyTarget | null>(null);

  useEffect(() => {
    if (!pending) return;
    const back = () => {
      if (document.visibilityState === 'hidden') return;
      setAsking(pending);
      setPending(null);
    };
    window.addEventListener('focus', back);
    document.addEventListener('visibilitychange', back);
    return () => {
      window.removeEventListener('focus', back);
      document.removeEventListener('visibilitychange', back);
    };
  }, [pending]);

  const markApplied = useMutation({
    mutationFn: async (t: ApplyTarget) => {
      const id = t.userJobId ?? (await call(() => api.POST('/api/v1/user-jobs', { body: { jobId: t.jobId! } }))).id;
      const entry = await call(() => api.GET('/api/v1/user-jobs/{id}', { params: { path: { id } } }));
      if (entry.status === 'applied') return entry;
      return call(() =>
        api.PATCH('/api/v1/user-jobs/{id}', {
          params: { path: { id } },
          body: { status: 'applied', notes: null, concurrencyToken: entry.concurrencyToken },
        }),
      );
    },
    onSuccess: (uj) => {
      qc.setQueryData(tailorKeys.userJob(uj.id), uj);
      void qc.invalidateQueries({ queryKey: trackerKeys.list });
      void qc.invalidateQueries({ queryKey: ['jobs'] });
      say('Moved to Applied');
    },
    onError: (e) => say(errorText(e)),
  });

  const dialog = (
    <Dialog open={!!asking} onOpenChange={(open) => !open && setAsking(null)}>
      {asking && (
        <DialogContent>
          <DialogTitle>Did you apply?</DialogTitle>
          <DialogDescription>
            {asking.title} at {asking.companyName}. We will move it to Applied on your tracker.
          </DialogDescription>
          <div className="row mt-4" style={{ justifyContent: 'flex-end' }}>
            <Button onClick={() => setAsking(null)}>Not yet</Button>
            <Button
              variant="primary"
              disabled={markApplied.isPending}
              onClick={() => markApplied.mutate(asking, { onSettled: () => setAsking(null) })}
            >
              Yes, I applied
            </Button>
          </div>
        </DialogContent>
      )}
    </Dialog>
  );

  return { start: setPending, dialog };
}
