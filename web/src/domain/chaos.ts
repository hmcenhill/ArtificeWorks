import type { InjectedFaultKind, WorkOrderStatus } from "../api/types";

// The three levers, and where each one bites — mirrored from Application.Chaos (the enum) and
// ChaosService.IsInjectable (the state gate). This client-side legality is UX only: it decides which
// buttons to *offer*. The API's 404/409 is the authority when a race slips through (an order that
// moved on between render and click). Keep this table in step with IsInjectable — the honesty here is
// "don't offer a fault the factory will refuse", not "enforce it".

export interface FaultDef {
  kind: InjectedFaultKind;
  /** Button label — plain, and honest about the simulated worker death. */
  label: string;
  /** One sentence: what this does and, crucially, where to watch it recover. The honesty principle. */
  blurb: string;
}

export const FAULTS: FaultDef[] = [
  {
    kind: "FailInspection",
    label: "Fail this inspection",
    blurb:
      "Forces this order's next inspection to fail. It routes through the ordinary rework loop — " +
      "rebuilt, re-inspected, and eventually Faulted if it keeps failing. Watch the timeline and the feed.",
  },
  {
    kind: "TransientOnce",
    label: "Kill a worker mid-pick",
    blurb:
      "A simulated worker death: the picking handler throws once before it acknowledges the message, " +
      "so the broker redelivers it and the next attempt finishes the job. Nothing is really killed. " +
      "Watch the architecture diagram — the message climbs the retry ladder, then recovers.",
  },
  {
    kind: "Poison",
    label: "Poison a message",
    blurb:
      "Sends an unprocessable message: the picking handler rejects it as poison and it parks straight " +
      "into the dead-letter queue. Watch the diagram's parked badge rise — then open the dead letters " +
      "and replay it to drive the order home.",
  },
];

/**
 * Whether this fault can meaningfully be armed against an order in this state — mirrors
 * ChaosService.IsInjectable. A finished, cancelled or already-faulted order can take nothing; a
 * fail-inspection can't bite once the order has reached Delivery; the two broker faults fire at the
 * picking stage, so they're gone once the order is InProcess or beyond.
 */
export function isFaultLegal(kind: InjectedFaultKind, status: WorkOrderStatus): boolean {
  if (status === "Completed" || status === "Cancelled" || status === "Fault") {
    return false;
  }
  if (kind === "FailInspection") {
    return status !== "Delivery";
  }
  // TransientOnce / Poison — only at or before the pick (Intake, Scheduled, OnHold).
  return status !== "InProcess" && status !== "Inspection" && status !== "Delivery";
}
