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
  /** 13.2: this product builds a component rather than being sold. The order form hides these. */
  isSubAssembly: boolean;
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

/**
 * Mirrors Application.Data.SystemStatsDto — the factory's vital signs as plain JSON (9.2), the
 * slow-polled source of the architecture diagram's node *strain* tint (11.4). The dictionaries key
 * on enum names (status / origin). Every `*SinceStart` is a monotonic process counter.
 */
export interface SystemStats {
  asOfUtc: string;
  /** False until the first snapshot has been taken — zeros that mean "not yet", not "none". */
  fresh: boolean;
  workOrdersByStatus: Record<string, number>;
  workOrdersTotal: number;
  workOrdersInFlight: number;
  workOrdersByOrigin: Record<string, number>;
  workOrdersInFlightByOrigin: Record<string, number>;
  /** Unsent outbox rows — a publish backlog. THE broker/API strain signal. */
  outboxUnsent: number;
  /** Age in seconds of the oldest unsent outbox row; 0 when there is none. */
  outboxLagSeconds: number;
  /** Parked messages nobody has replayed — the trouble path made countable. */
  deadLettersUnreplayed: number;
  /** On-hand stock as a fraction of seed levels: 1.0 is a full factory (10.4). */
  stockLevelRatio: number;
  messagesHandledSinceStart: number;
  messagesRetriedSinceStart: number;
  messagesParkedSinceStart: number;
  messagesReplayedSinceStart: number;
  messagesPacedSinceStart: number;
  outboxPublishedSinceStart: number;
  ordersRetiredSinceStart: number;
}

/**
 * Mirrors Application.Chaos.InjectedFaultKind — the three levers a visitor can arm against one
 * order (Epic 12). Crosses the wire as its *name* (the enum has a scoped string converter), so
 * unlike WorkOrderDto's enums these need no numeric decoding.
 */
export type InjectedFaultKind = "FailInspection" | "TransientOnce" | "Poison";

/** Mirrors Api.Controllers.ChaosArmedDto — what was armed, echoed back so the UI can confirm. */
export interface ChaosArmed {
  faultId: string;
  workOrderId: string;
  kind: InjectedFaultKind;
  armedUtc: string;
  armedBy: string;
}

/**
 * Mirrors Application.Data.DeadLetterSummaryDto — one row of the dead-letter list (8.3), shaped
 * for the table a person scans: what failed, whose order, how hard the system tried, and the first
 * line of why. `replayedUtc` non-null means it has already been put back.
 */
export interface DeadLetterSummary {
  id: string;
  eventType: string;
  correlationId: string;
  workOrderId: string | null;
  attempts: number;
  error: string;
  parkedUtc: string;
  replayedUtc: string | null;
  replayCount: number;
}

/** Mirrors Application.Data.DeadLetterDetailDto — the summary plus the full payload, for reading. */
export interface DeadLetterDetail extends DeadLetterSummary {
  payload: string;
}

/** Mirrors Application.Data.DeadLetterPageDto — a page of dead letters, newest first. */
export interface DeadLetterPage {
  items: DeadLetterSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Mirrors Application.Recovery.ReplayResult — the 202 body of a replay. `outcome` is a numeric
 *  enum on the wire (the global convention); the UI only reads the human `summary`. */
export interface ReplayResult {
  outcome: number;
  summary: string;
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
