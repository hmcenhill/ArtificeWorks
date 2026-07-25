import { useState } from "react";

import { replayDeadLetter } from "../api/client";
import { ApiProblem, errorMessage } from "../api/problems";

/**
 * Puts one parked message back (8.3), used from both the list and the detail. Handles the three
 * answers the endpoint gives: 202 (accepted — the order should start moving again), 409
 * `dead_letter_already_replayed` (offer "Replay again" with `force`, the "did the first one work?"
 * second click, not an error), and anything else as a sentence. `alreadyReplayed` seeds force mode
 * when the row already carries a `replayedUtc`.
 */
export function ReplayButton({
  id,
  alreadyReplayed,
  onReplayed,
}: {
  id: string;
  alreadyReplayed: boolean;
  onReplayed?: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [force, setForce] = useState(alreadyReplayed);
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function replay() {
    if (busy) return;
    setBusy(true);
    setError(null);
    setNote(null);
    try {
      const result = await replayDeadLetter(id, force);
      setNote(result.summary || "Replay accepted — watch the order pick up where it left off.");
      onReplayed?.();
    } catch (err) {
      // A concurrent replay (or a row that was already replayed) — switch to force and invite a retry.
      if (err instanceof ApiProblem && err.code === "dead_letter_already_replayed") {
        setForce(true);
        setNote("This was already replayed. Press again to replay it anyway.");
      } else {
        setError(errorMessage(err));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="replay">
      <button className="button button-primary replay-button" disabled={busy} onClick={replay}>
        {busy ? "Replaying…" : force ? "Replay again" : "Replay"}
      </button>
      {note && <span className="replay-note">{note}</span>}
      {error && <span className="form-error replay-error">{error}</span>}
    </div>
  );
}
