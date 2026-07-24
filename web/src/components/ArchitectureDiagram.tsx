import { useEffect, useRef } from "react";

import type { DashboardEvent } from "../api/realtime";
import {
  EDGE_ENDS,
  flowFor,
  type DiagramEdge,
  type DiagramHealth,
  type DiagramNode,
} from "../domain/hops";
import { useRealtime } from "../realtime/RealtimeProvider";

// The showpiece (11.4): the topology from docs/architecture.md drawn as a living picture, its nodes
// and edges pulsing as real events flow through them. Every pulse is a genuine work-order.* event
// the factory just published (via 11.2's SignalR stream) — never a decorative timer. The node
// *colour* is factory strain from a slow /system/stats poll, passed in as `health`.
//
// The animation is driven imperatively by one requestAnimationFrame loop that mutates SVG element
// attributes directly. React never re-renders per frame — that is what keeps it smooth when the feed
// is busy (sim generation on) with dozens of overlapping pulses. React owns only the slow strain
// tint (~5s) and the static drawing.

// ---- Geometry. Fixed coordinates in a 760×430 viewBox; the domain layer (hops.ts) knows the node
// and edge *ids*, this knows where they sit. Nodes are laid out left→right: API, broker, workers,
// with the database below, exactly the shape architecture.md draws.
const NODE_C: Record<DiagramNode, { x: number; y: number }> = {
  api: { x: 110, y: 160 },
  broker: { x: 380, y: 160 },
  workers: { x: 650, y: 160 },
  db: { x: 380, y: 340 },
};

type EdgeGeo =
  | { kind: "line"; p: [[number, number], [number, number]] }
  | { kind: "curve"; p: [[number, number], [number, number], [number, number]] };

const GEO: Record<DiagramEdge, EdgeGeo> = {
  publish: { kind: "line", p: [[160, 160], [330, 160]] },
  deliver: { kind: "line", p: [[430, 160], [600, 160]] },
  // The worker → broker return, bowed up over the forward lane so the pipeline loop reads as a loop.
  next: { kind: "curve", p: [[628, 128], [515, 64], [402, 128]] },
  "api-db": { kind: "line", p: [[118, 192], [348, 308]] },
  "workers-db": { kind: "line", p: [[642, 192], [412, 308]] },
};

const NODE_IDS: DiagramNode[] = ["api", "broker", "workers", "db"];
const EDGE_IDS: DiagramEdge[] = ["publish", "deliver", "next", "api-db", "workers-db"];

/** SVG path string for an edge, for the static line and its bright flow overlay. */
function pathD(edge: DiagramEdge): string {
  const g = GEO[edge];
  if (g.kind === "line") {
    const [[x0, y0], [x1, y1]] = g.p;
    return `M${x0} ${y0} L${x1} ${y1}`;
  }
  const [[x0, y0], [cx, cy], [x1, y1]] = g.p;
  return `M${x0} ${y0} Q${cx} ${cy} ${x1} ${y1}`;
}

/** Point at parameter t∈[0,1] along an edge; `reverse` walks it the other way. */
function pointAt(edge: DiagramEdge, t: number, reverse: boolean): { x: number; y: number } {
  const g = GEO[edge];
  const u = reverse ? 1 - t : t;
  if (g.kind === "line") {
    const [[x0, y0], [x1, y1]] = g.p;
    return { x: x0 + (x1 - x0) * u, y: y0 + (y1 - y0) * u };
  }
  const [[x0, y0], [cx, cy], [x1, y1]] = g.p;
  const m = 1 - u;
  return { x: m * m * x0 + 2 * m * u * cx + u * u * x1, y: m * m * y0 + 2 * m * u * cy + u * u * y1 };
}

