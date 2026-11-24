using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Automation;
using FolderPeek.Native;

namespace FolderPeek;

/// <summary>Item da Área de Trabalho que deve abrir preview.</summary>
/// <param name="ItemPath">Caminho do item no desktop (pasta ou .lnk).</param>
/// <param name="FolderPath">Pasta cujo conteúdo será listado (alvo do atalho, se for atalho).</param>
/// <param name="ItemRect">Retângulo do ícone+rótulo em pixels físicos de tela.</param>
internal sealed record DesktopHit(string ItemPath, string FolderPath, string DisplayName, RECT ItemRect, bool IsShortcut);

internal sealed record UiaItem(string Name, RECT Rect);

/// <summary>
/// Descobre QUAL item da Área de Trabalho está sob o cursor e qual o caminho dele.
///
/// Estratégia em duas fontes, porque nenhuma sozinha resolve:
///  1. UI Automation (FromPoint) diz com precisão SE o cursor está sobre um ícone
///     e devolve o retângulo + rótulo exibido — mas não o caminho.
///  2. IFolderView da view do desktop (via IShellWindows.FindWindowSW(SWC_DESKTOP))
///     dá o PIDL e a posição de cada item — o PIDL vira caminho completo com
///     SHGetNameFromIDList. Cruzamos as duas por posição e confirmamos pelo nome.
///
/// Não injeta nada no explorer.exe nem lê memória de outro processo.
/// Tudo aqui roda numa thread MTA de background (thread pool).
/// </summary>
internal static class DesktopItemResolver
{
    public static bool IsDesktopListView(IntPtr hwnd)
    {
        if (Win32.ClassOf(hwnd) != "SysListView32") return false;
        var defView = Win32.GetAncestor(hwnd, Win32.GA_PARENT);
        if (Win32.ClassOf(defView) != "SHELLDLL_DefView") return false;
        var host = Win32.ClassOf(Win32.GetAncestor(defView, Win32.GA_PARENT));
        return host is "Progman" or "WorkerW";
    }

