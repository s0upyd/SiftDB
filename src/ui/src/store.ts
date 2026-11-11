import { Kind } from './bridge';

interface ColData {
  kind: Kind;
  i32?: Int32Array;
  i64?: BigInt64Array;
  f64?: Float64Array;
  offs?: Int32Array;
  bytes?: Uint8Array;
  /** One bit per row, set = NULL. Absent when the column has no NULLs in this batch. */
  nulls?: Uint8Array;
}

export interface DecodedBatch {
  startRow: number;
  rowCount: number;
  cols: ColData[];
}

const MAGIC = 0x42544653; // "SFTB"
const TABLE_ENTRY_BYTES = 20; // kind, offset, length, nullOffset, nullLength
const utf8 = new TextDecoder();

/** Typed-array views over the copied buffer; no per-cell parsing happens until a cell is drawn. */
export function decodeBatch(buf: ArrayBuffer, startRow: number): DecodedBatch {
  const dv = new DataView(buf);
  if (dv.getUint32(0, true) !== MAGIC) throw new Error('Bad batch magic');
  const n = dv.getUint32(4, true);
  const colCount = dv.getUint32(8, true);
  const cols: ColData[] = [];
  for (let c = 0; c < colCount; c++) {
    const e = 12 + c * TABLE_ENTRY_BYTES;
    const kind = dv.getUint32(e, true) as Kind;
    const off = dv.getUint32(e + 4, true);
    const len = dv.getUint32(e + 8, true);
    const nullOff = dv.getUint32(e + 12, true);
    const nullLen = dv.getUint32(e + 16, true);
    const nulls = nullLen > 0 ? new Uint8Array(buf, nullOff, nullLen) : undefined;
    switch (kind) {
      case Kind.Int32: cols.push({ kind, nulls, i32: new Int32Array(buf, off, n) }); break;
      case Kind.Int64: cols.push({ kind, nulls, i64: new BigInt64Array(buf, off, n) }); break;
      case Kind.Float64: cols.push({ kind, nulls, f64: new Float64Array(buf, off, n) }); break;
      default: {
        const tableBytes = (n + 1) * 4;
        cols.push({
          kind,
          nulls,
          offs: new Int32Array(buf, off, n + 1),
          bytes: new Uint8Array(buf, off + tableBytes, len - tableBytes),
        });
      }
    }
  }
  return { startRow, rowCount: n, cols };
}

/** The cell as text, or null for a database NULL (which is not the same as an empty string or 0). */
export function cellText(b: DecodedBatch, col: number, row: number): string | null {
  const c = b.cols[col];
  if (c.nulls && (c.nulls[row >> 3] & (1 << (row & 7))) !== 0) return null;
  switch (c.kind) {
    case Kind.Int32: return String(c.i32![row]);
    case Kind.Int64: return c.i64![row].toString();
    case Kind.Float64: return String(c.f64![row]);
    default: return utf8.decode(c.bytes!.subarray(c.offs![row], c.offs![row + 1]));
  }
}

/** Batches arrive in any order; rows become visible once the contiguous prefix reaches them. */
export class ResultStore {
  private ordered: DecodedBatch[] = [];
  private pending = new Map<number, DecodedBatch>();
  private nextSeq = 0;
  loadedRows = 0;

  add(seq: number, batch: DecodedBatch) {
    this.pending.set(seq, batch);
    for (let b = this.pending.get(this.nextSeq); b; b = this.pending.get(this.nextSeq)) {
      this.pending.delete(this.nextSeq++);
      this.ordered.push(b);
      this.loadedRows = b.startRow + b.rowCount;
    }
  }

  /** Batch containing `row`, or undefined if it hasn't loaded yet. */
  batchFor(row: number): DecodedBatch | undefined {
    let lo = 0, hi = this.ordered.length - 1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      const b = this.ordered[mid];
      if (row < b.startRow) hi = mid - 1;
      else if (row >= b.startRow + b.rowCount) lo = mid + 1;
      else return b;
    }
    return undefined;
  }
}
