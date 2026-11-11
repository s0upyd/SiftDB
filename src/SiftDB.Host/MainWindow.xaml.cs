using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
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

    /// <summary>The page says a transaction holds changes that closing the window would roll back.</summary>
    private bool _unsavedChanges;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new EngineHost([new PostgresProvider()], this);
        SourceInitialized += (_, _) => ApplyTitleBarTheme(IsDarkTheme());
        Loaded += async (_, _) => await InitWebAsync();
        Closing += OnClosing;
        Closed += async (_, _) => await _engine.DisposeAsync();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_unsavedChanges) return;
        var answer = MessageBox.Show(this,
            "A transaction has changes that have not been committed. Closing now rolls them back.\n\nClose anyway?",
            "SiftDB", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
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
        // The page owns its shortcuts (F5 runs a statement, Ctrl+Shift+R rolls back); the browser's must not reload it.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
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
        // Transport and window concerns, not engine business.
        switch (doc.RootElement.GetProperty("type").GetString())
        {
            case "releaseBuffer": // the page is done with a shared buffer
                var id = doc.RootElement.GetProperty("bufferId").GetInt32();
                if (_inFlight.Remove(id, out var buf)) buf.Dispose();
                return;
            case "hostState": // for the close-window warning
                _unsavedChanges = doc.RootElement.GetProperty("unsavedChanges").GetBoolean();
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

    // The title bar belongs to Windows, not to the page, so it has to be asked to match. 20 is the documented attribute
    // (Windows 10 20H1 and later); 19 is the same switch on early Windows 10 builds. Failure just leaves a light title bar.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyTitleBarTheme(bool dark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        // Until the page has loaded, WebView2 shows white; match it so a dark window does not flash.
        Web.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(13, 17, 23) : System.Drawing.Color.White;
    }

    private static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
