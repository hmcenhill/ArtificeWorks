// Presentation for the event feed: a human label and a tone per routing key. This mirrors the
// backend's WorkOrderEventTypes (the honest inventory of what the factory announces) — a new event
// type shows up on the feed with its raw key until it's given a label here.

export type EventTone = "flow" | "good" | "bad" | "info";

interface EventMeta {
  label: string;
  tone: EventTone;
}

const EVENT_META: Record<string, EventMeta> = {
  "work-order.created": { label: "Created", tone: "info" },
  "work-order.scheduled": { label: "Scheduled", tone: "flow" },
  "work-order.materials-reserved": { label: "Materials reserved", tone: "flow" },
  "work-order.production-completed": { label: "Production done", tone: "flow" },
  "work-order.rework-required": { label: "Rework required", tone: "bad" },
  "work-order.inspection-passed": { label: "Inspection passed", tone: "good" },
  "work-order.shipment-scheduled": { label: "Shipment booked", tone: "flow" },
  "work-order.faulted": { label: "Faulted", tone: "bad" },
  "work-order.completed": { label: "Completed", tone: "good" },
};

export function eventMeta(eventType: string): EventMeta {
  return EVENT_META[eventType] ?? { label: eventType, tone: "info" };
}
