// The API's UTC timestamps come without a zone suffix (System.Text.Json writes DateTime bare), so
// parse them as UTC explicitly rather than letting the browser read them as local time.
function parseUtc(iso: string): Date {
  const hasZone = /[zZ]|[+-]\d{2}:?\d{2}$/.test(iso);
  return new Date(hasZone ? iso : `${iso}Z`);
}

const RELATIVE = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 60 * 60 * 24 * 365],
  ["day", 60 * 60 * 24],
  ["hour", 60 * 60],
  ["minute", 60],
  ["second", 1],
];

/** "just now", "3 minutes ago" — a live board reads better in relative time than in wall clocks. */
export function relativeTime(iso: string, now: number = Date.now()): string {
  const seconds = (parseUtc(iso).getTime() - now) / 1000;
  const abs = Math.abs(seconds);
  if (abs < 5) {
    return "just now";
  }
  for (const [unit, secondsPerUnit] of UNITS) {
    if (abs >= secondsPerUnit || unit === "second") {
      return RELATIVE.format(Math.round(seconds / secondsPerUnit), unit);
    }
  }
  return "just now";
}

/** Full timestamp for a tooltip / the detail view — local time, readable. */
export function absoluteTime(iso: string): string {
  return parseUtc(iso).toLocaleString();
}
