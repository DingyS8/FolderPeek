using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using FolderPeek.Native;

namespace FolderPeek;

/// <summary>
/// Máquina de estados do hover. Faz polling leve do cursor (50 ms) em vez de
/// hook global de mouse (WH_MOUSE_LL): um hook atrasa o mouse do sistema inteiro
/// se a nossa thread travar; polling custa ~0% de CPU e nunca interfere em ninguém.
///
/// Fora do desktop o tick só faz GetCursorPos + WindowFromPoint + GetClassName.
/// UIA/COM só rodam uma vez por "parada" de 1,5 s, numa thread de background.
/// </summary>
internal sealed class HoverMonitor
{
    private const int TickMs = 50;
    private const int CloseGraceMs = 300;   // tempo fora da área antes de fechar
    private const int ItemMarginPx = 6;     // tolerância ao redor do ícone
    private const int PreviewMarginPx = 16; // tolerância ao redor do popup (a sombra é do DWM, fora do rect)

    private readonly DispatcherTimer _timer;
    private readonly PreviewWindow _preview;
    private readonly AppSettings _settings;
    private readonly int _moveTolerancePx;

    private POINT _anchor;
    private long _anchorSince;   // 0 = sem âncora
    private bool _armed;         // pode disparar nesta parada?
    private bool _resolving;
    private long _outsideSince;
    private RECT _itemRect;
    private IntPtr _lastUnder;

    public HoverMonitor(PreviewWindow preview, AppSettings settings)
    {
        _preview = preview;
        _settings = settings;
        // "parado" = não sair de um raio de ~10 px (escalado pelo DPI do sistema)
        _moveTolerancePx = (int)Math.Round(10 * Win32.GetDpiForSystem() / 96.0);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        if (!Win32.GetCursorPos(out var pt)) return;
        long now = Environment.TickCount64;

        IntPtr under = Win32.WindowFromPoint(pt);
        bool overDesktop = DesktopItemResolver.IsDesktopListView(under);
        if (Log.Verbose && under != _lastUnder)
        {
            _lastUnder = under;
            var p1 = Win32.GetAncestor(under, Win32.GA_PARENT);
            var p2 = Win32.GetAncestor(p1, Win32.GA_PARENT);
            Log.Debug($"sob o cursor {pt}: {Win32.ClassOf(under)} < {Win32.ClassOf(p1)} < {Win32.ClassOf(p2)} desktop={overDesktop}");
        }
        bool overPreview = _preview.Handle != IntPtr.Zero && Win32.GetAncestor(under, Win32.GA_ROOT) == _preview.Handle;

        if (_preview.IsOpen) KeepAliveOrClose(pt, now, overDesktop, overPreview);

        if (_settings.Paused || !_settings.HoverEnabled || !overDesktop) { _anchorSince = 0; return; }

        // Clique/arraste no desktop = intenção diferente de "espiar". Só rearma depois de mover.
        if (Win32.IsButtonDown()) { _anchor = pt; _anchorSince = now; _armed = false; return; }

        if (_anchorSince == 0 || Distance(pt, _anchor) > _moveTolerancePx)
        {
            _anchor = pt; _anchorSince = now; _armed = true;   // mexeu: reinicia o timer
            return;
        }

        if (!_armed || _resolving) return;
        if (_preview.IsOpen && _itemRect.Contains(pt)) { _armed = false; return; } // já mostrando este

        if (now - _anchorSince >= _settings.HoverDelayMs)
        {
            _armed = false;
            Log.Debug($"parado {now - _anchorSince} ms em {pt} → resolvendo");
            _ = ResolveAndShowAsync(pt, under);
        }
    }

    // ------------------------------------------------------------------ gatilho por clique

    private POINT _downPt;
    private long _downTick;
    private bool _downOnDesktop;
    private bool _downIsDouble;

