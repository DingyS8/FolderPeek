using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FolderPeek.Native;

namespace FolderPeek;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private TrayIcon? _tray;
    private HoverMonitor? _monitor;
    private MouseClickHook? _clickHook;
    private PreviewWindow? _preview;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, a) => { Log.Write($"UI: {a.Exception}"); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write($"fatal: {a.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Write($"task: {a.Exception}"); a.SetObserved(); };

        // Ferramentas de diagnóstico (não precisam de mouse):
        //   FolderPeek.exe --diag              → casa todos os ícones do desktop, grava diag.txt
        //   FolderPeek.exe --preview <pasta>   → mostra o popup no cursor por 15 s
        if (e.Args.Contains("--diag")) { RunDiag(); return; }
        int ti = Array.IndexOf(e.Args, "--thumb"); // --thumb <arquivo> <saida.png>
        if (ti >= 0 && ti + 2 < e.Args.Length)
        {
            string r;
            int px = ti + 3 < e.Args.Length && int.TryParse(e.Args[ti + 3], out var p) ? p : 96;
            try { r = ShellImageLoader.DumpToPng(e.Args[ti + 1], px, e.Args[ti + 2]); } catch (Exception ex) { r = ex.ToString(); }
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Path.Combine(Log.Dir, "thumb.txt"), r);
            Shutdown();
            return;
        }

        int pi = Array.IndexOf(e.Args, "--preview");
        if (pi >= 0 && pi + 1 < e.Args.Length) { RunPreviewDemo(e.Args[pi + 1]); return; }

        Log.Verbose = e.Args.Contains("--verbose");

        _mutex = new Mutex(true, @"Local\FolderPeek.SingleInstance", out _ownsMutex);
        if (!_ownsMutex) { Shutdown(); return; }

        System.Windows.Forms.Application.EnableVisualStyles();

        _settings = AppSettings.Load();
        StartupRegistration.RefreshPathIfEnabled();

        _preview = new PreviewWindow { MaxItems = _settings.MaxItems };
        _monitor = new HoverMonitor(_preview, _settings);

        _tray = new TrayIcon(_settings);
        _tray.SettingsChanged += () =>
        {
            _settings.Save();
            if (_settings.Paused) _preview.CloseAnimated();
            ApplyClickHook();
        };
        _tray.ExitRequested += Shutdown;

        _monitor.Start();
        ApplyClickHook();
        Task.Run(DesktopItemResolver.WarmUp);

        if (!_settings.WelcomeShown)
        {
            _tray.ShowWelcome();
            _settings.WelcomeShown = true;
            _settings.Save();
        }
        Log.Write($"iniciado (hover {_settings.HoverDelayMs} ms, pausado={_settings.Paused})");
    }

    /// <summary>Hook de clique só existe enquanto o modo de clique estiver ligado.</summary>
    private void ApplyClickHook()
    {
        bool want = _settings.ClickEnabled && !_settings.Paused;
        if (want && _clickHook == null)
        {
            _clickHook = new MouseClickHook(Dispatcher);
            _clickHook.LeftButton += (down, pt) => _monitor?.OnLeftButton(down, pt);
        }
        else if (!want && _clickHook != null)
        {
            _clickHook.Dispose();
            _clickHook = null;
        }
    }

    private void RunDiag()
    {
        string report;
        try
        {
            report = Task.Run(() =>
            {
                var sb = new System.Text.StringBuilder(DesktopItemResolver.Diagnose());
                // --diag também resolve quaisquer .lnk passados depois do flag
                foreach (var lnk in Environment.GetCommandLineArgs().Where(a => a.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
                    sb.AppendLine($"lnk {lnk} -> {DesktopItemResolver.ResolveShortcutFolder(lnk) ?? "(não é pasta)"}");
                return sb.ToString();
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) { report = ex.ToString(); }
        Directory.CreateDirectory(Log.Dir);
        File.WriteAllText(Path.Combine(Log.Dir, "diag.txt"), report);
        Shutdown();
    }

    private void RunPreviewDemo(string folder)
    {
        Win32.GetCursorPos(out var pt);
        var rect = new RECT(pt.X - 40, pt.Y - 40, pt.X + 40, pt.Y + 50);
        var preview = new PreviewWindow { MaxItems = AppSettings.Load().MaxItems };
        preview.ShowFor(new DesktopHit(folder, folder, Path.GetFileName(folder.TrimEnd('\\')), rect, false));
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        t.Tick += (_, _) => Shutdown();
        t.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitor?.Stop();
        _clickHook?.Dispose();
        _tray?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
