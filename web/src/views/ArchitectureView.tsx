import { Link } from "react-router-dom";

import type { SystemStats } from "../api/types";
import { ArchitectureDiagram } from "../components/ArchitectureDiagram";
import { healthFrom } from "../domain/hops";
import { useSystemStats } from "../hooks/useSystemStats";

/**
 * The showpiece (11.4): the event-driven architecture drawn as a living system. Components pulse as
 * real events flow through them — every pulse is a genuine work-order.* event the factory just
 * published on 11.2's stream, never a decorative timer — and the nodes take on the factory's *strain*
 * (outbox backlog, parked messages, depleted stock) from a slow /system/stats poll. It is the pitch
 * on the résumé made visible: watch an order's event leave the API, sit in the broker, reach a
 * worker and come back, all without reading a line of code.
 */
export function ArchitectureView() {
  const stats = useSystemStats();
  const health = healthFrom(stats);

  return (
    <section className="arch">
      <div className="arch-toolbar">
        <Link to="/" className="back-link">
          ← Board
        </Link>
      </div>

      <header className="arch-header">
        <h1>The factory, live</h1>
        <p className="arch-caption">
          The event-driven backbone as a moving picture. Each pulse is a real message the factory
          just published — an order's event leaving the API, waiting in the broker, reaching a worker
          and coming back around the rework loop. Node colour is live strain from{" "}
          <code>/system/stats</code>: a publish backlog, a parked message, or a depleted stock room
          tints the node it belongs to.
        </p>
      </header>

      <div className="arch-stage">
        <ArchitectureDiagram health={health} />
      </div>

      <Legend />
      <StatsStrip stats={stats} />
    </section>
  );
}

function Legend() {
  return (
    <ul className="arch-legend" aria-label="Legend">
      <li>
        <span className="legend-swatch tone-flow" /> event flowing
      </li>
      <li>
        <span className="legend-swatch tone-good" /> completed / passed
      </li>
      <li>
        <span className="legend-swatch tone-rework" /> rework loop
      </li>
      <li>
        <span className="legend-swatch tone-fault" /> fault
      </li>
      <li>
        <span className="legend-strain strain-strained" /> strained
      </li>
      <li>
        <span className="legend-strain strain-trouble" /> in trouble
      </li>
    </ul>
  );
}

/** The aggregate numbers behind the picture, from the same slow poll that tints the nodes. */
function StatsStrip({ stats }: { stats: SystemStats | null }) {
  if (!stats || !stats.fresh) {
    return <p className="arch-stats arch-stats-waiting">Waiting for the first stats snapshot…</p>;
  }
  return (
    <dl className="arch-stats">
      <Stat label="In flight" value={stats.workOrdersInFlight} />
      <Stat label="Handled" value={stats.messagesHandledSinceStart} hint="messages since start" />
      <Stat label="Retried" value={stats.messagesRetriedSinceStart} hint="climbed the retry ladder" />
      <Stat
        label="Outbox lag"
        value={`${stats.outboxLagSeconds.toFixed(1)}s`}
        strained={stats.outboxUnsent > 5}
        hint={`${stats.outboxUnsent} unsent`}
      />
      <Stat
        label="Parked"
        value={stats.deadLettersUnreplayed}
        strained={stats.deadLettersUnreplayed > 0}
        hint="awaiting replay"
      />
      <Stat label="Stock" value={`${Math.round(stats.stockLevelRatio * 100)}%`} strained={stats.stockLevelRatio < 0.6} />
    </dl>
  );
}

function Stat({
  label,
  value,
  hint,
  strained,
}: {
  label: string;
  value: number | string;
  hint?: string;
  strained?: boolean;
}) {
  return (
    <div className={`arch-stat ${strained ? "is-strained" : ""}`}>
      <dt>{label}</dt>
      <dd>{value}</dd>
      {hint && <span className="arch-stat-hint">{hint}</span>}
    </div>
  );
}
