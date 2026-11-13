// Exporting results as CSV or JSON (design section 11, Export): the text for a file, or for the clipboard.
// The formatting is here, the file writing is the engine's (exportBegin / exportWrite / exportEnd).

import { Column, Kind } from './bridge';

export type ExportFormat = 'csv' | 'json';

/** A rectangle of a result to write: columns c0..c1 and rows r0..r1, inclusive. */
export interface ExportSource {
  columns: Column[];
  r0: number; r1: number; c0: number; c1: number;
  /** The cell's text, or null for NULL. */
  cell(row: number, col: number): string | null;
}

/**
 * One CSV field (RFC 4180). A value holding a comma, a quote or a line break is quoted, with its quotes doubled. NULL is
 * an empty field and an empty string is `""`, so the two stay apart for anything that reads the file back.
 */
export function csvField(value: string | null): string {
  if (value === null) return '';
  if (value === '') return '""';
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

const JSON_NUMBER = /^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$/;

/**
 * One value as JSON. Numbers stay numbers, written with the digits the database gave (so a bigint or a numeric keeps every
 * digit, which JSON allows even where JavaScript could not hold it); NaN and Infinity, which JSON has no number for,
 * become strings. Booleans become true / false. Everything else is a string, dates in their ISO form.
 */
export function jsonValue(value: string | null, column: Column): string {
  if (value === null) return 'null';
  const numeric = column.kind !== Kind.Text || column.type === 'integer' || column.type === 'decimal';
  if (numeric && JSON_NUMBER.test(value)) return value;
  if (column.type === 'boolean') {
    if (/^(true|t)$/i.test(value)) return 'true';
    if (/^(false|f)$/i.test(value)) return 'false';
  }
  return JSON.stringify(value);
}

/**
 * Keys for a JSON object per row. Two columns can share a name (`select a.id, b.id`), and a JSON reader keeps only one of
 * two equal keys, so the later ones become `id_2`, `id_3`.
 */
export function jsonKeys(names: string[]): string[] {
  const used = new Set<string>();
  return names.map((name) => {
    let key = name, n = 1;
    while (used.has(key)) key = `${name}_${++n}`;
    used.add(key);
    return key;
  });
}

/**
 * The file's text, in pieces of about `pieceChars` characters, so a large result is sent (and built) a piece at a time.
 * CSV: a header line, then one line per row, each ending in CRLF. JSON: an array with one object per row, one per line.
 */
export function* exportText(format: ExportFormat, src: ExportSource, pieceChars = 1 << 20): Generator<string> {
  const cols = src.columns.slice(src.c0, src.c1 + 1);
  let piece = '';
  if (format === 'csv') {
    piece = cols.map((c) => csvField(c.name)).join(',') + '\r\n';
    for (let r = src.r0; r <= src.r1; r++) {
      let line = '';
      for (let c = src.c0; c <= src.c1; c++) line += (c > src.c0 ? ',' : '') + csvField(src.cell(r, c));
      piece += line + '\r\n';
      if (piece.length >= pieceChars) { yield piece; piece = ''; }
    }
  } else {
    const keys = jsonKeys(cols.map((c) => c.name)).map((k) => JSON.stringify(k) + ': ');
    piece = '[';
    for (let r = src.r0; r <= src.r1; r++) {
      let obj = r > src.r0 ? ',\n  {' : '\n  {';
      for (let c = src.c0; c <= src.c1; c++) {
        obj += (c > src.c0 ? ', ' : '') + keys[c - src.c0] + jsonValue(src.cell(r, c), src.columns[c]);
      }
      piece += obj + '}';
      if (piece.length >= pieceChars) { yield piece; piece = ''; }
    }
    piece += src.r1 >= src.r0 ? '\n]\n' : ']\n';
  }
  if (piece) yield piece;
}

/** The whole text at once: for the clipboard, where the selection is already capped in size. */
export const exportAll = (format: ExportFormat, src: ExportSource) => [...exportText(format, src)].join('');

/**
 * A file name to suggest: the first table the statement reads from (`select * from sales.orders` → `orders`), else
 * "result". Characters Windows does not allow in a file name are dropped.
 */
export function suggestedName(sql: string): string {
  const m = /\b(?:from|into|update|join)\s+((?:"[^"]+"|[\w$]+)(?:\s*\.\s*(?:"[^"]+"|[\w$]+))*)/i.exec(sql);
  const last = m ? m[1].split('.').pop()!.trim().replace(/^"|"$/g, '') : '';
  const clean = last.replace(/[<>:"/\\|?*\x00-\x1f]/g, '').trim();
  return clean || 'result';
}
