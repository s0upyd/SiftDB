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

    public int Id { get; }

    public Channel<EncodedBatch> Channel { get; } =
        System.Threading.Channels.Channel.CreateBounded<EncodedBatch>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    public long TotalRows { get; private set; }
    public double FirstBatchMs => _firstBatchTicks < 0 ? -1 : _firstBatchTicks * 1000.0 / Stopwatch.Frequency;
    public double TotalMs { get; private set; }

    public QuerySession(int id, DbDataReader reader, RowBatchWriter writer, CancellationTokenSource cts, Stopwatch sw)
    {
        Id = id; _reader = reader; _writer = writer; _cts = cts; _sw = sw;
    }

    public void Cancel() => _cts.Cancel();

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
                await Channel.Writer.WriteAsync(new EncodedBatch(seq++, start, rows, data), ct);
                start += rows;
                TotalRows = start;
                lastFlush = _sw.ElapsedMilliseconds;
            }

            while (await _reader.ReadAsync(ct))
            {
                _writer.AppendRow(_reader);
                var limit = seq == 0 ? FirstBatchRows : BatchRows;
                if (_writer.RowCount >= limit || _writer.ApproxBytes >= BatchBytes
                    || _sw.ElapsedMilliseconds - lastFlush >= MaxBatchAgeMs)
                    await FlushAsync();
            }
            if (_writer.RowCount > 0) await FlushAsync();
            TotalMs = _sw.Elapsed.TotalMilliseconds;
            Channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            Channel.Writer.TryComplete(ex);
        }
        finally
        {
            await _reader.DisposeAsync();
        }
    }
}
