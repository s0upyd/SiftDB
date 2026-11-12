import type { Column } from './bridge';

// Result styling (design section 10): the rules from the user's settings, matched to a result's columns once, then asked
// per cell while drawing. One ordered list, presets included: the first rule that matches styles the cell.

/** Provider-neutral column types (mirrors SiftDB.Abstractions GenericType). */
export type GenericType = 'text' | 'integer' | 'decimal' | 'boolean' | 'date' | 'dateTime';
export const GENERIC_TYPES: { id: GenericType; label: string }[] = [
  { id: 'text', label: 'Text' }, { id: 'integer', label: 'Integer' }, { id: 'decimal', label: 'Decimal' },
  { id: 'boolean', label: 'Boolean' }, { id: 'date', label: 'Date' }, { id: 'dateTime', label: 'Date & time' },
];

export type Condition = 'null' | 'equals' | 'oneOf' | 'range' | 'negative' | 'pattern' | 'past' | 'future';
export const CONDITIONS: { id: Condition; label: string }[] = [
  { id: 'equals', label: 'equals' }, { id: 'oneOf', label: 'is one of' }, { id: 'range', label: 'is between' },
  { id: 'negative', label: 'is a negative number' }, { id: 'past', label: 'is a date in the past' }, { id: 'future', label: 'is a date in the future' },
  { id: 'pattern', label: 'matches the pattern' }, { id: 'null', label: 'is NULL' },
];

/** One rule, as the engine keeps it (SiftDB.Engine StyleRule). */
export interface StyleRule {
  id: string;
  /** Built in: can be switched off and moved, not edited or deleted. */
  preset: boolean;
  name: string;
  enabled: boolean;
  /** Wildcard over the column name (* and ?, case-insensitive). Empty: every column. */
  columns: string;
  /** Only columns of these types. Empty: any type. */
  types: GenericType[];
  condition: Condition;
  /** equals: [value]; oneOf: the list; range: [min, max] with '' for open; pattern: [pattern]. */
  values: string[];
  text: string;
  background: string;
  bold: boolean;
}

export interface CellStyle { text: string; background: string; bold: boolean }
/** The style for one cell's value (null for a database NULL), or null for none. */
export type CellStyler = (value: string | null) => CellStyle | null;

let rules: StyleRule[] = [];
let version = 0;

/** The rules changed (loaded or saved): every grid re-matches its columns on its next draw. */
export function setRules(next: StyleRule[]) { rules = next; version++; }
export const rulesVersion = () => version;

/** `is_*` → /^is_.*$/i. Everything else is literal. */
export function wildcard(pattern: string): RegExp {
  const body = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
  return new RegExp(`^${body}$`, 'i');
}

const NUMBER = /^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$/;
const toNumber = (s: string) => (NUMBER.test(s) ? Number(s) : NaN);

/**
 * An ISO date or date-time (how the grid shows them: 2026-11-12, 2026-11-12 14:05:09.5, 2026-11-12 14:05:09Z) as
 * milliseconds; NaN for anything else. Without a zone it is local time. `endOfDay`: a bare date means its last moment,
 * so "up to 2026-01-31" includes the whole of that day.
 */
const ISO = /^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2})(\.\d+)?)?)?\s*(Z|[+-]\d{2}(?::?\d{2})?)?$/;
export function toTime(s: string, endOfDay = false): number {
  const m = ISO.exec(s.trim());
  if (!m) return NaN;
  const [y, mo, d, h, mi, sec] = [1, 2, 3, 4, 5, 6].map((i) => Number(m[i] ?? 0));
  const ms = m[7] ? Math.round(Number(m[7]) * 1000) : 0;
  if (endOfDay && m[4] === undefined) return new Date(y, mo - 1, d + 1).getTime() - 1;
  if (!m[8]) return new Date(y, mo - 1, d, h, mi, sec, ms).getTime();
  const zone = m[8] === 'Z' ? 0 : (m[8][0] === '-' ? -1 : 1) * (Number(m[8].slice(1, 3)) * 60 + Number(m[8].replace(':', '').slice(3, 5) || 0));
  return Date.UTC(y, mo - 1, d, h, mi, sec, ms) - zone * 60_000;
}

