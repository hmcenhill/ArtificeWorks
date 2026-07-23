import { Link, Outlet } from "react-router-dom";

/** The shell every view renders inside: a header that links home, and the routed content. */
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
      </header>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  );
}
