import type { ReactNode } from 'react';

export interface KeyValueItem {
  label: string;
  value: ReactNode;
  title?: string;
}

export function KeyValueList({ items }: { items: KeyValueItem[] }) {
  return (
    <dl className="kv">
      {items.map((item) => (
        <div className="kv__row" key={item.label}>
          <dt className="kv__label">{item.label}</dt>
          <dd className="kv__value" title={item.title}>
            {item.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}
