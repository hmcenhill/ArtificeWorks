// The one place that knows the wire shapes. These mirror the API DTOs this epic consumes, by
// hand — so a contract drift (a renamed field, a dropped status) is a compile error here rather
// than a silent runtime surprise across the app. JSON is camelCase (System.Text.Json web
// defaults); enums cross the wire as their names (see WorkOrderListItemDto / TimelineKind).

/** Mirrors Domain.Models.WorkOrderStatus — the pipeline stages plus the off-pipeline states. */
export type WorkOrderStatus =
  | "Intake"
  | "Scheduled"
  | "InProcess"
  | "Inspection"
  | "Delivery"
  | "Completed"
  | "OnHold"
  | "Fault"
  | "Cancelled";

/** Mirrors Domain.Models.WorkOrderOrigin — who asked for the order. */
export type WorkOrderOrigin = "Visitor" | "Simulated";

/** Mirrors Application.Data.WorkOrderListItemDto — the board's slim per-order row. */
export interface WorkOrderListItem {
  id: string;
  productName: string;
  status: WorkOrderStatus;
  origin: WorkOrderOrigin;
  createdUtc: string;
  updatedUtc: string;
}

/** Mirrors Application.Data.TimelineKind — the stable entry kinds a client switches on. */
export type TimelineKind =
  | "state"
  | "pick"
  | "build"
  | "inspection"
  | "verdict"
  | "shipment";

/**
 * Mirrors Application.Data.TimelineEntryDto. `detail` is the per-kind payload, typed by `kind`
 * rather than by the schema, so it stays an open bag here and the view narrows it per kind.
 */
export interface TimelineEntry {
  at: string;
  kind: TimelineKind;
  by: string | null;
  summary: string;
  detail: Record<string, unknown> | null;
}

/** Mirrors Application.Data.WorkOrderTimelineDto — one order's whole story, in order. */
export interface WorkOrderTimeline {
  workOrderId: string;
  entries: TimelineEntry[];
}
