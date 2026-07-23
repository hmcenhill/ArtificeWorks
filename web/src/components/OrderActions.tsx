import { useState } from "react";

import {
  advanceOrder,
  bookShipment,
  cancelOrder,
  holdOrder,
  recordVerdict,
  releaseOrder,
} from "../api/client";
import { errorMessage } from "../api/problems";
import type { WorkOrder } from "../api/types";
import { KNOWN_CARRIERS } from "../domain/carriers";
import { STATUS_LABEL } from "../domain/stages";

/**
 * The visitor's decision moments (11.3), surfaced on the order detail only where the order's state
 * allows the action — the endpoints enforce it with 409s, so this client-side gating is UX, not the
 * guard. Every button calls the *ordinary* pipeline endpoint (no back door); a rejection that races
 * through is shown as a readable sentence with its reason. On success we reload the order, since the
 * SignalR event may not have landed yet.
 */
export function OrderActions({ order, onActed }: { order: WorkOrder; onActed: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Run any action, reload on success, and turn a rejection into a sentence. One in-flight at a time.
  async function run(action: () => Promise<void>) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await action();
      onActed();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  const s = order.status;
  const isPipeline = ["Intake", "Scheduled", "InProcess", "Inspection", "Delivery"].includes(s);
  const canRelease = s === "OnHold";
  const canBook = s === "Delivery" && !order.shipment;
  const builtUnits = order.units.filter((u) => u.status === "Built");
  const canVerdict = s === "Inspection" && builtUnits.length > 0;
  const canCancel = s !== "Completed" && s !== "Cancelled";

  // Nothing a visitor can do to a finished order — say so rather than showing an empty panel.
  const hasAnyAction = isPipeline || canRelease || canBook || canVerdict || canCancel;

  return (
    <section className="actions">
      <header className="actions-header">
        <h2>What you can do</h2>
        <span className={`status-badge status-${s.toLowerCase()}`}>{STATUS_LABEL[s]}</span>
      </header>

      {!hasAnyAction ? (
        <p className="actions-none">This order has finished its life — there's nothing left to do.</p>
      ) : (
        <>
          {canRelease && (
            <p className="actions-hint">
              This order is on hold. A visitor is the only thing that rescues a held order — release
              it to send it back into the pipeline, or the factory's sweep eventually retires it.
            </p>
          )}

          {canVerdict && (
            <VerdictForm order={order} busy={busy} onSubmit={(serial, passed, reason) =>
              run(() => recordVerdict(order.id, serial, passed, reason))
            } />
          )}

          {canBook && (
            <CarrierForm busy={busy} onSubmit={(carrier) => run(() => bookShipment(order.id, carrier))} />
          )}

          <div className="actions-row">
            {canRelease && (
              <button className="button button-primary" disabled={busy} onClick={() => run(() => releaseOrder(order.id))}>
                Release from hold
              </button>
            )}
            {isPipeline && (
              <button className="button" disabled={busy} onClick={() => run(() => advanceOrder(order.id))}>
                Approve / advance
              </button>
            )}
            {isPipeline && (
              <button className="button" disabled={busy} onClick={() => run(() => holdOrder(order.id))}>
                Put on hold
              </button>
            )}
            {canCancel && (
              <button className="button button-danger" disabled={busy} onClick={() => run(() => cancelOrder(order.id))}>
                Cancel
              </button>
            )}
          </div>
        </>
      )}

      {error && <p className="form-error">{error}</p>}
    </section>
  );
}

/** Book a carrier — offered in Delivery when auto-book is off. Empty means "let the factory choose". */
function CarrierForm({
  busy,
  onSubmit,
}: {
  busy: boolean;
  onSubmit: (carrier: string) => void;
}) {
  const [carrier, setCarrier] = useState("");
  return (
    <div className="action-card">
      <h3>Book a carrier</h3>
      <p className="action-card-sub">
        This order is resting in Delivery. Choose who carries it — or let the factory pick.
      </p>
      <div className="action-inline">
        <select className="field-input" value={carrier} disabled={busy} onChange={(e) => setCarrier(e.target.value)}>
          <option value="">Let the factory choose</option>
          {KNOWN_CARRIERS.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
        <button className="button button-primary" disabled={busy} onClick={() => onSubmit(carrier)}>
          Book
        </button>
      </div>
    </div>
  );
}

/** Record a verdict for one built-but-unjudged unit — the manual inspector. */
function VerdictForm({
  order,
  busy,
  onSubmit,
}: {
  order: WorkOrder;
  busy: boolean;
  onSubmit: (serial: string, passed: boolean, reason?: string) => void;
}) {
  const built = order.units.filter((u) => u.status === "Built");
  const [serial, setSerial] = useState(built[0]?.serialNumber ?? "");
  const [passed, setPassed] = useState(true);
  const [reason, setReason] = useState("");

  const needsReason = !passed && reason.trim().length === 0;

  return (
    <div className="action-card">
      <h3>Record an inspection verdict</h3>
      <p className="action-card-sub">
        {built.length} unit{built.length === 1 ? "" : "s"} awaiting a verdict. Judge one by hand — the
        same path the automatic inspector uses.
      </p>
      <div className="action-fields">
        <label className="field">
          <span className="field-label">Unit</span>
          <select className="field-input" value={serial} disabled={busy} onChange={(e) => setSerial(e.target.value)}>
            {built.map((u) => (
              <option key={u.serialNumber} value={u.serialNumber}>
                {u.serialNumber.slice(0, 8)} · attempt {u.buildAttempt}
              </option>
            ))}
          </select>
        </label>

        <div className="segmented" role="group" aria-label="Verdict">
          <button
            type="button"
            className={passed ? "is-active" : ""}
            disabled={busy}
            onClick={() => setPassed(true)}
          >
            ✓ Pass
          </button>
          <button
            type="button"
            className={!passed ? "is-active" : ""}
            disabled={busy}
            onClick={() => setPassed(false)}
          >
            ✕ Fail
          </button>
        </div>
      </div>

      {!passed && (
        <label className="field">
          <span className="field-label">Reason for scrapping</span>
          <input
            className="field-input"
            type="text"
            placeholder="e.g. Misaligned escapement"
            value={reason}
            disabled={busy}
            onChange={(e) => setReason(e.target.value)}
          />
        </label>
      )}

      <button
        className="button button-primary"
        disabled={busy || !serial || needsReason}
        onClick={() => onSubmit(serial, passed, passed ? undefined : reason.trim())}
      >
        Record verdict
      </button>
    </div>
  );
}
