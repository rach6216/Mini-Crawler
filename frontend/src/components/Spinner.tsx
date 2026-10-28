import { Loader2 } from 'lucide-react';

export function Spinner({ size = 16, label }: { size?: number; label?: string }) {
  return (
    <span className="spinner" role={label ? 'status' : undefined}>
      <Loader2 size={size} className="spin" aria-hidden />
      {label && <span className="spinner__label">{label}</span>}
    </span>
  );
}
