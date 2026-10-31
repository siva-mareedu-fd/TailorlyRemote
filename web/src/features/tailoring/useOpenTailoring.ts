import { useNavigate } from 'react-router';
import { useTrackJob } from '@/api/tailoring';
import { useToast } from '@/components/Toast';
import { errorText } from './messages';

/**
 * Opens the tailoring workspace for a job on the Shortlist (prototype `slAct('tailor' | 'cover')`): tracks the job first
 * when it is not tracked yet (idempotent on the server), then navigates.
 */
export function useOpenTailoring() {
  const navigate = useNavigate();
  const say = useToast();
  const track = useTrackJob();
  const open = (job: { id: string; tracked?: { userJobId: string } | null }, tab: 'resume' | 'cover' = 'resume') => {
    const go = (userJobId: string) => navigate(`/tailor/${userJobId}${tab === 'cover' ? '?tab=cover' : ''}`);
    if (job.tracked) return go(job.tracked.userJobId);
    track.mutate(job.id, { onSuccess: (uj) => go(uj.id), onError: (e) => say(errorText(e)) });
  };
  return { open, pending: track.isPending };
}
