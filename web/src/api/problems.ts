// The API speaks RFC 7807 (3.3): every non-2xx carries a ProblemDetails with a stable, machine
// -readable `code`. This is the one place that turns those codes into a sentence a visitor can
// read — a shared mapping keyed by `code`, so a 409 becomes "this order isn't in Delivery yet",
// not a raw status. The API is the authority (client-side gating is only UX); this is how a
// rejection that gets through is explained.

/** Mirrors the ProblemDetails the API returns, plus the `code` extension ApiControllerBase stamps. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  /** The stable reason code (ProblemCodes on the server). The thing we branch on. */
  code?: string;
}

/**
 * A failed API call carrying the parsed ProblemDetails. Views catch this and render
 * {@link problemMessage}. Falls back to a bare message when the body wasn't problem+json.
 */
export class ApiProblem extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails,
  ) {
    super(problem.detail ?? problem.title ?? `Request failed (${status})`);
    this.name = "ApiProblem";
  }

  get code(): string | undefined {
    return this.problem.code;
  }
}

// One sentence per known code. Human, second-person, and about *what the visitor can do* where
// there is something — a held order is rescued by releasing it, a booked order is already on its
// way. Anything not listed falls back to the server's own `detail`, which is already a sentence.
const MESSAGES: Record<string, string> = {
  // Lifecycle transitions (3.3, state machine).
  work_order_not_found: "That work order no longer exists.",
  terminal_state: "This order has finished its life — nothing more can be done to it.",
  invalid_transition: "That move isn't allowed from where this order is right now.",
  must_release_first: "This order is on hold — release it before moving it on.",
  already_held: "This order is already on hold.",
  not_held: "This order isn't on hold, so there's nothing to release.",
  attempt_out_of_sequence: "The factory is mid-attempt on this order — try again in a moment.",

  // Inspection verdicts (6.2).
  order_not_in_inspection: "This order isn't in Inspection, so a verdict can't be recorded.",
  unit_not_found: "That unit doesn't belong to this order.",
  unit_already_inspected: "That unit has already been judged — the inspector got there first.",
  scrap_reason_required: "A failed unit needs a reason.",

  // Shipping (7.2, 7.3).
  order_not_in_delivery: "This order isn't resting in Delivery yet, so a carrier can't be booked.",
  shipment_already_booked: "A carrier is already booked for this order.",
  unknown_carrier: "That isn't a carrier this factory works with — pick one from the list.",
  carrier_unavailable: "That carrier has no capacity right now. Release the order and try again.",
  nothing_to_ship: "No units passed inspection, so there's nothing to ship.",

  // Create (8.4 idempotency).
  product_not_found: "That product isn't in the catalog.",
  idempotency_key_reused: "That looked like a repeat with different details — nothing was created.",
  idempotency_key_in_flight: "An identical request is still being processed — give it a second.",

  // Simulation dials (10.2).
  simulation_setting_out_of_range: "One of those values is outside what the factory will accept.",

  validation_failed: "The request was missing something required.",
};

/** The human sentence for a ProblemDetails, by code, falling back to the server's detail. */
export function problemMessage(problem: ProblemDetails | undefined): string {
  if (!problem) return "Something went wrong.";
  const byCode = problem.code ? MESSAGES[problem.code] : undefined;
  return byCode ?? problem.detail ?? problem.title ?? "Something went wrong.";
}

/** The sentence for any caught error — an {@link ApiProblem} maps by code, anything else is generic. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiProblem) return problemMessage(error.problem);
  return "Couldn't reach the factory. Is the API running?";
}
