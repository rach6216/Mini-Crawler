import { AlertTriangle, Info, OctagonAlert } from 'lucide-react';
import type { ReactNode } from 'react';

type AlertTone = 'warning' | 'danger' | 'info';

const ICONS = { warning: AlertTriangle, danger: OctagonAlert, info: Info } as const;

export function Alert({ tone, title, children }: { tone: AlertTone; title: string; children?: ReactNode }) {
  const Icon = ICONS[tone];
  return (
    <div className={`alert alert--${tone}`} role={tone === 'danger' ? 'alert' : 'status'}>
      <Icon size={18} aria-hidden className="alert__icon" />
      <div>
        <p className="alert__title">{title}</p>
        {children && <div className="alert__body">{children}</div>}
      </div>
    </div>
  );
}
