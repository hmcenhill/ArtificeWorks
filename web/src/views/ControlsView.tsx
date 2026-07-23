import { useEffect, useState } from "react";
import { Link } from "react-router-dom";

import { fetchSimulation, updateSimulation } from "../api/client";
import { errorMessage } from "../api/problems";
import type { SimulationSettings } from "../api/types";
import { useLiveData } from "../hooks/useLiveData";

// The pace durations, paired with the routing key their resolved rung is reported under, so the
// panel can show "5s → 5s rung" next to each edit (10.2: a small change may not move a rung, and
// that looks like a bug unless the UI shows the rung it landed on).
const PACE_FIELDS: { key: keyof SimulationSettings; label: string; routingKey: string }[] = [
  { key: "paceSecondsScheduled", label: "Scheduling", routingKey: "work-order.scheduled" },
  { key: "paceSecondsMaterialsReserved", label: "Material picking", routingKey: "work-order.materials-reserved" },
  { key: "paceSecondsProductionCompleted", label: "Production", routingKey: "work-order.production-completed" },
  { key: "paceSecondsReworkRequired", label: "Rework", routingKey: "work-order.rework-required" },
  { key: "paceSecondsInspectionPassed", label: "Inspection", routingKey: "work-order.inspection-passed" },
  { key: "paceSecondsShipmentScheduled", label: "Shipping", routingKey: "work-order.shipment-scheduled" },
];

/**
 * The factory's live dials (11.3 over 10.2's PUT /system/simulation). Turn the failure rate up and
 * the next order starts failing inspections live, no restart. These are **global** tuning — they
 * retune the whole factory, every order, not one — which is the line 10.2 drew against Epic 12's
 * per-order failure injection, and the panel says so.
 */
export function ControlsView() {
  const { data, loading, error } = useLiveData<SimulationSettings>(
    (signal) => fetchSimulation(signal),
    [],
  );

  const [draft, setDraft] = useState<SimulationSettings | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [applied, setApplied] = useState<SimulationSettings | null>(null);

  // Seed the editable draft once the current settings arrive.
  useEffect(() => {
    if (data && !draft) setDraft(data);
  }, [data, draft]);

  function set<K extends keyof SimulationSettings>(key: K, value: SimulationSettings[K]) {
    setDraft((d) => (d ? { ...d, [key]: value } : d));
  }

  async function save() {
    if (!draft || saving) return;
    setSaving(true);
    setSaveError(null);
    try {
      // PUT is whole-object; the server rejects out-of-range with 422 and changes nothing.
      const result = await updateSimulation(draft);
      setDraft(result);
      setApplied(result);
    } catch (err) {
      setSaveError(errorMessage(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="panel">
      <div className="panel-toolbar">
        <Link to="/" className="back-link">
          ← Board
        </Link>
      </div>

      <header className="panel-header">
        <h1>Factory dials</h1>
        <p className="panel-caption">
          The knobs the factory runs on, turnable while it runs. Raise the failure rate and the next
          order starts failing inspection live; turn generation off and the robots stop asking.
        </p>
        <p className="panel-warning">
          ⚠ These are <strong>global</strong> — they retune the whole factory, every order, visitor
          and robot alike. This isn't a way to fail <em>one</em> order (that's a later story); it's
          the whole line's tuning.
        </p>
      </header>

      {loading || !draft ? (
        <p className="notice">Loading the dials…</p>
      ) : error ? (
        <p className="notice notice-error">Couldn't read the dials. Is the API running?</p>
      ) : (
        <>
          <fieldset className="dials">
            <legend>Demand</legend>
            <Toggle label="Generate orders" value={draft.generationEnabled} onChange={(v) => set("generationEnabled", v)} />
            <NumberDial label="Every … seconds" value={draft.generationIntervalSeconds} min={1} onChange={(v) => set("generationIntervalSeconds", v)} />
            <NumberDial label="Max orders in flight" value={draft.maxInFlight} min={1} onChange={(v) => set("maxInFlight", v)} />
          </fieldset>

          <fieldset className="dials">
            <legend>Quality</legend>
            <RateDial label="Failure rate" value={draft.failureRate} onChange={(v) => set("failureRate", v)} />
            <Toggle label="Auto-inspect" value={draft.autoInspect} onChange={(v) => set("autoInspect", v)} />
            <NumberDial label="Max rebuild attempts" value={draft.maxRebuildAttempts} min={0} onChange={(v) => set("maxRebuildAttempts", v)} />
          </fieldset>

          <fieldset className="dials">
            <legend>Shipping</legend>
            <RateDial label="Carrier refusal rate" value={draft.refusalRate} onChange={(v) => set("refusalRate", v)} />
            <Toggle label="Auto-book a carrier" value={draft.autoBook} onChange={(v) => set("autoBook", v)} />
          </fieldset>

          <fieldset className="dials">
            <legend>Pacing</legend>
            <Toggle label="Pacing enabled" value={draft.pacingEnabled} onChange={(v) => set("pacingEnabled", v)} />
            <RateDial label="Jitter" value={draft.paceJitter} onChange={(v) => set("paceJitter", v)} />
            {draft.pacingEnabled &&
              PACE_FIELDS.map((f) => (
                <NumberDial
                  key={f.key}
                  label={`${f.label} (s)`}
                  value={draft[f.key] as number}
                  min={0}
                  step={0.5}
                  rung={applied?.resolvedRungs?.[f.routingKey]}
                  onChange={(v) => set(f.key, v as never)}
                />
              ))}
          </fieldset>

          {saveError && <p className="form-error">{saveError}</p>}

          <div className="form-actions form-actions-split">
            <button className="button button-primary" disabled={saving} onClick={save}>
              {saving ? "Applying…" : "Apply to the whole factory"}
            </button>
            {applied && (
              <span className="apply-note">
                <span className={`source-badge source-${applied.source}`}>{applied.source}</span>
                Takes effect within {applied.takesEffectWithinSeconds}s
              </span>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function Toggle({ label, value, onChange }: { label: string; value: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="dial dial-toggle">
      <span className="dial-label">{label}</span>
      <button
        type="button"
        role="switch"
        aria-checked={value}
        className={`switch ${value ? "switch-on" : ""}`}
        onClick={() => onChange(!value)}
      >
        <span className="switch-knob" />
      </button>
    </label>
  );
}

function NumberDial({
  label,
  value,
  min,
  step = 1,
  rung,
  onChange,
}: {
  label: string;
  value: number;
  min?: number;
  step?: number;
  rung?: string;
  onChange: (v: number) => void;
}) {
  return (
    <label className="dial">
      <span className="dial-label">
        {label}
        {rung && <span className="dial-rung" title="The rung this duration resolved to">→ {rung} rung</span>}
      </span>
      <input
        className="field-input dial-input"
        type="number"
        min={min}
        step={step}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </label>
  );
}

/** A 0.0–1.0 probability: a slider plus its value. Soft-clamped in the UI; the 422 is the authority. */
function RateDial({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <label className="dial">
      <span className="dial-label">
        {label} <span className="dial-value">{Math.round(value * 100)}%</span>
      </span>
      <input
        className="dial-range"
        type="range"
        min={0}
        max={1}
        step={0.05}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </label>
  );
}
