import { Check, Copy } from 'lucide-react';
import { useEffect, useState } from 'react';

const RESET_AFTER_MS = 1_500;

export function CopyButton({ value, label }: { value: string; label: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle');

  useEffect(() => {
    if (state === 'idle') return;
    const timer = setTimeout(() => setState('idle'), RESET_AFTER_MS);
    return () => clearTimeout(timer);
  }, [state]);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setState('copied');
    } catch {
      setState('failed');
    }
  };

  const text = state === 'copied' ? 'Copied' : state === 'failed' ? 'Copy failed' : label;
  return (
    <button type="button" className="btn btn--ghost btn--icon" onClick={copy} aria-label={text} title={text}>
      {state === 'copied' ? <Check size={14} aria-hidden /> : <Copy size={14} aria-hidden />}
      <span className="sr-only" aria-live="polite">
        {state === 'idle' ? '' : text}
      </span>
    </button>
  );
}
