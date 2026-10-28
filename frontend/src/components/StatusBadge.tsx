import { Ban, CheckCircle2, CircleDashed, Clock, Loader2, MinusCircle, XCircle, type LucideIcon } from 'lucide-react';
import type { FetchStatus, JobStatus } from '../api/types';

const JOB_STATUS: Record<JobStatus, { tone: Tone; icon: LucideIcon; spin?: boolean }> = {
  Pending: { tone: 'neutral', icon: Clock },
  Running: { tone: 'info', icon: Loader2, spin: true },
  Completed: { tone: 'success', icon: CheckCircle2 },
  Failed: { tone: 'danger', icon: XCircle },
  Canceled: { tone: 'warning', icon: Ban },
};

const FETCH_STATUS: Record<FetchStatus, { tone: Tone; icon: LucideIcon }> = {
  Fetched: { tone: 'success', icon: CheckCircle2 },
  Failed: { tone: 'danger', icon: XCircle },
  Skipped: { tone: 'neutral', icon: MinusCircle },
  Discovered: { tone: 'outline', icon: CircleDashed },
};

type Tone = 'neutral' | 'info' | 'success' | 'danger' | 'warning' | 'outline';

export function StatusBadge({ status }: { status: JobStatus }) {
  const { tone, icon: Icon, spin } = JOB_STATUS[status];
  return (
    <span className={`badge badge--${tone}`}>
      <Icon size={14} aria-hidden className={spin ? 'spin' : undefined} />
      {status}
    </span>
  );
}

export function FetchStatusBadge({
  status,
  httpStatusCode,
  title,
}: {
  status: FetchStatus;
  httpStatusCode?: number | null;
  title?: string;
}) {
  const { tone, icon: Icon } = FETCH_STATUS[status];
  return (
    <span className={`badge badge--sm badge--${tone}`} title={title}>
      <Icon size={12} aria-hidden />
      {status}
      {httpStatusCode != null && <span className="badge__code">{httpStatusCode}</span>}
    </span>
  );
}
