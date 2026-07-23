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

/** Mirrors Application.Data.ProductSummaryDto — a catalog row for the create form (11.3). */
export interface ProductSummary {
  itemId: string;
  itemName: string;
}

/** Mirrors Domain.Models.Materials.UnitStatus — one serialized unit's own state. */
export type UnitStatus = "Built" | "Passed" | "Scrapped";

/** Mirrors Domain.Models.Shipping.ShipmentStatus. */
export type ShipmentStatus = "Booked" | "Dispatched" | "Cancelled";

/**
 * One serialized unit and its verdict — mirrors Application.Data.StockUnitDto.
 * `status` is the client (name) shape; the wire carries a number (see client.ts adapter).
 */
export interface StockUnit {
  serialNumber: string;
  status: UnitStatus;
  buildAttempt: number;
  builtUtc: string;
  inspectedUtc: string | null;
  scrapReason: string | null;
}

/** The parcel — mirrors Application.Data.ShipmentDto. `status` is the name shape (see client.ts). */
export interface Shipment {
  carrier: string;
  trackingNumber: string;
  status: ShipmentStatus;
  bookedUtc: string;
  estimatedArrivalUtc: string;
  dispatchedUtc: string | null;
  serialNumbers: string[];
}

/**
 * The full order — mirrors Application.Data.WorkOrderDto, but as the client uses it: `status`,
 * `origin` and the units'/shipment's statuses are the *name* shapes. Unlike the board list DTO,
 * the wire WorkOrderDto serializes these enums as **numbers** (the name converter is confined to
 * the list DTO to keep existing API tests green), so client.ts maps them on the way in.
 */
export interface WorkOrder {
  id: string;
  status: WorkOrderStatus;
  orderedItemId: string;
  orderItemQty: number;
  origin: WorkOrderOrigin;
  passedQty: number;
  buildAttempt: number;
  units: StockUnit[];
  shipment: Shipment | null;
}

/** The body of POST /work-orders. Origin defaults to Visitor server-side; the form always sends it. */
export interface CreateWorkOrderBody {
  requestor: string;
  itemId: string;
  qty: number;
  origin?: WorkOrderOrigin;
  notes?: string;
}

/**
 * Mirrors Application.Data.SimulationSettingsDto — the factory's live dials (10.2). PUT *replaces*
 * the whole object, so the controls panel loads this, edits a few fields, and sends it all back.
 * `source`, `resolvedRungs` and `takesEffectWithinSeconds` are read-only on the response.
 */
export interface SimulationSettings {
  pacingEnabled: boolean;
  paceSecondsScheduled: number;
  paceSecondsMaterialsReserved: number;
  paceSecondsProductionCompleted: number;
  paceSecondsReworkRequired: number;
  paceSecondsInspectionPassed: number;
  paceSecondsShipmentScheduled: number;
  paceJitter: number;

  failureRate: number;
  autoInspect: boolean;
  refusalRate: number;
  autoBook: boolean;
  maxRebuildAttempts: number;

  generationEnabled: boolean;
  generationIntervalSeconds: number;
  maxInFlight: number;

  worldSweepIntervalHours: number;
  retireAfterHours: number;

  /** "configured" (appsettings) or "overridden" (a row is in force). Read-only. */
  source: string;
  /** Rung each stage's duration snapped to, keyed by routing key. Absent when pacing is off. */
  resolvedRungs: Record<string, string> | null;
  /** How long until every host runs on these values — the snapshot is eventually consistent. */
  takesEffectWithinSeconds: number;
}
