import { Link, useParams } from "react-router-dom";

import { ApiError, fetchDeadLetter } from "../api/client";
import type { DeadLetterDetail } from "../api/types";
import { ReplayButton } from "../components/ReplayButton";
import { eventMeta } from "../domain/events";
import { useLiveData } from "../hooks/useLiveData";
import { absoluteTime, relativeTime } from "../util/time";

/**
 * One parked message in full (12.3): the payload and the whole error, for a human to read before
 * deciding to put it back. Closes the loop the other way — a link back to the order this message
 * belonged to, so a visitor can follow one order from "arm poison" to "watch it complete".
 */
export function DeadLetterDetailView() {
  const { id = "" } = useParams();

  const { data, loading, error, reload } = useLiveData<DeadLetterDetail>(
    (signal) => fetchDeadLetter(id, signal),
    [id],
  );

  return (
    <section className="panel dlq-detail">
      <div className="panel-toolbar">
        <Link to="/dead-letters" className="back-link">
          ← Dead letters
        </Link>
      </div>

      {loading ? (
        <p className="notice">Reading the record…</p>
      ) : error ? (
        <div className="notice notice-error">
          <p>{error instanceof ApiError && error.status === 404 ? "That dead letter is gone." : "Couldn't read this record."}</p>
          <Link to="/dead-letters" className="inline-link">
            Back to the list
          </Link>
        </div>
      ) : (
        data && <DeadLetterBody letter={data} onReplayed={reload} />
      )}
    </section>
  );
}

function DeadLetterBody({ letter, onReplayed }: { letter: DeadLetterDetail; onReplayed: () => void }) {
  const meta = eventMeta(letter.eventType);
  const now = Date.now();
  const replayed = letter.replayedUtc != null;

  return (
    <>
      <header className="panel-header">
        <h1>
          <span className={`feed-label tone-${meta.tone}`}>{meta.label}</span> dead letter
        </h1>
        <p className="panel-caption">
          Parked after {letter.attempts} attempt{letter.attempts === 1 ? "" : "s"},{" "}
          <time dateTime={letter.parkedUtc} title={absoluteTime(letter.parkedUtc)}>
            {relativeTime(letter.parkedUtc, now)}
          </time>
          {replayed && (
            <>
              {" · "}
              <span className="dlq-badge-replayed">
                replayed{letter.replayCount > 1 ? ` ×${letter.replayCount}` : ""}
              </span>
            </>
          )}
        </p>
      </header>

      <dl className="dlq-facts">
        <div>
          <dt>Event type</dt>
          <dd>
            <code>{letter.eventType}</code>
          </dd>
        </div>
        <div>
          <dt>Correlation</dt>
          <dd>
            <code>{letter.correlationId}</code>
          </dd>
        </div>
        <div>
          <dt>Work order</dt>
          <dd>
            {letter.workOrderId ? (
              <Link to={`/orders/${letter.workOrderId}`} className="inline-link">
                {letter.workOrderId}
              </Link>
            ) : (
              "—"
            )}
          </dd>
        </div>
      </dl>

      <section className="dlq-block">
        <h2>Why it parked</h2>
        <pre className="dlq-pre dlq-pre-error">{letter.error}</pre>
      </section>

      <section className="dlq-block">
        <h2>Payload</h2>
        <pre className="dlq-pre">{formatPayload(letter.payload)}</pre>
      </section>

      <div className="dlq-detail-actions">
        <ReplayButton id={letter.id} alreadyReplayed={replayed} onReplayed={onReplayed} />
        {letter.workOrderId && (
          <Link to={`/orders/${letter.workOrderId}`} className="button">
            Watch the order
          </Link>
        )}
      </div>
    </>
  );
}

/** Pretty-print the stored payload if it's JSON; otherwise show it verbatim. */
function formatPayload(payload: string): string {
  try {
    return JSON.stringify(JSON.parse(payload), null, 2);
  } catch {
    return payload;
  }
}
