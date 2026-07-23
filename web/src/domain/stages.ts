import type { WorkOrderStatus } from "../api/types";

export interface StageDef {
  status: WorkOrderStatus;
  label: string;
}

/** The manufacturing pipeline, left to right — the same walk the state machine advances through. */
export const PIPELINE_STAGES: StageDef[] = [
  { status: "Intake", label: "Intake" },
  { status: "Scheduled", label: "Scheduled" },
  { status: "InProcess", label: "In Process" },
  { status: "Inspection", label: "Inspection" },
  { status: "Delivery", label: "Delivery" },
  { status: "Completed", label: "Completed" },
];

/** Off the line: surfaced apart from the flow, because they are not a stage anything advances to. */
export const EXCEPTION_STAGES: StageDef[] = [
  { status: "OnHold", label: "On Hold" },
  { status: "Fault", label: "Fault" },
  { status: "Cancelled", label: "Cancelled" },
];

export const STATUS_LABEL: Record<WorkOrderStatus, string> = Object.fromEntries(
  [...PIPELINE_STAGES, ...EXCEPTION_STAGES].map((s) => [s.status, s.label]),
) as Record<WorkOrderStatus, string>;
