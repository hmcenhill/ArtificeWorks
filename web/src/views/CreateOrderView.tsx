import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";

import { createWorkOrder, fetchProducts } from "../api/client";
import { errorMessage } from "../api/problems";
import type { ProductSummary } from "../api/types";
import { useLiveData } from "../hooks/useLiveData";

/**
 * Give the visitor hands (11.3): create a work order from a product template. On success it routes
 * straight to the new order's live timeline, so the visitor watches the very thing they made travel
 * the pipeline — the same path a simulated order takes, only tagged Visitor.
 */
export function CreateOrderView() {
  const navigate = useNavigate();
  const { data: products, loading, error } = useLiveData<ProductSummary[]>(
    (signal) => fetchProducts(signal),
    [],
  );

  const [itemId, setItemId] = useState("");
  const [qty, setQty] = useState(1);
  const [requestor, setRequestor] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  // Default the product once the catalog arrives.
  useEffect(() => {
    if (!itemId && products && products.length > 0) {
      setItemId(products[0].itemId);
    }
  }, [products, itemId]);

  // One idempotency key per distinct order (8.4): a double-click reuses it and makes one order, but
  // changing the template or quantity mints a fresh key so a genuinely different order isn't refused
  // as a reused key.
  const idempotencyKey = useRef(crypto.randomUUID());
  useEffect(() => {
    idempotencyKey.current = crypto.randomUUID();
  }, [itemId, qty]);

  const selected = useMemo(
    () => products?.find((p) => p.itemId === itemId),
    [products, itemId],
  );

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!itemId || qty < 1 || submitting) return;
    setSubmitting(true);
    setSubmitError(null);
    try {
      const order = await createWorkOrder(
        { requestor: requestor.trim() || "A visitor", itemId, qty, origin: "Visitor" },
        idempotencyKey.current,
      );
      navigate(`/orders/${order.id}`);
    } catch (err) {
      setSubmitError(errorMessage(err));
      setSubmitting(false);
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
        <h1>Create a work order</h1>
        <p className="panel-caption">
          Pick a line and a quantity. This goes through the factory's ordinary{" "}
          <code>POST /work-orders</code> — there is no special path — so your order travels the whole
          pipeline exactly as a robot's does, tagged <strong>Visitor</strong>. You'll be taken to its
          live timeline to watch.
        </p>
      </header>

      {loading ? (
        <p className="notice">Loading the catalog…</p>
      ) : error ? (
        <p className="notice notice-error">Couldn't load the catalog. Is the API running?</p>
      ) : (
        <form className="form" onSubmit={submit}>
          <label className="field">
            <span className="field-label">Product line</span>
            <select
              className="field-input"
              value={itemId}
              onChange={(e) => setItemId(e.target.value)}
            >
              {(products ?? []).map((p) => (
                <option key={p.itemId} value={p.itemId}>
                  {p.itemName} ({p.itemId})
                </option>
              ))}
            </select>
          </label>

          <label className="field">
            <span className="field-label">Quantity</span>
            <input
              className="field-input"
              type="number"
              min={1}
              max={100}
              value={qty}
              onChange={(e) => setQty(Math.max(1, Math.floor(Number(e.target.value) || 1)))}
            />
          </label>

          <label className="field">
            <span className="field-label">
              Your name <span className="field-optional">(optional)</span>
            </span>
            <input
              className="field-input"
              type="text"
              placeholder="A visitor"
              value={requestor}
              onChange={(e) => setRequestor(e.target.value)}
            />
          </label>

          {submitError && <p className="form-error">{submitError}</p>}

          <div className="form-actions">
            <button type="submit" className="button button-primary" disabled={submitting || !itemId}>
              {submitting ? "Creating…" : `Create ${qty} × ${selected?.itemName ?? "order"}`}
            </button>
          </div>
        </form>
      )}
    </section>
  );
}
