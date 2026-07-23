import { useCallback } from "react";
import { Link, useParams } from "react-router-dom";

import { ApiError, fetchTimeline, fetchWorkOrder } from "../api/client";
import type { TimelineEntry, TimelineKind, WorkOrder, WorkOrderTimeline } from "../api/types";
import { OrderActions } from "../components/OrderActions";
import { useLiveData } from "../hooks/useLiveData";
import { useReloadOnStream } from "../hooks/useReloadOnStream";
import { absoluteTime, relativeTime } from "../util/time";

const KIND_ICON: Record<TimelineKind, string> = {
  state: "◆",
  pick: "🧰",
  build: "🔧",
  inspection: "🔎",
  verdict: "✓",
  shipment: "📦",
};

const KIND_LABEL: Record<TimelineKind, string> = {
  state: "State",
  pick: "Materials",
  build: "Build",
  inspection: "Inspection",
  verdict: "Verdict",
  shipment: "Shipment",
};

export function OrderDetailView() {
  const { id = "" } = useParams();

  const { data, error, loading, refreshing, reload } = useLiveData<WorkOrderTimeline>(
    (signal) => fetchTimeline(id, signal),
    [id],
  );

  // The order itself, for the decision moments — status, units and shipment drive which actions are
  // legal. Fetched alongside the timeline so both stay live off the same stream.
  const { data: order, reload: reloadOrder } = useLiveData<WorkOrder>(
    (signal) => fetchWorkOrder(id, signal),
    [id],
  );

  // Live while open: an event for *this* order re-fetches both the timeline and the order, so a
  // watched order animates through its stages and its actions re-gate. A reconnect reconciles.
  const reloadBoth = useCallback(() => {
    reload();
    reloadOrder();
  }, [reload, reloadOrder]);
  useReloadOnStream(reloadBoth, (event) => event.workOrderId === id);

  return (
    <section className="detail">
      <div className="detail-toolbar">
        <Link to="/" className="back-link">
          ← Board
        </Link>
        {refreshing && <span className="board-live" aria-label="refreshing" />}
        <button type="button" className="refresh-button" onClick={reloadBoth}>
          ↻ Refresh
        </button>
      </div>

      {loading ? (
        <p className="notice">Loading the order's story…</p>
      ) : error ? (
        <DetailError error={error} onRetry={reloadBoth} />
      ) : (
        <>
          {order && <OrderActions order={order} onActed={reloadBoth} />}
          <TimelineBody timeline={data!} />
        </>
      )}
    </section>
  );
}

function DetailError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const notFound = error instanceof ApiError && error.status === 404;
  return (
    <div className="notice notice-error">
      <p>{notFound ? "No such work order." : "Couldn't load this order."}</p>
      {!notFound && (
        <button type="button" onClick={onRetry}>
          Try again
        </button>
      )}
    </div>
  );
}

function TimelineBody({ timeline }: { timeline: WorkOrderTimeline }) {
  const now = Date.now();
  return (
    <>
      <header className="detail-header">
        <h1>Work order</h1>
        <code className="detail-id">{timeline.workOrderId}</code>
        <p className="detail-caption">
          What happened, in order — derived from the records the factory keeps. It is the story, not
          the message log: nothing here proves an event flowed. This view is live — an event for
          this order re-fetches it, so it grows as the order moves.
        </p>
      </header>

      {timeline.entries.length === 0 ? (
        <p className="notice">No timeline entries yet.</p>
      ) : (
        <ol className="timeline">
          {timeline.entries.map((entry, i) => (
            <TimelineRow key={i} entry={entry} now={now} />
          ))}
        </ol>
      )}
    </>
  );
}

function TimelineRow({ entry, now }: { entry: TimelineEntry; now: number }) {
  return (
    <li className={`timeline-row kind-${entry.kind}`}>
      <div className="timeline-marker" aria-hidden="true">
        {KIND_ICON[entry.kind]}
      </div>
      <div className="timeline-content">
        <div className="timeline-line">
          <span className={`kind-tag kind-tag-${entry.kind}`}>{KIND_LABEL[entry.kind]}</span>
          <span className="timeline-summary">{entry.summary}</span>
        </div>
        <div className="timeline-sub">
          <time dateTime={entry.at} title={absoluteTime(entry.at)}>
            {relativeTime(entry.at, now)}
          </time>
          {entry.by && <span className="timeline-by">by {entry.by}</span>}
        </div>
        <TimelineDetail entry={entry} />
      </div>
    </li>
  );
}

/** The per-kind payload, typed by `kind` rather than by the schema. Best-effort and defensive:
 *  a shape the API adds to later still renders its summary, just without the extra chips. */
function TimelineDetail({ entry }: { entry: TimelineEntry }) {
  const d = entry.detail;
  if (!d) {
    return null;
  }

  switch (entry.kind) {
    case "pick": {
      const lines = asArray(d.lines);
      if (lines.length === 0) return null;
      return (
        <ul className="chip-list">
          {lines.map((line, i) => (
            <li key={i} className="chip">
              {str(line?.componentId)} × {num(line?.quantity)}
            </li>
          ))}
        </ul>
      );
    }
    case "build":
      return (
        <div className="chip-list">
          <span className="chip">attempt {num(d.attemptNumber)}</span>
          <span className="chip">{num(d.unitsBuilt)} built</span>
        </div>
      );
    case "inspection":
      return (
        <div className="chip-list">
          <span className="chip">attempt {num(d.attemptNumber)}</span>
          <span className="chip chip-pass">{num(d.unitsPassed)} passed</span>
          <span className="chip chip-scrap">{num(d.unitsScrapped)} scrapped</span>
        </div>
      );
    case "verdict": {
      const passed = str(d.status) === "Passed";
      return (
        <div className="chip-list">
          <span className={`chip ${passed ? "chip-pass" : "chip-scrap"}`}>{str(d.status)}</span>
          <span className="chip">unit {str(d.serialNumber).slice(0, 8)}</span>
          {d.scrapReason ? <span className="chip">{str(d.scrapReason)}</span> : null}
        </div>
      );
    }
    case "shipment":
      return (
        <div className="chip-list">
          {d.carrier ? <span className="chip">{str(d.carrier)}</span> : null}
          {d.trackingNumber ? <span className="chip">#{str(d.trackingNumber)}</span> : null}
          {d.status ? <span className="chip">{str(d.status)}</span> : null}
        </div>
      );
    case "state":
    default:
      return null;
  }
}

// Small defensive readers for the open `detail` bag.
function asArray(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value) ? (value as Record<string, unknown>[]) : [];
}
function str(value: unknown): string {
  return value == null ? "" : String(value);
}
function num(value: unknown): number {
  return typeof value === "number" ? value : Number(value ?? 0);
}
