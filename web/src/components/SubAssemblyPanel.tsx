import { Link } from "react-router-dom";

import type { WorkOrder } from "../api/types";

/**
 * The parent/child relationship, both ways (13.3) — 11.x's loop-closes-both-ways pattern applied to
 * the one link in the system that connects two orders.
 *
 * A **child** says what it is made for and links up. A **parent** says how many sub-assemblies it is
 * waiting on and links down to each. Renders nothing at all for the overwhelming majority of orders,
 * which are neither, so the detail view is unchanged unless there is something to say.
 */
export function SubAssemblyPanel({ order }: { order: WorkOrder }) {
  const isChild = order.parentWorkOrderId !== null;
  const hasChildren = order.children.length > 0;

  if (!isChild && !hasChildren) {
    return null;
  }

  return (
    <section className="sub-assembly-panel">
      {isChild && (
        <p className="sub-assembly-lineage">
          <span className="sub-assembly-badge">⛓ sub-assembly</span> building{" "}
          {order.forComponentId && <code>{order.forComponentId}</code>} for{" "}
          <Link to={`/orders/${order.parentWorkOrderId}`}>
            order {order.parentWorkOrderId!.slice(0, 8)}
          </Link>
          . It is stocked rather than shipped: its passed units credit the shelf, and the parent
          picks them back off it.
        </p>
      )}

      {hasChildren && (
        <>
          <p className="sub-assembly-lineage">
            {order.liveChildCount > 0 ? (
              <>
                Waiting on <strong>{order.liveChildCount}</strong> sub-assembly order
                {order.liveChildCount === 1 ? "" : "s"}. This order resumes picking when they
                finish.
              </>
            ) : (
              <>
                All {order.children.length} sub-assembly order
                {order.children.length === 1 ? "" : "s"} finished.
              </>
            )}
          </p>
          <ul className="sub-assembly-children">
            {order.children.map((child) => (
              <li key={child.id}>
                <Link to={`/orders/${child.id}`} className="chip">
                  {child.qty} × {child.forComponentId ?? "—"}
                </Link>
                <span className={`chip ${child.isLive ? "" : "chip-pass"}`}>{child.status}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}
