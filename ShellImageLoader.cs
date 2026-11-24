using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FolderPeek.Native;

namespace FolderPeek;

/// <summary>
/// Ícones grandes e miniaturas do shell (IShellItemImageFactory — o mesmo que o
/// Explorer usa no modo "Ícones grandes"), carregados numa thread STA própria
/// para nunca travar a UI. Imagens/vídeos/PDF/3D ganham miniatura real; o resto,
/// ícone. Arquivos-placeholder da nuvem nunca são lidos (não disparam download).
/// </summary>
internal static class ShellImageLoader
{
    private sealed record Request(string Path, bool IsDir, FileAttributes Attrs, int Px, int Gen,
                                  Dispatcher Ui, Action<ImageSource?> Done);

    private enum Mode { Thumbnail, Icon, ByAttributes }

    private static readonly BlockingCollection<Request> Queue = new();
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static int _generation;

    // Tipos com miniatura útil.
    private static readonly HashSet<string> ThumbExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".ico", ".jfif", ".avif",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v",
        ".pdf", ".3mf", ".stl", ".psd",
    };

    // Tipos cujo ícone varia por arquivo.
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "", ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".scr", ".cpl", ".appref-ms",
    };

    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN | OFFLINE
    private const int CloudMask = 0x00400000 | 0x00040000 | 0x00001000;

    static ShellImageLoader()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "FolderPeek icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Descarta pedidos pendentes de pastas que já não estão na tela.</summary>
    public static void NewGeneration() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Devolve a imagem se já estiver em cache; senão agenda e retorna null —
    /// <paramref name="done"/> é chamado depois na thread de UI.
    /// </summary>
    public static ImageSource? Get(string path, bool isDir, FileAttributes attrs, int px, Action<ImageSource?> done)
    {
        var key = KeyFor(path, isDir, attrs, px, out _);
        if (Cache.TryGetValue(key, out var img)) return img;
        Queue.Add(new Request(path, isDir, attrs, px, Volatile.Read(ref _generation), Dispatcher.CurrentDispatcher, done));
        return null;
    }

    private static string KeyFor(string path, bool isDir, FileAttributes attrs, int px, out Mode mode)
    {
        bool cloud = ((int)attrs & CloudMask) != 0;
        if (isDir)
        {
            mode = cloud ? Mode.ByAttributes : Mode.Thumbnail; // Win11 mostra o conteúdo no ícone da pasta
            return cloud ? $"<dir>|{px}" : $"{path}|{px}";
        }

        var ext = Path.GetExtension(path);
        if (cloud) { mode = Mode.ByAttributes; return $"*{ext}|{px}"; }
        if (ThumbExtensions.Contains(ext)) { mode = Mode.Thumbnail; return $"{path}|{px}"; }
        mode = Mode.Icon;
        return PerFileExtensions.Contains(ext) ? $"{path}|{px}" : $"*{ext}|{px}";
    }

    private static void Worker()
    {
        foreach (var r in Queue.GetConsumingEnumerable())
        {
            if (r.Gen != Volatile.Read(ref _generation)) continue;
            var key = KeyFor(r.Path, r.IsDir, r.Attrs, r.Px, out var mode);
            if (!Cache.TryGetValue(key, out var img))
            {
                try { img = mode == Mode.ByAttributes ? LoadByAttributes(r.Path, r.IsDir) : LoadShell(r.Path, r.Px, mode == Mode.Icon); }
                catch (Exception ex) { Log.Debug($"imagem '{r.Path}': {ex.Message}"); img = null; }
                img ??= LoadByAttributes(r.Path, r.IsDir);
                if (Cache.Count > 3000) Cache.Clear();
                Cache[key] = img;
            }
            r.Ui.BeginInvoke(DispatcherPriority.Background, () => r.Done(img));
        }
    }

    // ------------------------------------------------------------------ IShellItemImageFactory

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_ICONONLY = 0x04;

    private static ImageSource? LoadShell(string path, int px, bool iconOnly)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj) < 0) return null;
        try
        {
            var factory = (IShellItemImageFactory)obj;
            int flags = SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0);
            if (factory.GetImage(new SIZE { cx = px, cy = px }, flags, out IntPtr hbm) < 0 || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(obj); }
    }

    // ------------------------------------------------------------------ HBITMAP → BitmapSource (com alfa)

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint f0, f1, f2; public IntPtr dshSection; public uint dsOffset; }

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, out DIBSECTION ds);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    /// <summary>
    /// CreateBitmapSourceFromHBitmap perde o canal alfa dos ícones (fundo preto),
    /// então copiamos os pixels da DIB na mão. JPEG sem alfa (tudo 0) vira opaco.
    /// </summary>
    private static ImageSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0
            || ds.dsBm.bmBitsPixel != 32 || ds.dsBm.bmBits == IntPtr.Zero)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = ds.dsBm.bmWidthBytes;
        var px = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, px, 0, px.Length);

        if (ds.dsBmih.biHeight > 0) // DIB bottom-up → inverte as linhas
        {
            var row = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                Buffer.BlockCopy(px, y * stride, row, 0, stride);
                Buffer.BlockCopy(px, (h - 1 - y) * stride, px, y * stride, stride);
                Buffer.BlockCopy(row, 0, px, (h - 1 - y) * stride, stride);
            }
        }

        bool hasAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { hasAlpha = true; break; }

        var src = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, px, stride);
        src.Freeze();
        return src;
    }

    // ------------------------------------------------------------------ fallback sem tocar no arquivo

    private static ImageSource? LoadByAttributes(string path, bool isDir)
    {
        var info = new SHFILEINFO();
        uint flags = Win32.SHGFI_ICON | Win32.SHGFI_USEFILEATTRIBUTES; // ícone grande (32 px)
        uint fa = isDir ? Win32.FILE_ATTRIBUTE_DIRECTORY : Win32.FILE_ATTRIBUTE_NORMAL;
        if (Win32.SHGetFileInfo(path, fa, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { Win32.DestroyIcon(info.hIcon); }
    }
}
