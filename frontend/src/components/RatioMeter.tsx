import { formatRatio } from '../lib/format';

/** Small inline meter for the Domain Link Ratio (internal links / all links). */
export function RatioMeter({ ratio }: { ratio: number }) {
  const text = formatRatio(ratio);
  return (
    <span className="ratio" title={`Domain link ratio ${text}`}>
      <span className="ratio__track" aria-hidden>
        <span className="ratio__fill" style={{ width: text }} />
      </span>
      <span className="ratio__text">{text}</span>
    </span>
  );
}
