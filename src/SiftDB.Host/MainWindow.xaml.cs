using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SiftDB.Engine;
using SiftDB.Providers.Postgres;

namespace SiftDB.Host;

/// <summary>
/// A bare window with one full-size WebView2. No UI logic: it moves messages between the page and the engine.
/// Binary row batches travel in WebView2 shared buffers (zero-copy); everything else is JSON.
/// </summary>
public partial class MainWindow : Window, IEngineOutput
{
    private readonly EngineHost _engine;
    private readonly Dictionary<int, CoreWebView2SharedBuffer> _inFlight = new();
    private int _nextBufferId;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new EngineHost([new PostgresProvider()], this);
        Loaded += async (_, _) => await InitWebAsync();
        Closed += async (_, _) => await _engine.DisposeAsync();
    }

    private async Task InitWebAsync()
    {
        await Web.EnsureCoreWebView2Async();
        var core = Web.CoreWebView2;

        var uiDir = Path.Combine(AppContext.BaseDirectory, "ui");
        if (!File.Exists(Path.Combine(uiDir, "index.html")))
        {
            MessageBox.Show($"UI bundle not found in {uiDir}. Run 'npm run build' in src/ui, then rebuild the host.");
            return;
        }
        core.SetVirtualHostNameToFolderMapping("siftdb.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) => SendEvent(JsonSerializer.Serialize(new
        {
            type = "init",
            dark = IsDarkTheme(),
            defaultConnection = Environment.GetEnvironmentVariable("SIFTDB_SPIKE_CONN") ?? "",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        core.Navigate("https://siftdb.local/index.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        using var doc = JsonDocument.Parse(json);
        // Transport concern, not engine business: the page is done with a shared buffer.
        if (doc.RootElement.GetProperty("type").GetString() == "releaseBuffer")
        {
            var id = doc.RootElement.GetProperty("bufferId").GetInt32();
            if (_inFlight.Remove(id, out var buf)) buf.Dispose();
            return;
        }
        _ = _engine.HandleCommandAsync(json);
    }

    // IEngineOutput: called from engine threads, so hop to the UI thread before touching WebView2.
    public void SendEvent(string json) =>
        Dispatcher.BeginInvoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));

    public void SendBatch(int queryId, EncodedBatch batch) =>
        Dispatcher.BeginInvoke(() =>
        {
            var core = Web.CoreWebView2;
            if (core is null) return;
            var buf = core.Environment.CreateSharedBuffer((ulong)batch.Data.Length);
            using (var s = buf.OpenStream()) s.Write(batch.Data);
            var bufferId = ++_nextBufferId;
            _inFlight[bufferId] = buf;
            var meta = JsonSerializer.Serialize(new
            {
                queryId, seq = batch.Seq, startRow = batch.StartRow, rowCount = batch.RowCount,
                bufferId, byteLength = batch.Data.Length,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            core.PostSharedBufferToScript(buf, CoreWebView2SharedBufferAccess.ReadOnly, meta);
        });

    private static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
