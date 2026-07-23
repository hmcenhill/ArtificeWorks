import { Link, NavLink, Outlet } from "react-router-dom";

import { ConnectionStatus } from "./components/ConnectionStatus";

/** The shell every view renders inside: a header that links home, the nav, and the routed content. */
export function AppLayout() {
  return (
    <div className="app">
      <header className="app-header">
        <Link to="/" className="app-brand">
          <span className="app-brand-mark" aria-hidden="true">
            ⚙
          </span>
          <span className="app-brand-text">
            <strong>Hermannsson Artifice Works</strong>
            <span className="app-brand-sub">Factory Floor</span>
          </span>
        </Link>
        <nav className="app-nav" aria-label="Primary">
          <NavLink to="/" end className={({ isActive }) => (isActive ? "is-active" : "")}>
            Board
          </NavLink>
          <NavLink to="/create" className={({ isActive }) => (isActive ? "is-active" : "")}>
            Create order
          </NavLink>
          <NavLink to="/controls" className={({ isActive }) => (isActive ? "is-active" : "")}>
            Dials
          </NavLink>
        </nav>
        <ConnectionStatus />
      </header>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  );
}
