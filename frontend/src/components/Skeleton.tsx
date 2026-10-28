export function Skeleton({ width, height = '0.875rem' }: { width?: string; height?: string }) {
  return <span className="skeleton" style={{ width, height }} aria-hidden />;
}
