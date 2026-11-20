using System.Data.Common;
using System.Diagnostics;
using System.Threading.Channels;
using SiftDB.Bridge;

namespace SiftDB.Engine;

public sealed record EncodedBatch(int Seq, long StartRow, int RowCount, byte[] Data);

/// <summary>
/// One running query. A producer task reads the DbDataReader into encoded batches and writes them to a
/// small bounded channel; when the consumer (the UI) stops fetching, the channel fills and reading pauses.
/// </summary>
internal sealed class QuerySession
{
    private const int FirstBatchRows = 2_000;
    private const int BatchRows = 20_000;
    private const int BatchBytes = 1 << 20;
    private const long MaxBatchAgeMs = 100;

    private readonly DbDataReader _reader;
    private readonly RowBatchWriter _writer;
    private readonly CancellationTokenSource _cts;
    private readonly Stopwatch _sw;
    private long _firstBatchTicks = -1;
    private long _readTicks, _appendTicks, _writeTicks;
    private readonly Func<long, bool, Task>? _onFinished;
    private bool _failed;

    public int Id { get; }

    public Channel<EncodedBatch> Channel { get; } =
        System.Threading.Channels.Channel.CreateBounded<EncodedBatch>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    public long TotalRows { get; private set; }

    /// <summary>Rows an INSERT/UPDATE/DELETE reported, or a negative number for a statement that has no such count.</summary>
    public long RecordsAffected { get; private set; } = -1;
    public double FirstBatchMs => _firstBatchTicks < 0 ? -1 : _firstBatchTicks * 1000.0 / Stopwatch.Frequency;
    public double TotalMs { get; private set; }

    /// <summary>Where the producer spent its time: reading from the provider, encoding, waiting on the full channel.</summary>
    public (double ReadMs, double AppendMs, double WriteWaitMs) Breakdown =>
        (ToMs(_readTicks), ToMs(_appendTicks), ToMs(_writeTicks));

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <param name="onFinished">Called once the reader is closed, whether the query finished, failed or was cancelled: the
    /// session is idle again, so this is where the transaction state is read and the session lock released. It is told the
    /// rows affected and whether the statement failed (or was cancelled) part way.</param>
    /// <param name="maxRows">Rows sent to the UI at most (0: all). The rest are still read, so the statement runs to the end
    /// and <see cref="RecordsAffected"/> counts them all, but are not encoded or sent (a dry run shows the first ones).</param>
    public QuerySession(int id, DbDataReader reader, RowBatchWriter writer, CancellationTokenSource cts, Stopwatch sw,
        Func<long, bool, Task>? onFinished = null, int maxRows = 0)
    {
        Id = id; _reader = reader; _writer = writer; _cts = cts; _sw = sw; _onFinished = onFinished; _maxRows = maxRows;
    }

    private readonly int _maxRows;

    public void Cancel() => _cts.Cancel();

    private volatile bool _readingFromServer = true;

    /// <summary>
    /// The statement is waiting on the database (for its next rows, or for a lock), not on the UI to take the rows already
    /// read. False once it has finished. Who's blocking me (design section 14, F3) looks for blockers only while this holds.
    /// </summary>
    public bool ReadingFromServer => _readingFromServer;

    public bool IsCancelled(Exception ex) => _cts.IsCancellationRequested || ex is OperationCanceledException;

    public void Start() => _ = Task.Run(ProduceAsync);

    private async Task ProduceAsync()
    {
        var ct = _cts.Token;
        try
        {
            var seq = 0;
            long start = 0;
            var lastFlush = _sw.ElapsedMilliseconds;

            async Task FlushAsync()
            {
                var rows = _writer.RowCount;
                var data = _writer.Flush();
                if (_firstBatchTicks < 0) _firstBatchTicks = _sw.ElapsedTicks;
                var w0 = Stopwatch.GetTimestamp();
                _readingFromServer = false; // waiting for the UI to take rows, if the channel is full
                await Channel.Writer.WriteAsync(new EncodedBatch(seq++, start, rows, data), ct);
                _readingFromServer = true;
                _writeTicks += Stopwatch.GetTimestamp() - w0;
                start += rows;
                TotalRows = start;
                lastFlush = _sw.ElapsedMilliseconds;
            }

            while (true)
            {
                var t0 = Stopwatch.GetTimestamp();
                var more = await _reader.ReadAsync(ct);
                var t1 = Stopwatch.GetTimestamp();
                _readTicks += t1 - t0;
                if (!more) break;
                if (_maxRows > 0 && start + _writer.RowCount >= _maxRows) continue; // past the cap: read, not kept
                _writer.AppendRow(_reader);
                _appendTicks += Stopwatch.GetTimestamp() - t1;
                var limit = seq == 0 ? FirstBatchRows : BatchRows;
                if (_writer.RowCount >= limit || _writer.ApproxBytes >= BatchBytes
                    || _sw.ElapsedMilliseconds - lastFlush >= MaxBatchAgeMs)
                    await FlushAsync();
            }
            if (_writer.RowCount > 0) await FlushAsync();
            RecordsAffected = _reader.RecordsAffected;
            TotalMs = _sw.Elapsed.TotalMilliseconds;
            Channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _failed = true;
            Channel.Writer.TryComplete(ex);
        }
        finally
        {
            _readingFromServer = false;
            await _reader.DisposeAsync();
            if (_onFinished is not null)
            {
                try { await _onFinished(RecordsAffected, _failed); }
                catch { /* the callback reports its own failures; the session must still be released */ }
            }
        }
    }
}