    public static IntPtr FindDesktopListView()
    {
        var defView = Win32.FindWindowEx(Win32.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            IntPtr w = IntPtr.Zero;
            while ((w = Win32.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
            {
                defView = Win32.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) break;
            }
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : Win32.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>Resolve o item sob <paramref name="pt"/>. Null se não for pasta/atalho de pasta.</summary>
    public static DesktopHit? Resolve(POINT pt, IntPtr listView)
    {
        var uia = HitTestUia(pt, listView);
        Log.Debug(uia == null ? $"  nenhum ícone em {pt}" : $"  ícone '{uia.Name}' {uia.Rect}");
        return uia == null ? null : Match(uia, listView);
    }

    /// <summary>
    /// Qual ícone do desktop contém <paramref name="pt"/>.
    ///
    /// NÃO usa AutomationElement.FromPoint: overlays transparentes de tela cheia
    /// (ex.: "NVIDIA GeForce Overlay", Discord, Xbox Game Bar) ficam por cima no
    /// hit-test do UIA, embora o WindowFromPoint os ignore. Em vez disso, pede ao
    /// próprio ListView do desktop a lista de itens (uma chamada só, com Name e
    /// BoundingRectangle em cache) e testa o retângulo de cada um.
    /// </summary>
    public static UiaItem? HitTestUia(POINT pt, IntPtr listView)
    {
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        using (cache.Activate())
        {
            var items = AutomationElement.FromHandle(listView).FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            foreach (AutomationElement it in items)
            {
                var rect = ToRect(it.Cached.BoundingRectangle);
                if (rect.Contains(pt)) return new UiaItem(it.Cached.Name ?? string.Empty, rect);
            }
        }
        return null;
    }

    /// <summary>Cruza o item do UIA com os itens do IFolderView do desktop.</summary>
    public static DesktopHit? Match(UiaItem uia, IntPtr listView)
    {
        var view = AcquireDesktopFolderView();
        if (view == null) return null;

        var all = new List<(IntPtr Pidl, double Dist)>();
        try
        {
            if (view.ItemCount(ShellConst.SVGIO_ALLVIEW, out int count) < 0) return null;

            for (int i = 0; i < count; i++)
            {
                if (view.Item(i, out IntPtr pidl) < 0 || pidl == IntPtr.Zero) continue;
                double dist = double.MaxValue;
                if (view.GetItemPosition(pidl, out POINT p) >= 0)
                {
                    Win32.ClientToScreen(listView, ref p);
                    dist = Math.Sqrt(Sq(p.X - uia.Rect.Left) + Sq(p.Y - uia.Rect.Top));
                }
                all.Add((pidl, dist));
            }

            double tol = Math.Max(uia.Rect.Width, uia.Rect.Height);
            var near = all.Where(c => c.Dist <= tol).OrderBy(c => c.Dist).Take(4).ToList();

            // 1) entre os mais próximos, o que tem o mesmo rótulo
            IntPtr chosen = near.FirstOrDefault(c => SameName(c.Pidl, uia.Name)).Pidl;
            // 2) senão, o mais próximo se estiver bem colado
            if (chosen == IntPtr.Zero && near.Count > 0 && near[0].Dist <= tol / 2) chosen = near[0].Pidl;
            // 3) senão, rótulo único no desktop inteiro (cobre offsets de coordenada inesperados)
            if (chosen == IntPtr.Zero)
            {
                var byName = all.Where(c => SameName(c.Pidl, uia.Name)).Take(2).ToList();
                if (byName.Count == 1) chosen = byName[0].Pidl;
            }

            return chosen == IntPtr.Zero ? null : BuildHit(chosen, uia);
        }
        finally
        {
            foreach (var c in all) Marshal.FreeCoTaskMem(c.Pidl);
            Release(view);
        }
    }

    private static DesktopHit? BuildHit(IntPtr pidl, UiaItem uia)
    {
        // O desktop é a raiz do namespace: PIDL filho dele == PIDL absoluto.
        string? path = Win32.NameFromPidl(pidl, Win32.SIGDN_FILESYSPATH);
        if (string.IsNullOrEmpty(path)) return null; // item virtual (Lixeira, Este Computador...)

        string display = Win32.NameFromPidl(pidl, Win32.SIGDN_NORMALDISPLAY) ?? uia.Name;

        if (Directory.Exists(path))
            return new DesktopHit(path, path, display, uia.Rect, IsShortcut: false);

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ResolveShortcutFolder(path);
            if (target != null) return new DesktopHit(path, target, display, uia.Rect, IsShortcut: true);
        }
        return null;
    }

    /// <summary>Alvo do .lnk, se for uma pasta existente.</summary>
    public static string? ResolveShortcutFolder(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new ShellLinkCoClass();
            ((IPersistFile)obj).Load(lnkPath, 0 /* STGM_READ */);
            var link = (IShellLinkW)obj;

            var sb = new StringBuilder(32768);
            if (link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) == 0 && sb.Length > 0)
            {
                var t = Environment.ExpandEnvironmentVariables(sb.ToString());
                if (Directory.Exists(t)) return t;
            }
            // Atalhos criados a partir de PIDL (sem caminho texto)
            if (link.GetIDList(out IntPtr pidl) == 0 && pidl != IntPtr.Zero)
            {
                try
                {
                    var t = Win32.NameFromPidl(pidl, Win32.SIGDN_FILESYSPATH);
                    if (t != null && Directory.Exists(t)) return t;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
        }
        catch (Exception ex) { Log.Write($"lnk '{lnkPath}': {ex.Message}"); }
        finally { Release(obj); }
        return null;
    }

    private static IFolderView? AcquireDesktopFolderView()
    {
        object? shellWindows = null, disp = null, browser = null, view = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellConst.CLSID_ShellWindows, throwOnError: true)!;
            shellWindows = Activator.CreateInstance(type);
            var sw = (IShellWindows)shellWindows!;

            object loc = ShellConst.CSIDL_DESKTOP;
            object root = null!; // VT_EMPTY
            disp = sw.FindWindowSW(ref loc, ref root, ShellConst.SWC_DESKTOP, out _, ShellConst.SWFO_NEEDDISPATCH);
            if (disp == null) return null;

            Guid sid = ShellConst.SID_STopLevelBrowser, iid = ShellConst.IID_IShellBrowser;
            if (((IOleServiceProvider)disp).QueryService(ref sid, ref iid, out IntPtr pBrowser) < 0 || pBrowser == IntPtr.Zero)
                return null;
            try { browser = Marshal.GetObjectForIUnknown(pBrowser); }
            finally { Marshal.Release(pBrowser); }

            if (((IShellBrowser)browser).QueryActiveShellView(out view) < 0 || view == null) return null;
            var fv = (IFolderView)view;
            view = null; // ownership passa para quem chamou
            return fv;
        }
        catch (Exception ex)
        {
            Log.Write($"AcquireDesktopFolderView: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            Release(view); Release(browser); Release(disp); Release(shellWindows);
        }
    }

    /// <summary>Esquenta UIA + COM para o primeiro hover não pagar o custo de carga.</summary>
    public static void WarmUp()
    {
        try
        {
            _ = AutomationElement.RootElement.Current.Name;
            Release(AcquireDesktopFolderView());
        }
        catch (Exception ex) { Log.Write($"WarmUp: {ex.Message}"); }
    }

    /// <summary>Modo --diag: casa todos os ícones visíveis sem precisar mexer o mouse.</summary>
    public static string Diagnose()
    {
        var sb = new StringBuilder();
        var lv = FindDesktopListView();
        sb.AppendLine($"ListView do desktop: 0x{lv.ToInt64():X} ({Win32.ClassOf(lv)}) host={Win32.ClassOf(Win32.GetAncestor(Win32.GetAncestor(lv, Win32.GA_PARENT), Win32.GA_PARENT))} reconhecido={IsDesktopListView(lv)}");
        if (lv == IntPtr.Zero) return sb.ToString();

        var items = AutomationElement.FromHandle(lv).FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        sb.AppendLine($"Itens via UIA: {items.Count}");
        foreach (AutomationElement it in items)
        {
            var r = ToRect(it.Current.BoundingRectangle);
            // Fluxo real: hit-test no centro do ícone → casar → caminho.
            var center = new POINT(r.Left + r.Width / 2, r.Top + r.Height / 3);
            var uia = HitTestUia(center, lv) ?? new UiaItem("(hit-test falhou) " + it.Current.Name, r);
            DesktopHit? hit = null;
            string err = "";
            try { hit = Resolve(center, lv); } catch (Exception ex) { err = ex.Message; }
            string res = hit == null ? $"(sem preview) {err}"
                : (hit.IsShortcut ? $"ATALHO -> {hit.FolderPath}" : $"PASTA  {hit.FolderPath}");
            sb.AppendLine($"  {uia.Rect,-24} {uia.Name,-32} {res}");
        }

        // posições cruas do IFolderView, para calibrar offset
        var view = AcquireDesktopFolderView();
        if (view != null)
        {
            try
            {
                view.ItemCount(ShellConst.SVGIO_ALLVIEW, out int n);
                var spacing = new POINT();
                view.GetSpacing(ref spacing);
                sb.AppendLine($"IFolderView: {n} itens, spacing={spacing}");
                for (int i = 0; i < n; i++)
                {
                    if (view.Item(i, out IntPtr pidl) < 0) continue;
                    try
                    {
                        view.GetItemPosition(pidl, out POINT p);
                        var s = p; Win32.ClientToScreen(lv, ref s);
                        sb.AppendLine($"  pos={p} tela={s} {Win32.NameFromPidl(pidl, Win32.SIGDN_NORMALDISPLAY)}");
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
            }
            finally { Release(view); }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------
    private static bool SameName(IntPtr pidl, string uiaName) =>
        pidl != IntPtr.Zero && string.Equals(Win32.NameFromPidl(pidl, Win32.SIGDN_NORMALDISPLAY), uiaName, StringComparison.Ordinal);

    private static RECT ToRect(System.Windows.Rect r) =>
        r.IsEmpty || double.IsInfinity(r.Width) ? default
            : new RECT((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));

    private static double Sq(double v) => v * v;

    private static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}
