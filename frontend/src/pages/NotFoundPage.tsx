import { Compass } from 'lucide-react';
import { Link } from 'react-router-dom';
import { EmptyState } from '../components/EmptyState';

export function NotFoundPage() {
  return (
    <div className="page">
      <EmptyState
        icon={<Compass size={28} aria-hidden />}
        title="Page not found"
        description="The address does not match any screen in this app."
        action={
          <Link to="/" className="btn btn--primary">
            Start a crawl
          </Link>
        }
      />
    </div>
  );
}
