import { OctagonAlert, RotateCw } from 'lucide-react';
import { describeError } from '../api/errors';
import { Spinner } from './Spinner';

export function ErrorState({
  error,
  title,
  onRetry,
  retrying,
}: {
  error: unknown;
  title?: string;
  onRetry?: () => void;
  retrying?: boolean;
}) {
  const described = describeError(error);
  return (
    <div className="error-state" role="alert">
      <OctagonAlert size={22} aria-hidden className="error-state__icon" />
      <p className="error-state__title">{title ?? described.title}</p>
      {described.detail && <p className="error-state__detail">{described.detail}</p>}
      {onRetry && (
        <button type="button" className="btn btn--secondary" onClick={onRetry} disabled={retrying}>
          {retrying ? <Spinner size={14} /> : <RotateCw size={14} aria-hidden />}
          Try again
        </button>
      )}
    </div>
  );
}