/** Today as the grid writes a date: 2026-11-12, in local time. */
const today = () => { const n = new Date(); return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, '0')}-${String(n.getDate()).padStart(2, '0')}`; };

/**
 * The test a rule's condition makes on a non-NULL value, for a column of type `type`. Past and future compare a Date
 * column by day (today is neither) and a Date & time column with now; both ask the clock per cell, so a result left
 * open stays right. A range compares numbers, or dates when its ends are dates.
 */
function test(rule: StyleRule, type: GenericType): (v: string) => boolean {
  const numeric = type === 'integer' || type === 'decimal';
  const v0 = rule.values[0] ?? '';
  const same = (want: string) => {
    const n = numeric ? toNumber(want) : NaN;
    const lower = want.toLowerCase();
    return Number.isNaN(n) ? (v: string) => v.toLowerCase() === lower : (v: string) => toNumber(v) === n || v.toLowerCase() === lower;
  };
  switch (rule.condition) {
    case 'equals': return same(v0);
    case 'oneOf': { const tests = rule.values.map(same); return (v) => tests.some((t) => t(v)); }
    case 'negative': return (v) => toNumber(v) < 0;
    case 'range': {
      const [low, high] = [rule.values[0] ?? '', rule.values[1] ?? ''];
      if ([low, high].some((b) => b && Number.isNaN(toNumber(b)) && !Number.isNaN(toTime(b)))) {
        const lo = low ? toTime(low) : -Infinity, hi = high ? toTime(high, true) : Infinity;
        return (v) => { const t = toTime(v); return t >= lo && t <= hi; }; // NaN (not a date) never matches
      }
      const lo = low ? toNumber(low) : -Infinity, hi = high ? toNumber(high) : Infinity;
      return (v) => { const n = toNumber(v); return n >= lo && n <= hi; }; // NaN (not a number) never matches
    }
    case 'past':
      return type === 'date' ? (v) => ISO.test(v) && v.slice(0, 10) < today() : (v) => toTime(v) < Date.now();
    case 'future':
      return type === 'date' ? (v) => ISO.test(v) && v.slice(0, 10) > today() : (v) => toTime(v) > Date.now();
    case 'pattern': { const re = wildcard(v0); return (v) => re.test(v); }
    default: return () => false;
  }
}

/** Does this rule apply to this column at all? Decided once per result. */
export function appliesTo(rule: StyleRule, column: Column): boolean {
  if (!rule.enabled) return false;
  if (rule.columns.trim() && !wildcard(rule.columns.trim()).test(column.name)) return false;
  return rule.types.length === 0 || rule.types.includes(column.type ?? 'text');
}

/** Per column, a styler from the rules that apply to it, or null when none do (the common case: no cost to draw). */
export function stylersFor(columns: Column[], list: StyleRule[] = rules): (CellStyler | null)[] {
  return columns.map((column) => {
    const applying = list.filter((r) => appliesTo(r, column)).map((r) => ({
      isNull: r.condition === 'null',
      test: test(r, column.type ?? 'text'),
      style: { text: r.text, background: r.background, bold: r.bold } as CellStyle,
    }));
    if (applying.length === 0) return null;
    return (value) => {
      for (const a of applying) {
        if (value === null ? a.isNull : !a.isNull && a.test(value)) return a.style;
      }
      return null;
    };
  });
}

// ---- readability ------------------------------------------------------------------------------------------------

/** #rgb or #rrggbb as [r, g, b]; null when it is neither. */
export function parseColour(c: string): [number, number, number] | null {
  const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(c.trim());
  if (!m) return null;
  const h = m[1].length === 3 ? [...m[1]].map((x) => x + x).join('') : m[1];
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)) as [number, number, number];
}

/** WCAG contrast ratio between two colours, 1 to 21. Under 4.5 is hard to read for normal text. */
export function contrast(a: string, b: string): number {
  const lum = (c: string) => {
    const rgb = parseColour(c);
    if (!rgb) return 0;
    const [r, g, bl] = rgb.map((v) => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; });
    return 0.2126 * r + 0.7152 * g + 0.0722 * bl;
  };
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}
