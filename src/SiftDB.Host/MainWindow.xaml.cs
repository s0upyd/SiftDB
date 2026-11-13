using System.Diagnostics;
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
        // Saved connections and their DPAPI-encrypted passwords live in %APPDATA%\SiftDB, in separate files.
        var dir = ConnectionStore.DefaultDirectory;
        var store = new ConnectionStore(Path.Combine(dir, "connections.json"), new DpapiSecretStore(Path.Combine(dir, "secrets.json")));
        _engine = new EngineHost([new PostgresProvider()], this, store, new SessionStore(SessionStore.DefaultDirectory),
            new SettingsStore(SettingsStore.DefaultPath));
        SourceInitialized += (_, _) => ApplyTitleBarTheme(IsDarkTheme());
        Loaded += async (_, _) => await InitWebAsync();
        Closing += OnClosing;
        Closed += async (_, _) => await _engine.DisposeAsync();
    }

    /// <summary>The user has agreed to close (or there was nothing to warn about); a second Closing must not ask again.</summary>
    private bool _closeConfirmed;

    /// <summary>The page has written its last autosave, so the window may close.</summary>
    private bool _flushed;

    private System.Windows.Threading.DispatcherTimer? _flushTimeout;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeConfirmed && _unsavedChanges)
        {
            var answer = MessageBox.Show(this,
                "A transaction has changes that have not been committed. Closing now rolls them back.\n\nClose anyway?",
                "SiftDB", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; _updateOnClose = false; return; }
        }
        _closeConfirmed = true;

        // Edits made in the last moment before closing may not be autosaved yet (autosave waits for a pause in typing).
        // Ask the page to save now and close when it says it has, or after a short wait if it cannot answer.
        if (_flushed || Web.CoreWebView2 is null)
        {
            // Closing for certain now. The updater waits for this process to exit, then applies the update and restarts.
            if (_updateOnClose) _updater.ApplyAfterExit();
            return;
        }
        e.Cancel = true;
        if (_flushTimeout is not null) return; // already waiting
        _flushTimeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _flushTimeout.Tick += (_, _) => FinishClose();
        _flushTimeout.Start();
        SendEvent("""{"type":"flushBeforeClose"}""");
    }

    private void FinishClose()
    {
        _flushTimeout?.Stop();
        if (_flushed) return;
        _flushed = true;
        Close();
    }

    /// <summary>The native Open and Save As dialogs (design section 6). The page asks; the path, or null, goes back to it.</summary>
    private void FileDialog(JsonElement request)
    {
        var replyTo = request.GetProperty("id").GetInt32();
        string? StringOf(string name) =>
            request.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        const string filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*";
        // Exporting a result: the file type list is the format choice ("format" preselects one; the answer says which).
        string[] exportFormats = ["csv", "json"];
        const string exportFilter = "CSV, comma-separated (*.csv)|*.csv|JSON (*.json)|*.json";
        var current = StringOf("currentPath");
        var export = StringOf("purpose") == "export";
        var exportIndex = Math.Max(0, Array.IndexOf(exportFormats, StringOf("format") ?? "csv"));
        FileDialog dialog = request.GetProperty("type").GetString() == "openFileDialog"
            ? new OpenFileDialog { Filter = filter, Title = "Open a SQL file" }
            : export
                ? new SaveFileDialog
                {
                    Filter = exportFilter, FilterIndex = exportIndex + 1, Title = "Export the result",
                    DefaultExt = "." + exportFormats[exportIndex], AddExtension = true, OverwritePrompt = true,
                    FileName = StringOf("suggestedName") ?? "result",
                }
                : new SaveFileDialog
                {
                    Filter = filter, Title = "Save the sheet", DefaultExt = ".sql", AddExtension = true, OverwritePrompt = true,
                    FileName = StringOf("suggestedName") ?? "Sheet.sql",
                };
        if (current is not null && Path.GetDirectoryName(current) is { } dir && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        var path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        var format = export && path is not null ? exportFormats[Math.Clamp(dialog.FilterIndex - 1, 0, exportFormats.Length - 1)] : null;
        SendEvent(JsonSerializer.Serialize(new { type = "fileDialog", replyTo, path, format }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    /// <summary>
    /// WebView2's own data (the page's localStorage, its cache). By default it sits next to the exe, which an update
    /// replaces, so it lives in the user's local app data instead.
    /// </summary>
    private static string WebViewDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SiftDB", "WebView2");

    private async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewDataDirectory);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // The installer installs it (design section 9), so this means a copy run without installing, or one removed since.
            var answer = MessageBox.Show(this,
                "SiftDB needs the Microsoft Edge WebView2 Runtime, which is not installed on this computer.\n\n" +
                "Open Microsoft's download page now? Start SiftDB again once it is installed.",
                "SiftDB", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            _closeConfirmed = _flushed = true;
            Close();
            return;
        }
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
        core.NavigationCompleted += (_, _) =>
        {
            SendEvent(JsonSerializer.Serialize(new
            {
                type = "init",
                dark = IsDarkTheme(),
                version = _updater.CurrentVersion,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            StartUpdateChecks();
        };
        core.Navigate("https://siftdb.local/index.html");
    }

    // ---- updates (design section 9) ----------------------------------------------------------------------------------

    private readonly Updater _updater = new();
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    /// <summary>The user chose to restart into a downloaded update; the updater starts once the window is certainly closing.</summary>
    private bool _updateOnClose;

    /// <summary>A first check shortly after start (so it never slows startup), then every few hours for a window left open.</summary>
    private void StartUpdateChecks()
    {
        if (!_updater.IsInstalled || _updateTimer is not null) return;
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(4);
            if (await _updater.CheckAndDownloadAsync() is { } version)
                SendEvent(JsonSerializer.Serialize(new { type = "updateReady", version }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        };
        _updateTimer.Start();
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
            case "openFileDialog":
            case "saveFileDialog":
                FileDialog(doc.RootElement);
                return;
            case "flushed": // the page's last autosave is written: close now
                if (_closeConfirmed) FinishClose();
                return;
            case "applyUpdate": // restart into the downloaded update, through the usual close (warning, last autosave)
                _updateOnClose = true;
                Close();
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