    /// <summary>
    /// Clique único (sem arrastar, sem Ctrl/Shift) num ícone de pasta → abre o preview na hora.
    /// Clique duplo continua abrindo a pasta normalmente: o 2º clique fecha o preview.
    /// Clicar de novo no mesmo ícone com o preview aberto fecha (alterna).
    /// </summary>
    public void OnLeftButton(bool down, POINT pt)
    {
        if (_settings.Paused || !_settings.ClickEnabled) return;
        long now = Environment.TickCount64;

        if (down)
        {
            IntPtr under = Win32.WindowFromPoint(pt);
            _downOnDesktop = DesktopItemResolver.IsDesktopListView(under);
            _downIsDouble = now - _downTick <= Win32.GetDoubleClickTime() && Distance(pt, _downPt) <= _moveTolerancePx;
            _downPt = pt;
            _downTick = now;
            if (_downIsDouble && _preview.IsOpen && _itemRect.Contains(pt)) _preview.CloseAnimated();
            return;
        }

        if (!_downOnDesktop || _downIsDouble) return;
        _downOnDesktop = false;

        if (Distance(pt, _downPt) > _moveTolerancePx) return;            // arrastou ícone / seleção
        if (now - _downTick > 1000) return;                                // pressionou e segurou
        if (((ushort)Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0
            || ((ushort)Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0) return; // multisseleção

        if (_preview.IsOpen && _itemRect.Contains(pt)) { Log.Debug("clique no mesmo item → fecha"); _preview.CloseAnimated(); return; }
        if (_resolving) return;

        Log.Debug($"clique em {pt} → resolvendo");
        _armed = false; // o hover não dispara de novo nesta mesma parada
        _ = ResolveAndShowAsync(pt, Win32.WindowFromPoint(pt));
    }

    private async Task ResolveAndShowAsync(POINT pt, IntPtr listView)
    {
        _resolving = true;
        try
        {
            var started = Environment.TickCount64;
            var hit = await Task.Run(() => DesktopItemResolver.Resolve(pt, listView));
            var ms = Environment.TickCount64 - started;
            if (ms > 400) Log.Write($"resolução lenta: {ms} ms");
            Log.Debug($"resolvido em {ms} ms: {(hit == null ? "nada (não é pasta / sem item)" : $"{hit.FolderPath} rect={hit.ItemRect}")}");

            if (hit == null || _settings.Paused) return;

            // O usuário pode ter saído durante a resolução.
            if (!Win32.GetCursorPos(out var cur) || !hit.ItemRect.Inflate(4).Contains(cur)) { Log.Debug($"cursor saiu do item ({cur})"); return; }
            if (!DesktopItemResolver.IsDesktopListView(Win32.WindowFromPoint(cur))) { Log.Debug("cursor não está mais no desktop"); return; }
            if (_preview.IsOpen && string.Equals(_preview.Current?.ItemPath, hit.ItemPath, StringComparison.OrdinalIgnoreCase)) return;
            Log.Debug("mostrando preview");

            _itemRect = hit.ItemRect;
            _outsideSince = 0;
            _preview.ShowFor(hit);
        }
        catch (Exception ex) { Log.Write($"resolve: {ex}"); }
        finally { _resolving = false; }
    }

    /// <summary>
    /// Mantém aberto enquanto o cursor estiver: no popup; ou, sobre o desktop,
    /// no ícone de origem, no "corredor" entre ícone e popup, ou na margem do popup.
    /// Fora disso por mais de <see cref="CloseGraceMs"/> → fecha com fade.
    /// </summary>
    private void KeepAliveOrClose(POINT pt, long now, bool overDesktop, bool overPreview)
    {
        var pr = _preview.ScreenRect;
        bool keep = overPreview
                    || Mouse.Captured != null // arrastando a barra de rolagem
                    || (overDesktop && (_itemRect.Inflate(ItemMarginPx).Contains(pt)
                                        || Bridge(_itemRect, pr).Contains(pt)
                                        || pr.Inflate(PreviewMarginPx).Contains(pt)));
        if (keep) { _outsideSince = 0; return; }

        if (_outsideSince == 0) _outsideSince = now;
        else if (now - _outsideSince >= CloseGraceMs)
        {
            _outsideSince = 0;
            _preview.CloseAnimated();
        }
    }

    /// <summary>Faixa horizontal entre o ícone e o popup (na altura do ícone).</summary>
    private static RECT Bridge(RECT item, RECT popup)
    {
        if (popup.IsEmpty) return default;
        int top = Math.Max(item.Top, popup.Top) - ItemMarginPx;
        int bottom = Math.Min(item.Bottom, popup.Bottom) + ItemMarginPx;
        if (popup.Left >= item.Right) return new RECT(item.Right, top, popup.Left + 1, bottom);
        if (popup.Right <= item.Left) return new RECT(popup.Right - 1, top, item.Left, bottom);
        return default;
    }

    private static double Distance(POINT a, POINT b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
}
