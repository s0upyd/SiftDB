// Typed bridge to the host. Commands go up as JSON strings; events come down as JSON;
// row batches arrive in WebView2 shared buffers (zero-copy) and are copied out immediately.

export const enum Kind { Text = 0, Int32 = 1, Int64 = 2, Float64 = 3 }

/** `type` is the column's generic type, for result styling. */
export interface Column { name: string; kind: Kind; type?: import('./styles').GenericType }

// Database structure as sent by the engine's "schema" event (mirrors SiftDB.Abstractions/SchemaModel.cs).
export type RelationKind = 'table' | 'partitionedTable' | 'view' | 'materializedView' | 'foreignTable';
export interface ColumnInfo { name: string; dataType: string; isNullable: boolean; default: string | null }
export interface ForeignKeyInfo {
  name: string; columns: string[];
  referencedSchema: string; referencedRelation: string; referencedColumns: string[];
}
export interface IndexInfo { name: string; columns: string[]; isUnique: boolean }
export interface RelationInfo {
  name: string; kind: RelationKind; columns: ColumnInfo[];
  primaryKey: string[]; foreignKeys: ForeignKeyInfo[]; indexes: IndexInfo[];
}
export interface SchemaInfo { name: string; relations: RelationInfo[] }
export interface DatabaseSchema { schemas: SchemaInfo[] }

export interface BatchMeta {
  queryId: number;
  seq: number;
  startRow: number;
  rowCount: number;
  bufferId: number;
  byteLength: number;
}

export interface BridgeEvent { type: string; replyTo?: number; queryId?: number; [k: string]: any }

export interface Bridge {
  send(cmd: Record<string, unknown>): void;
  request(cmd: Record<string, unknown>): Promise<BridgeEvent>;
}

export function connectBridge(
  onEvent: (e: BridgeEvent) => void,
  onBatch: (meta: BatchMeta, data: ArrayBuffer) => void,
): Bridge {
  const wv = (window as any).chrome?.webview;
  if (!wv) throw new Error('Not running inside the SiftDB host (chrome.webview is missing).');

  let nextId = 1;
  const pending = new Map<number, (e: BridgeEvent) => void>();

  wv.addEventListener('message', (e: MessageEvent) => {
    const ev = e.data as BridgeEvent;
    if (ev.replyTo !== undefined && pending.has(ev.replyTo)) {
      pending.get(ev.replyTo)!(ev);
      pending.delete(ev.replyTo);
    } else {
      onEvent(ev);
    }
  });

  wv.addEventListener('sharedbufferreceived', (e: any) => {
    const meta = e.additionalData as BatchMeta;
    const shared: ArrayBuffer = e.getBuffer();
    const copy = shared.slice(0, meta.byteLength); // own the bytes, then free the shared slot
    wv.releaseBuffer(shared);
    wv.postMessage(JSON.stringify({ type: 'releaseBuffer', bufferId: meta.bufferId }));
    onBatch(meta, copy);
  });

  const send = (cmd: Record<string, unknown>) => wv.postMessage(JSON.stringify(cmd));
  return {
    send,
    request(cmd) {
      const id = nextId++;
      return new Promise((resolve) => {
        pending.set(id, resolve);
        send({ ...cmd, id });
      });
    },
  };
}