// ---- Timing. Each hop's dot travels for HOP_MS; between hops it rests DWELL_MS at the waypoint —
// the visible "sitting in the broker" beat. The *real* pacing (10.1) shows as the gap between an
// order's successive events arriving, which needs no help from us; this dwell just makes the
// waypoint legible. Kept short so a pulse doesn't outlive the event that spawned it by much.
const HOP_MS = 820;
const DWELL_MS = 420;
const POOL_SIZE = 40; // reused dot elements; a burst beyond this simply doesn't add more dots
const MAX_PULSES = 200; // hard cap so a flood can never grow the active list unbounded

interface Pulse {
  hops: { edge: DiagramEdge; reverse: boolean }[];
  tone: string;
  startedAt: number;
}

interface Lit {
  until: number;
  tone: string;
}

export function ArchitectureDiagram({ health }: { health: DiagramHealth }) {
  const { subscribe } = useRealtime();

  const pulsesRef = useRef<Pulse[]>([]);
  const nodeLitRef = useRef<Partial<Record<DiagramNode, Lit>>>({});
  const edgeLitRef = useRef<Partial<Record<DiagramEdge, Lit>>>({});

  // Refs into the DOM the rAF loop mutates directly, bypassing React's render.
  const dotRefs = useRef<(SVGCircleElement | null)[]>([]);
  const haloRefs = useRef<Partial<Record<DiagramNode, SVGCircleElement | null>>>({});
  const flowRefs = useRef<Partial<Record<DiagramEdge, SVGPathElement | null>>>({});

  const rafRef = useRef<number | null>(null);
  const runningRef = useRef(false);
  const reducedRef = useRef(
    typeof window !== "undefined" &&
      window.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true,
  );

  // Subscribe to the live stream: each event that maps to a flow becomes a pulse. The pulse is
  // scheduled the moment its envelope arrives — concurrent orders light overlapping hops.
  useEffect(() => {
    const unsubscribe = subscribe((event: DashboardEvent) => {
      const spec = flowFor(event.eventType);
      if (!spec) return;
      const pulse: Pulse = {
        hops: spec.hops.map((h) => ({ edge: h.edge, reverse: h.reverse ?? false })),
        tone: spec.tone,
        startedAt: performance.now(),
      };
      const pulses = pulsesRef.current;
      pulses.push(pulse);
      if (pulses.length > MAX_PULSES) pulses.splice(0, pulses.length - MAX_PULSES);
      ensureRunning();
    });
    return unsubscribe;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subscribe]);

  // The animation loop. Runs only while there is something to draw; goes idle when the factory is
  // quiet (an event restarts it), so a diagram left on a screen doesn't burn a frame forever.
  useEffect(() => {
    ensureRunning();
    return () => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
      runningRef.current = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function ensureRunning() {
    if (runningRef.current) return;
    runningRef.current = true;
    rafRef.current = requestAnimationFrame(frame);
  }

  function frame(now: number) {
    const reduced = reducedRef.current;
    const pulses = pulsesRef.current;
    const nodeLit = nodeLitRef.current;
    const edgeLit = edgeLitRef.current;

    const keep: Pulse[] = [];
    let slot = 0;

    for (const p of pulses) {
      const r = evalPulse(p, now);
      if (r.done) continue;
      keep.push(p);

      edgeLit[r.edge] = { until: now + 160, tone: p.tone };
      if (r.t < 0.2) nodeLit[r.from] = { until: now + 320, tone: p.tone };
      if (r.t > 0.8) nodeLit[r.to] = { until: now + 320, tone: p.tone };

      // Position a pooled dot (skipped under reduced-motion — the node/edge glow still fires).
      if (!reduced && slot < POOL_SIZE) {
        const dot = dotRefs.current[slot++];
        if (dot) {
          dot.setAttribute("cx", String(r.point.x));
          dot.setAttribute("cy", String(r.point.y));
          dot.setAttribute("class", `pulse-dot tone-${p.tone}`);
          dot.style.opacity = "1";
        }
      }
    }
    pulsesRef.current = keep;

    for (let i = slot; i < POOL_SIZE; i++) {
      const dot = dotRefs.current[i];
      if (dot) dot.style.opacity = "0";
    }

    // Node halos fade out over ~320ms after a pulse last touched the node.
    for (const node of NODE_IDS) {
      const lit = nodeLit[node];
      const halo = haloRefs.current[node];
      if (!halo) continue;
      const k = lit ? Math.max(0, (lit.until - now) / 320) : 0;
      halo.style.opacity = String(k);
      if (lit && k > 0) halo.setAttribute("class", `node-halo tone-${lit.tone}`);
    }

    // Edge overlays brighten while a dot is on them, then fade.
    for (const edge of EDGE_IDS) {
      const lit = edgeLit[edge];
      const flow = flowRefs.current[edge];
      if (!flow) continue;
      const k = lit ? Math.max(0, (lit.until - now) / 160) : 0;
      flow.style.opacity = String(k);
      if (lit && k > 0) flow.setAttribute("class", `edge-flow tone-${lit.tone}`);
    }

    // Keep going while anything is still moving or fading; otherwise park until the next event.
    const active =
      keep.length > 0 ||
      NODE_IDS.some((n) => (nodeLit[n]?.until ?? 0) > now) ||
      EDGE_IDS.some((e) => (edgeLit[e]?.until ?? 0) > now);
    if (active) {
      rafRef.current = requestAnimationFrame(frame);
    } else {
      runningRef.current = false;
      rafRef.current = null;
    }
  }

  return (
    <svg
      className="arch-svg"
      viewBox="0 0 760 430"
      role="img"
      aria-label="Live architecture diagram: the API, the RabbitMQ broker, the workers and the Postgres database, with pulses tracing real events as they flow through the system."
    >
      <defs>
        <marker
          id="arch-arrow"
          viewBox="0 0 10 10"
          refX="9"
          refY="5"
          markerWidth="7"
          markerHeight="7"
          orient="auto-start-reverse"
        >
          <path d="M0 0 L10 5 L0 10 z" className="arch-arrowhead" />
        </marker>
      </defs>

      {/* Static edges. Flow edges (message paths) get an arrow; persistence edges are dashed. */}
      {EDGE_IDS.map((edge) => {
        const persist = edge === "api-db" || edge === "workers-db";
        return (
          <path
            key={edge}
            className={`arch-edge ${persist ? "arch-edge-persist" : ""}`}
            d={pathD(edge)}
            markerEnd={persist ? undefined : "url(#arch-arrow)"}
          />
        );
      })}

      {/* Bright flow overlays, one per edge — opacity driven by the rAF loop. */}
      {EDGE_IDS.map((edge) => (
        <path
          key={`flow-${edge}`}
          ref={(el) => (flowRefs.current[edge] = el)}
          className="edge-flow"
          d={pathD(edge)}
          style={{ opacity: 0 }}
        />
      ))}

      {/* Edge labels. */}
      <text className="arch-edge-label" x="245" y="150">
        publish
      </text>
      <text className="arch-edge-label" x="515" y="150">
        deliver
      </text>
      <text className="arch-edge-label" x="515" y="58" textAnchor="middle">
        next event
      </text>

      {/* Node halos, behind the shapes — the pulse glow. */}
      {NODE_IDS.map((node) => (
        <circle
          key={`halo-${node}`}
          ref={(el) => (haloRefs.current[node] = el)}
          className="node-halo"
          cx={NODE_C[node].x}
          cy={NODE_C[node].y}
          r={48}
          style={{ opacity: 0 }}
        />
      ))}

      <ServiceNode id="api" title="API" sub="publishes" strain={health.api} />
      <BrokerNode strain={health.broker} deadLetters={health.deadLetters} />
      <ServiceNode id="workers" title="Workers" sub="consume + drive" strain={health.workers} />
      <DatabaseNode strain={health.db} />

      {/* Pulse pool: reused dot elements the loop positions. Rendered last so they sit on top. */}
      {Array.from({ length: POOL_SIZE }, (_, i) => (
        <circle
          key={`dot-${i}`}
          ref={(el) => (dotRefs.current[i] = el)}
          className="pulse-dot"
          r={5}
          style={{ opacity: 0 }}
        />
      ))}
    </svg>
  );
}

/** Which hop a pulse is on right now, and where its dot sits. */
function evalPulse(p: Pulse, now: number) {
  const elapsed = now - p.startedAt;
  const hops = p.hops;
  const unit = HOP_MS + DWELL_MS;
  const total = hops.length * HOP_MS + (hops.length - 1) * DWELL_MS;
  if (elapsed >= total) {
    return { done: true as const, edge: hops[0].edge, t: 1, from: "api" as DiagramNode, to: "api" as DiagramNode, point: { x: 0, y: 0 } };
  }
  let i = Math.floor(elapsed / unit);
  if (i >= hops.length) i = hops.length - 1;
  const local = elapsed - i * unit;
  // During the dwell (local > HOP_MS) t clamps at 1, so the dot rests at the waypoint node.
  const t = Math.min(1, local / HOP_MS);
  const hop = hops[i];
  const ends = EDGE_ENDS[hop.edge];
  const from = hop.reverse ? ends.to : ends.from;
  const to = hop.reverse ? ends.from : ends.to;
  return { done: false as const, edge: hop.edge, t, from, to, point: pointAt(hop.edge, t, hop.reverse) };
}

// ---- Node drawings. Each is a static <g> whose strain tint React sets via data-strain (~5s poll);
// the rAF loop never touches these, only the halos behind them.

function ServiceNode({
  id,
  title,
  sub,
  strain,
}: {
  id: DiagramNode;
  title: string;
  sub: string;
  strain: string;
}) {
  const { x, y } = NODE_C[id];
  return (
    <g className="arch-node" data-strain={strain}>
      <rect className="node-box" x={x - 50} y={y - 32} width={100} height={64} rx={10} />
      <text className="node-title" x={x} y={y - 4} textAnchor="middle">
        {title}
      </text>
      <text className="node-sub" x={x} y={y + 14} textAnchor="middle">
        {sub}
      </text>
    </g>
  );
}

function BrokerNode({ strain, deadLetters }: { strain: string; deadLetters: number }) {
  const { x, y } = NODE_C.broker;
  return (
    <g className="arch-node arch-node-broker" data-strain={strain}>
      <rect className="node-box" x={x - 54} y={y - 32} width={108} height={64} rx={6} />
      <text className="node-title" x={x} y={y - 4} textAnchor="middle">
        Broker
      </text>
      <text className="node-sub" x={x} y={y + 14} textAnchor="middle">
        artifice.events
      </text>
      {deadLetters > 0 && (
        <g className="dead-letter-badge" aria-hidden="true">
          <title>{`${deadLetters} parked message${deadLetters === 1 ? "" : "s"} waiting for a replay`}</title>
          <circle cx={x + 54} cy={y - 32} r={12} />
          <text x={x + 54} y={y - 28} textAnchor="middle">
            {deadLetters > 99 ? "99+" : deadLetters}
          </text>
        </g>
      )}
    </g>
  );
}

function DatabaseNode({ strain }: { strain: string }) {
  const { x, y } = NODE_C.db;
  const rx = 52;
  const top = y - 32;
  const bottom = y + 32;
  return (
    <g className="arch-node" data-strain={strain}>
      <path
        className="node-box"
        d={`M${x - rx} ${top} A${rx} 12 0 0 0 ${x + rx} ${top} L${x + rx} ${bottom} A${rx} 12 0 0 1 ${x - rx} ${bottom} Z`}
      />
      <ellipse className="node-box-rim" cx={x} cy={top} rx={rx} ry={12} />
      <text className="node-title" x={x} y={y - 2} textAnchor="middle">
        Postgres
      </text>
      <text className="node-sub" x={x} y={y + 16} textAnchor="middle">
        orders + stock
      </text>
    </g>
  );
}
