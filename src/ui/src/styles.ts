import type { Column } from './bridge';

// Result styling (design section 10): the rules from the user's settings, matched to a result's columns once, then asked
// per cell while drawing. One ordered list, presets included: the first rule that matches styles the cell.

/** Provider-neutral column types (mirrors SiftDB.Abstractions GenericType). */
export type GenericType = 'text' | 'integer' | 'decimal' | 'boolean' | 'date' | 'dateTime';
export const GENERIC_TYPES: { id: GenericType; label: string }[] = [
  { id: 'text', label: 'Text' }, { id: 'integer', label: 'Integer' }, { id: 'decimal', label: 'Decimal' },
  { id: 'boolean', label: 'Boolean' }, { id: 'date', label: 'Date' }, { id: 'dateTime', label: 'Date & time' },
];

export type Condition = 'null' | 'equals' | 'oneOf' | 'range' | 'negative' | 'pattern';
export const CONDITIONS: { id: Condition; label: string }[] = [
  { id: 'equals', label: 'equals' }, { id: 'oneOf', label: 'is one of' }, { id: 'range', label: 'is a number between' },
  { id: 'negative', label: 'is a negative number' }, { id: 'pattern', label: 'matches the pattern' }, { id: 'null', label: 'is NULL' },
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

/** The test a rule's condition makes on a non-NULL value. `numeric`: the column is Integer or Decimal. */
function test(rule: StyleRule, numeric: boolean): (v: string) => boolean {
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
      const lo = rule.values[0] ? toNumber(rule.values[0]) : -Infinity, hi = rule.values[1] ? toNumber(rule.values[1]) : Infinity;
      return (v) => { const n = toNumber(v); return n >= lo && n <= hi; }; // NaN (not a number) never matches
    }
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
    const numeric = column.type === 'integer' || column.type === 'decimal';
    const applying = list.filter((r) => appliesTo(r, column)).map((r) => ({
      isNull: r.condition === 'null',
      test: test(r, numeric),
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
