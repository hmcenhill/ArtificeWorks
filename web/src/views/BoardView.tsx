import { useMemo, useState } from "react";

import { fetchWorkOrders } from "../api/client";
import type { WorkOrderListItem, WorkOrderOrigin, WorkOrderStatus } from "../api/types";
import { OrderCard } from "../components/OrderCard";
import { EXCEPTION_STAGES, PIPELINE_STAGES, type StageDef } from "../domain/stages";
import { usePolledData } from "../hooks/usePolledData";

const POLL_MS = 4000;

type OriginFilter = "all" | WorkOrderOrigin;

const ORIGIN_FILTERS: { value: OriginFilter; label: string }[] = [
  { value: "all", label: "Everyone" },
  { value: "Visitor", label: "Visitors" },
  { value: "Simulated", label: "Robots" },
];

export function BoardView() {
  const [originFilter, setOriginFilter] = useState<OriginFilter>("all");

  const query = useMemo(
    () => (originFilter === "all" ? {} : { origin: [originFilter] }),
    [originFilter],
  );

  const { data, error, loading, refreshing, refresh } = usePolledData<WorkOrderListItem[]>(
    (signal) => fetchWorkOrders(query, signal),
    [originFilter],
    POLL_MS,
  );

  // A single clock for the whole render, so every card's relative time is consistent per tick.
  const now = Date.now();

  const byStatus = useMemo(() => groupByStatus(data ?? []), [data]);

  if (loading) {
    return <p className="notice">Loading the factory floor…</p>;
  }
  if (error) {
    return (
      <div className="notice notice-error">
        <p>Couldn't reach the factory. Is the API running?</p>
        <button type="button" onClick={refresh}>
          Try again
        </button>
      </div>
    );
  }

  const orders = data ?? [];

  return (
    <section className="board">
      <div className="board-toolbar">
        <div className="board-summary">
          <span className="board-count">{orders.length}</span>
          <span>order{orders.length === 1 ? "" : "s"} on the floor</span>
          {refreshing && <span className="board-live" aria-label="refreshing" />}
        </div>
        <div className="board-controls">
          <div className="segmented" role="group" aria-label="Filter by who ordered">
            {ORIGIN_FILTERS.map((f) => (
              <button
                key={f.value}
                type="button"
                className={originFilter === f.value ? "is-active" : ""}
                onClick={() => setOriginFilter(f.value)}
              >
                {f.label}
              </button>
            ))}
          </div>
          <button type="button" className="refresh-button" onClick={refresh}>
            ↻ Refresh
          </button>
        </div>
      </div>

      {orders.length === 0 ? (
        <p className="notice">
          Nothing on the floor yet. Create a work order (the API's <code>POST /work-orders</code>),
          or let the simulation generate some.
        </p>
      ) : (
        <>
          <div className="stage-row" aria-label="Pipeline">
            {PIPELINE_STAGES.map((stage) => (
              <StageColumn key={stage.status} stage={stage} orders={byStatus[stage.status]} now={now} />
            ))}
          </div>

          <h2 className="stage-band-title">Off the line</h2>
          <div className="stage-row stage-row-exceptions" aria-label="Off the line">
            {EXCEPTION_STAGES.map((stage) => (
              <StageColumn key={stage.status} stage={stage} orders={byStatus[stage.status]} now={now} />
            ))}
          </div>
        </>
      )}
    </section>
  );
}

function StageColumn({
  stage,
  orders,
  now,
}: {
  stage: StageDef;
  orders: WorkOrderListItem[] | undefined;
  now: number;
}) {
  const items = orders ?? [];
  return (
    <div className={`stage-column stage-${stage.status.toLowerCase()}`}>
      <header className="stage-header">
        <span className="stage-label">{stage.label}</span>
        <span className="stage-count">{items.length}</span>
      </header>
      <div className="stage-cards">
        {items.map((order) => (
          <OrderCard key={order.id} order={order} now={now} />
        ))}
      </div>
    </div>
  );
}

function groupByStatus(
  orders: WorkOrderListItem[],
): Partial<Record<WorkOrderStatus, WorkOrderListItem[]>> {
  const groups: Partial<Record<WorkOrderStatus, WorkOrderListItem[]>> = {};
  for (const order of orders) {
    (groups[order.status] ??= []).push(order);
  }
  return groups;
}
