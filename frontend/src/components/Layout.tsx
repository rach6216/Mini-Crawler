import { History, Network, Plus } from 'lucide-react';
import { NavLink, Outlet } from 'react-router-dom';
import { usingMocks } from '../api';

export function Layout() {
  return (
    <div className="app">
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <header className="app-header">
        <div className="app-header__inner">
          <NavLink to="/" className="brand" end>
            <Network size={20} aria-hidden className="brand__icon" />
            <span>Crawler</span>
          </NavLink>
          {usingMocks && (
            <span className="mock-pill" title="VITE_USE_MOCKS=true: data comes from the in-memory mock client">
              Mock data
            </span>
          )}
          <nav className="nav" aria-label="Primary">
            <NavLink to="/" end className={({ isActive }) => `nav__link${isActive ? ' nav__link--active' : ''}`}>
              <Plus size={16} aria-hidden />
              Start crawl
            </NavLink>
            <NavLink to="/history" className={({ isActive }) => `nav__link${isActive ? ' nav__link--active' : ''}`}>
              <History size={16} aria-hidden />
              History
            </NavLink>
          </nav>
        </div>
      </header>
      <main id="main" className="app-main" tabIndex={-1}>
        <Outlet />
      </main>
    </div>
  );
}
