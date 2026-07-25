import { useState } from "react";
import { Link } from "react-router-dom";

import { armChaos } from "../api/client";
import { errorMessage } from "../api/problems";
import type { InjectedFaultKind, WorkOrder } from "../api/types";
import { FAULTS, isFaultLegal } from "../domain/chaos";

/**
 * The money shot (12.3): the contextual sabotage panel, a deliberate sibling to 11.3's decision
 * moments rather than mixed in with them — this is danger territory, styled and captioned so nobody
 * mistakes it for an ordinary action. It offers only the fault(s) the order's stage can actually take
 * (mirroring the API's state gate for UX; the 409 `chaos_target_not_injectable` is the authority when
 * a race loses), and each button arms via the ordinary `POST /system/chaos` — no dashboard back door,
 * the lever *is* an endpoint. The honesty principle is a visible feature here: the copy says what will
 * happen, that a "killed" worker is a simulated death, and where to watch the recovery play out.
 */
export function ChaosPanel({ order }: { order: WorkOrder }) {
  const [busy, setBusy] = useState<InjectedFaultKind | null>(null);
  const [armed, setArmed] = useState<InjectedFaultKind | null>(null);
  const [error, setError] = useState<string | null>(null);

  const legal = FAULTS.filter((f) => isFaultLegal(f.kind, order.status));

  // Nothing to sabotage on a finished/faulted order — say so plainly rather than show an empty panel.
  if (legal.length === 0) {
    return null;
  }

  async function arm(kind: InjectedFaultKind) {
    if (busy) return;
    setBusy(kind);
    setError(null);
    setArmed(null);
    try {
      await armChaos(order.id, kind);
      setArmed(kind);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="chaos">
      <header className="chaos-header">
        <h2>Break something</h2>
        <span className="chaos-tag">simulated chaos</span>
      </header>
      <p className="chaos-intro">
        Arm a failure against <em>this order only</em> — nothing else on the floor is touched. Then
        watch Epic 8's reliability machinery heal it: on the{" "}
        <Link to="/architecture" className="inline-link">
          architecture diagram
        </Link>
        , in the live feed, or in the{" "}
        <Link to={`/dead-letters?workOrderId=${order.id}`} className="inline-link">
          dead-letter inspector
        </Link>
        .
      </p>

      <div className="chaos-list">
        {legal.map((f) => (
          <div key={f.kind} className="chaos-card">
            <div className="chaos-card-body">
              <h3>{f.label}</h3>
              <p className="chaos-blurb">{f.blurb}</p>
              {armed === f.kind && (
                <p className="chaos-armed">
                  ✓ Armed. It fires the next time this order reaches that stage — keep an eye on the
                  feed and the diagram.
                </p>
              )}
            </div>
            <button
              className="button button-danger chaos-button"
              disabled={busy !== null}
              onClick={() => arm(f.kind)}
            >
              {busy === f.kind ? "Arming…" : f.label}
            </button>
          </div>
        ))}
      </div>

      {error && <p className="form-error">{error}</p>}
    </section>
  );
}
