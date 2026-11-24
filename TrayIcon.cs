using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FolderPeek.Native;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FolderPeek;

/// <summary>Ícone da bandeja: pausar, tempo de hover, iniciar com o Windows, sair.</summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly int[] DelayOptionsMs = { 500, 1000, 1500, 2000, 3000 };
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly AppSettings _settings;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _triggerMenu;
    private readonly Forms.ToolStripMenuItem _delayMenu;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Drawing.Icon _activeIcon;
    private readonly Drawing.Icon _pausedIcon;

    /// <summary>Disparado quando pausa/tempo mudam (o App salva e reage).</summary>
    public event Action? SettingsChanged;
    public event Action? ExitRequested;

    public TrayIcon(AppSettings settings)
    {
        _settings = settings;
        _activeIcon = BuildIcon(paused: false);
        _pausedIcon = BuildIcon(paused: true);

        var menu = new Forms.ContextMenuStrip();
        var title = new Forms.ToolStripMenuItem("FolderPeek") { Enabled = false };

        _pauseItem = new Forms.ToolStripMenuItem("Pausar", null, (_, _) => TogglePause());

        _triggerMenu = new Forms.ToolStripMenuItem("Abrir preview com");
        foreach (var (mode, label) in new[]
                 {
                     (TriggerMode.Click, "Clique único na pasta"),
                     (TriggerMode.Hover, "Mouse parado em cima"),
                     (TriggerMode.Both, "Os dois"),
                 })
        {
            var it = new Forms.ToolStripMenuItem(label) { Tag = mode };
            it.Click += (_, _) => { _settings.Trigger = mode; Refresh(); SettingsChanged?.Invoke(); };
            _triggerMenu.DropDownItems.Add(it);
        }

        _delayMenu = new Forms.ToolStripMenuItem("Tempo de hover");
        foreach (int ms in DelayOptionsMs)
        {
            var it = new Forms.ToolStripMenuItem(FormatDelay(ms)) { Tag = ms };
            it.Click += (_, _) => { _settings.HoverDelayMs = ms; Refresh(); SettingsChanged?.Invoke(); };
            _delayMenu.DropDownItems.Add(it);
        }

        _startupItem = new Forms.ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => ToggleStartup());
        var logs = new Forms.ToolStripMenuItem("Abrir pasta de logs", null, (_, _) => OpenLogs());
        var exit = new Forms.ToolStripMenuItem("Sair", null, (_, _) => ExitRequested?.Invoke());

        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            title, new Forms.ToolStripSeparator(),
            _pauseItem, _triggerMenu, _delayMenu, _startupItem,
            new Forms.ToolStripSeparator(), logs, exit,
        });
        menu.Opening += (_, _) => Refresh();

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) TogglePause(); };
        Refresh();
    }

    public void ShowWelcome() =>
        _icon.ShowBalloonTip(5000, "FolderPeek está rodando",
            "Clique uma vez numa pasta da Área de Trabalho (ou pare o mouse em cima) para espiar o conteúdo. " +
            "Clique duplo neste ícone pausa/retoma.", Forms.ToolTipIcon.Info);

    private void TogglePause()
    {
        _settings.Paused = !_settings.Paused;
        Refresh();
        SettingsChanged?.Invoke();
    }

    private void ToggleStartup()
    {
        try { StartupRegistration.Set(!StartupRegistration.IsEnabled); }
        catch (Exception ex) { Log.Write($"startup: {ex.Message}"); }
        Refresh();
    }

    private static void OpenLogs()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.Dir);
            Process.Start(new ProcessStartInfo(Log.Dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"abrir logs: {ex.Message}"); }
    }

    private void Refresh()
    {
        _pauseItem.Checked = _settings.Paused;
        foreach (Forms.ToolStripItem it in _triggerMenu.DropDownItems)
            if (it is Forms.ToolStripMenuItem mi && mi.Tag is TriggerMode m) mi.Checked = m == _settings.Trigger;
        _delayMenu.Enabled = _settings.HoverEnabled;
        foreach (Forms.ToolStripItem it in _delayMenu.DropDownItems)
            if (it is Forms.ToolStripMenuItem mi && mi.Tag is int ms) mi.Checked = ms == _settings.HoverDelayMs;
        _startupItem.Checked = StartupRegistration.IsEnabled;

        _icon.Icon = _settings.Paused ? _pausedIcon : _activeIcon;
        _icon.Text = _settings.Paused
            ? "FolderPeek — pausado"
            : $"FolderPeek — ativo ({FormatDelay(_settings.HoverDelayMs)})";
    }

    private static string FormatDelay(int ms) => (ms / 1000.0).ToString("0.#", PtBr) + " s";

    /// <summary>Ícone de pasta do próprio sistema + selo (azul = ativo, cinza com ‖ = pausado).</summary>
    private static Drawing.Icon BuildIcon(bool paused)
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        using var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
            if (Win32.SHGetStockIconInfo(Win32.SIID_FOLDER, Win32.SHGSI_ICON | Win32.SHGSI_SMALLICON, ref info) == 0
                && info.hIcon != IntPtr.Zero)
            {
                using (var folder = Drawing.Icon.FromHandle(info.hIcon))
                    g.DrawIcon(folder, new Drawing.Rectangle(0, 0, size, size));
                Win32.DestroyIcon(info.hIcon);
            }

            int b = (int)Math.Round(size * 0.58);
            var badge = new Drawing.Rectangle(size - b, size - b, b - 1, b - 1);
            using var fill = new Drawing.SolidBrush(paused ? Drawing.Color.FromArgb(255, 110, 110, 110)
                                                           : Drawing.Color.FromArgb(255, 0, 120, 212));
            g.FillEllipse(fill, badge);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            if (paused)
            {
                float bw = Math.Max(1.5f, b / 7f), bh = b * 0.45f;
                float cy = badge.Top + (badge.Height - bh) / 2f, cx = badge.Left + badge.Width / 2f;
                g.FillRectangle(white, cx - bw * 1.6f, cy, bw, bh);
                g.FillRectangle(white, cx + bw * 0.6f, cy, bw, bh);
            }
            else
            {
                float d = b * 0.36f;
                g.FillEllipse(white, badge.Left + (badge.Width - d) / 2f, badge.Top + (badge.Height - d) / 2f, d, d);
            }
        }

        IntPtr h = bmp.GetHicon();
        try { return (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone(); }
        finally { Win32.DestroyIcon(h); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }
}
