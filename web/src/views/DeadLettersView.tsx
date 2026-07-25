import { useMemo } from "react";
import { Link, useSearchParams } from "react-router-dom";

import { fetchDeadLetters, type DeadLetterQuery } from "../api/client";
import type { DeadLetterPage, DeadLetterSummary } from "../api/types";
import { ReplayButton } from "../components/ReplayButton";
import { eventMeta } from "../domain/events";
import { useLiveData } from "../hooks/useLiveData";
import { absoluteTime, relativeTime } from "../util/time";

type ReplayFilter = "waiting" | "replayed" | "all";

const FILTERS: { value: ReplayFilter; label: string }[] = [
  { value: "waiting", label: "Waiting" },
  { value: "replayed", label: "Replayed" },
  { value: "all", label: "All" },
];

/**
 * The dead-letter inspector (12.3): the first browser surface over `dead_letters` (8.3). What has
 * parked, newest first — each row readable and replayable, so a stranger can poison a message, watch
 * it park here, and put it back without hunting. Not stream-driven (the relay carries only business
 * events, not park/replay notifications — see the handoff finding), so it fetches on load and after a
 * replay, with a manual refresh. The `workOrderId` query param scopes it to one order, which is how
 * the order detail links in to close the loop.
 */
export function DeadLettersView() {
  const [params, setParams] = useSearchParams();
  const workOrderId = params.get("workOrderId") ?? undefined;
  const filter = (params.get("filter") as ReplayFilter) || "waiting";

  const query = useMemo<DeadLetterQuery>(
    () => ({
      workOrderId,
      replayed: filter === "all" ? undefined : filter === "replayed",
      pageSize: 50,
    }),
    [workOrderId, filter],
  );

  const { data, loading, error, refreshing, reload } = useLiveData<DeadLetterPage>(
    (signal) => fetchDeadLetters(query, signal),
    [workOrderId, filter],
  );

  function setFilter(next: ReplayFilter) {
    const p = new URLSearchParams(params);
    p.set("filter", next);
    setParams(p, { replace: true });
  }

  function clearOrderScope() {
    const p = new URLSearchParams(params);
    p.delete("workOrderId");
    setParams(p, { replace: true });
  }

  const items = data?.items ?? [];

  return (
    <section className="panel dlq">
      <div className="panel-toolbar">
        <Link to="/" className="back-link">
          ← Board
        </Link>
      </div>

      <header className="panel-header">
        <h1>Dead-letter inspector</h1>
        <p className="panel-caption">
          Messages the pipeline couldn't process, parked after exhausting the retry ladder (Epic 8).
          Read one, then press <strong>Replay</strong> to put it back on the line — the dedupe keys
          make a replay safe even if the first attempt half-succeeded.
        </p>
      </header>

      <div className="dlq-toolbar">
        <div className="segmented" role="group" aria-label="Filter by replay state">
          {FILTERS.map((f) => (
            <button
              key={f.value}
              type="button"
              className={filter === f.value ? "is-active" : ""}
              onClick={() => setFilter(f.value)}
            >
              {f.label}
            </button>
          ))}
        </div>
        {workOrderId && (
          <button type="button" className="dlq-scope" onClick={clearOrderScope} title="Show all orders">
            order {workOrderId.slice(0, 8)} ✕
          </button>
        )}
        <button type="button" className="refresh-button" onClick={reload}>
          ↻ Refresh
        </button>
        {refreshing && <span className="board-live" aria-label="refreshing" />}
      </div>

      {loading ? (
        <p className="notice">Reading the dead-letter queue…</p>
      ) : error ? (
        <div className="notice notice-error">
          <p>Couldn't read the dead letters. Is the API running?</p>
          <button type="button" onClick={reload}>
            Try again
          </button>
        </div>
      ) : items.length === 0 ? (
        <p className="notice">
          Nothing parked{workOrderId ? " for this order" : ""}
          {filter === "waiting" ? " and waiting" : ""}. A clean queue is a healthy factory — or{" "}
          <Link to="/" className="inline-link">
            go break something
          </Link>
          .
        </p>
      ) : (
        <ul className="dlq-list">
          {items.map((letter) => (
            <DeadLetterRow key={letter.id} letter={letter} onReplayed={reload} />
          ))}
        </ul>
      )}
    </section>
  );
}

function DeadLetterRow({
  letter,
  onReplayed,
}: {
  letter: DeadLetterSummary;
  onReplayed: () => void;
}) {
  const meta = eventMeta(letter.eventType);
  const now = Date.now();
  const replayed = letter.replayedUtc != null;

  return (
    <li className={`dlq-row ${replayed ? "dlq-row-replayed" : ""}`}>
      <div className="dlq-row-main">
        <div className="dlq-row-top">
          <span className={`feed-label tone-${meta.tone}`}>{meta.label}</span>
          <span className="dlq-attempts" title="Delivery attempts before parking">
            {letter.attempts} attempt{letter.attempts === 1 ? "" : "s"}
          </span>
          <time className="feed-time" dateTime={letter.parkedUtc} title={absoluteTime(letter.parkedUtc)}>
            parked {relativeTime(letter.parkedUtc, now)}
          </time>
          {replayed && <span className="dlq-badge-replayed">replayed{letter.replayCount > 1 ? ` ×${letter.replayCount}` : ""}</span>}
        </div>
        <p className="dlq-error">{letter.error}</p>
        <div className="dlq-row-sub">
          <Link to={`/dead-letters/${letter.id}`} className="inline-link">
            Read payload
          </Link>
          {letter.workOrderId && (
            <Link to={`/orders/${letter.workOrderId}`} className="dlq-order" title={letter.workOrderId}>
              order {letter.workOrderId.slice(0, 8)}
            </Link>
          )}
        </div>
      </div>
      <ReplayButton id={letter.id} alreadyReplayed={replayed} onReplayed={onReplayed} />
    </li>
  );
}
