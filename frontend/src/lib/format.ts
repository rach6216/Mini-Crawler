const dateTimeFormat = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
});

export const EMPTY_VALUE = '—';

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return EMPTY_VALUE;
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? EMPTY_VALUE : dateTimeFormat.format(date);
}

export function formatRelative(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return EMPTY_VALUE;
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return EMPTY_VALUE;
  const seconds = Math.round((now - then) / 1000);
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${seconds} s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  return `${Math.floor(hours / 24)} d ago`;
}

/** Ratio in [0, 1] as a percentage with one decimal, e.g. 0.75 -> "75.0%". */
export function formatRatio(ratio: number): string {
  const safe = Number.isFinite(ratio) ? Math.min(1, Math.max(0, ratio)) : 0;
  return `${(safe * 100).toFixed(1)}%`;
}

/** Elapsed time between two timestamps; while `completed` is null it measures up to `now`. */
export function formatDuration(
  started: string | null | undefined,
  completed: string | null | undefined,
  now: number = Date.now(),
): string {
  if (!started) return EMPTY_VALUE;
  const start = new Date(started).getTime();
  const end = completed ? new Date(completed).getTime() : now;
  if (Number.isNaN(start) || Number.isNaN(end)) return EMPTY_VALUE;
  const ms = Math.max(0, end - start);
  if (ms < 1000) return '< 1 s';
  const totalSeconds = Math.floor(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds} s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes} m ${seconds.toString().padStart(2, '0')} s`;
  const hours = Math.floor(minutes / 60);
  return `${hours} h ${(minutes % 60).toString().padStart(2, '0')} m`;
}

export function formatCount(value: number): string {
  return value.toLocaleString();
}

/** Splits a URL into host and path+query for display; falls back to the raw string. */
export function splitUrl(url: string): { host: string; path: string } {
  try {
    const parsed = new URL(url);
    return { host: parsed.host, path: `${parsed.pathname}${parsed.search}` || '/' };
  } catch {
    return { host: '', path: url };
  }
}
