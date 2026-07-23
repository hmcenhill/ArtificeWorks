import { Link } from "react-router-dom";

import type { WorkOrderListItem } from "../api/types";
import { absoluteTime, relativeTime } from "../util/time";
import { OriginBadge } from "./OriginBadge";

/** One order as a board card. A card is a link to that order's timeline. */
export function OrderCard({ order, now }: { order: WorkOrderListItem; now: number }) {
  return (
    <Link to={`/orders/${order.id}`} className="order-card" data-origin={order.origin}>
      <div className="order-card-top">
        <span className="order-card-product">{order.productName}</span>
        <OriginBadge origin={order.origin} />
      </div>
      <div className="order-card-meta">
        <code className="order-card-id" title={order.id}>
          {order.id.slice(0, 8)}
        </code>
        <time dateTime={order.updatedUtc} title={absoluteTime(order.updatedUtc)}>
          {relativeTime(order.updatedUtc, now)}
        </time>
      </div>
    </Link>
  );
}
