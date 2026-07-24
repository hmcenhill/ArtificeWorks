// The animated diagram's ONLY domain knowledge (11.4): how each work-order.* event maps to the
// path it travelled through the system, and how the /system/stats numbers become node strain. The
// geometry — where the nodes sit, how the edges curve — is presentation and lives in the component;
// this file knows only *which* nodes and edges an event lit, and in what order.
//
// Honesty note (the whole point of the story): every pulse is a real published event, never a timer.
// Who publishes what is a fact of the backend, not a choice here:
//   • `created` / `scheduled` are published by the **API** (create + the advance endpoint), so they
//     leave the API node. `scheduled` is then consumed by a worker → API → broker → worker.
//   • everything from `materials-reserved` on is published by the **Workers** host and (except the
//     terminal announcements) consumed by it too — worker → broker → worker, the pipeline loop that
//     Workers/Program.cs calls out as really going "out over the broker and back".
//   • `faulted` / `completed` are terminal announcements the pipeline never consumes (their only
//     subscriber is 11.2's dashboard relay), so they stop at the broker.

import type { SystemStats } from "../api/types";

export type DiagramNode = "api" | "broker" | "workers" | "db";
export type DiagramEdge = "publish" | "deliver" | "next" | "api-db" | "workers-db";
export type DiagramTone = "flow" | "good" | "rework" | "fault" | "info";

/** Which two nodes an edge joins, and the natural direction a pulse travels it. */
export const EDGE_ENDS: Record<DiagramEdge, { from: DiagramNode; to: DiagramNode }> = {
  publish: { from: "api", to: "broker" }, // API stages an event; its outbox dispatches it
  deliver: { from: "broker", to: "workers" }, // the broker hands it to a worker
  next: { from: "workers", to: "broker" }, // a worker publishes its next-stage event
  "api-db": { from: "api", to: "db" }, // the API persists the order
  "workers-db": { from: "workers", to: "db" }, // a worker persists its stage's work
};

/** One leg of an event's journey. `reverse` walks the edge against its natural direction. */
export interface Hop {
  edge: DiagramEdge;
  reverse?: boolean;
}

/** The whole journey one event type traces, and the tone every hop of it lights. */
export interface FlowSpec {
  tone: DiagramTone;
  hops: Hop[];
}

// The event → hop table. One entry per routing key in WorkOrderEventTypes.All; a new event type
// shows up here or it simply doesn't animate (the feed still lists it). This is the mapping the
// story calls "the diagram's only domain knowledge".
const FLOW_TABLE: Record<string, FlowSpec> = {
  // Born on the API: written to the database, then announced to the broker.
  "work-order.created": { tone: "info", hops: [{ edge: "api-db" }, { edge: "publish" }] },
  // Advanced on the API, picked up by a worker — the API → broker → worker the story names.
  "work-order.scheduled": {
    tone: "flow",
    hops: [{ edge: "publish" }, { edge: "deliver" }, { edge: "workers-db" }],
  },
  // The pipeline middle: a worker publishes, the broker routes, the next handler consumes and saves.
  "work-order.materials-reserved": { tone: "flow", hops: WORKER_LOOP() },
  "work-order.production-completed": { tone: "flow", hops: WORKER_LOOP() },
  "work-order.inspection-passed": { tone: "good", hops: WORKER_LOOP() },
  "work-order.shipment-scheduled": { tone: "flow", hops: WORKER_LOOP() },
  // The rebuild loop back to production — same path, tinted as trouble so the retry is legible.
  "work-order.rework-required": { tone: "rework", hops: WORKER_LOOP() },
  // Terminal announcements: published to the broker and consumed by nobody in the pipeline.
  "work-order.faulted": { tone: "fault", hops: [{ edge: "next" }] },
  "work-order.completed": { tone: "good", hops: [{ edge: "next" }] },
};

/** worker → broker → worker → db: publish the result, route it, consume it, persist the next stage. */
function WORKER_LOOP(): Hop[] {
  return [{ edge: "next" }, { edge: "deliver" }, { edge: "workers-db" }];
}

/** The path an event traces, or null if it isn't one the diagram animates. */
export function flowFor(eventType: string): FlowSpec | null {
  return FLOW_TABLE[eventType] ?? null;
}

// ---- Node strain, from the slow /system/stats poll. A node's colour says how the *factory* is
// doing, independent of the per-event pulses flowing over it.

export type Strain = "ok" | "strained" | "trouble";

export interface DiagramHealth {
  api: Strain;
  broker: Strain;
  workers: Strain;
  db: Strain;
  /** Surfaced as a badge on the broker: parked messages waiting for a replay (8.3). */
  deadLetters: number;
}

function worse(a: Strain, b: Strain): Strain {
  const rank: Record<Strain, number> = { ok: 0, strained: 1, trouble: 2 };
  return rank[a] >= rank[b] ? a : b;
}

function band(value: number, strained: number, trouble: number): Strain {
  if (value >= trouble) return "trouble";
  if (value >= strained) return "strained";
  return "ok";
}

/**
 * Turns the stats snapshot into a strain per node. The mapping is honest about what each node owns:
 * the **API** publishes, so an outbox backlog strains it; the **broker** is where an unpublished
 * backlog piles up and where parked messages sit; the **workers** are what a parked message means
 * failed; the **database** holds component stock, so a depleted factory (10.4) tints it.
 */
export function healthFrom(stats: SystemStats | null): DiagramHealth {
  if (!stats || !stats.fresh) {
    return { api: "ok", broker: "ok", workers: "ok", db: "ok", deadLetters: 0 };
  }

  const backlog = worse(band(stats.outboxUnsent, 5, 25), band(stats.outboxLagSeconds, 3, 12));
  const parked = band(stats.deadLettersUnreplayed, 1, 5);
  // stockLevelRatio is inverted: low stock is bad, so band on how far *below* full it has fallen.
  const depletion = band(1 - stats.stockLevelRatio, 0.4, 0.7);

  return {
    api: backlog,
    broker: worse(backlog, parked),
    workers: parked,
    db: depletion,
    deadLetters: stats.deadLettersUnreplayed,
  };
}
