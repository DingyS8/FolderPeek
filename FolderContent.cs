using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderPeek.Native;
using Microsoft.Win32;

namespace FolderPeek;

internal sealed class FolderEntry
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public FileAttributes Attributes { get; init; }

    private ImageSource? _icon;
    private bool _iconLoaded;

    /// <summary>Carregado sob demanda (só itens visíveis são realizados pela virtualização).</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _iconLoaded = true; _icon = IconCache.Get(FullPath, IsDirectory, Attributes); }
            return _icon;
        }
    }

    public string Meta => IsDirectory
        ? $"Pasta · {Modified.ToString("dd/MM/yyyy HH:mm", PtBr)}"
        : Modified.ToString("dd/MM/yyyy HH:mm", PtBr);

    public string SizeText => IsDirectory ? string.Empty : FormatSize(Size);

    public Visibility ChevronVisibility => IsDirectory ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v < 10 ? "0.0" : "0", PtBr) + " " + units[u];
    }
}

internal sealed record FolderListing(IReadOnlyList<FolderEntry> Entries, int Total, bool TotalCapped, string? Error);

internal static class FolderLister
{
    private const int EnumerationCap = 50_000;

    public static FolderListing List(string folder, int maxShown, CancellationToken ct)
    {
        var (showHidden, showSuperHidden, hideKnownExt) = ExplorerSettings();
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0, // filtro manual abaixo, igual ao Explorer
        };

        var entries = new List<FolderEntry>();
        int total = 0;
        bool capped = false;
        try
        {
            foreach (var fsi in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                var a = fsi.Attributes;
                bool hidden = (a & FileAttributes.Hidden) != 0;
                bool system = (a & FileAttributes.System) != 0;
                if (hidden && !showHidden) continue;
                if (hidden && system && !showSuperHidden) continue; // "arquivos protegidos do SO"

                if (++total >= EnumerationCap) { capped = true; break; }
                bool isDir = (a & FileAttributes.Directory) != 0;
                entries.Add(new FolderEntry
                {
                    Name = isDir ? fsi.Name : DisplayName(fsi.Name, hideKnownExt),
                    FullPath = fsi.FullName,
                    IsDirectory = isDir,
                    Size = isDir ? 0 : ((FileInfo)fsi).Length,
                    Modified = fsi.LastWriteTime,
                    Attributes = a,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return new([], 0, false, "Sem permissão para ler esta pasta."); }
        catch (DirectoryNotFoundException) { return new([], 0, false, "Pasta não encontrada."); }
        catch (IOException ex) { return new([], 0, false, ex.Message); }

        // Pastas primeiro, depois ordem "natural" do Explorer (arq2 < arq10).
        entries.Sort((x, y) =>
            x.IsDirectory != y.IsDirectory ? (x.IsDirectory ? -1 : 1) : Win32.StrCmpLogicalW(x.Name, y.Name));

        if (entries.Count > maxShown) entries.RemoveRange(maxShown, entries.Count - maxShown);
        return new(entries, total, capped, null);
    }

    /// <summary>
    /// Respeita "Mostrar itens ocultos", "Ocultar arquivos protegidos" e
    /// "Ocultar as extensões dos tipos de arquivo conhecidos" do Explorer.
    /// </summary>
    private static (bool Hidden, bool SuperHidden, bool HideKnownExt) ExplorerSettings()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return (k?.GetValue("Hidden") is int h && h == 1,
                    k?.GetValue("ShowSuperHidden") is int s && s == 1,
                    k?.GetValue("HideFileExt") is not int e || e != 0);
        }
        catch { return (false, false, true); }
    }

    // Extensões que o Explorer nunca mostra (NeverShowExt): atalhos.
    private static readonly HashSet<string> NeverShowExt = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".pif", ".appref-ms" };
    private static readonly Dictionary<string, bool> KnownExtCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object KnownExtGate = new();

    /// <summary>Nome como o Explorer mostraria (sem extensão de atalho / de tipo conhecido).</summary>
    private static string DisplayName(string fileName, bool hideKnownExt)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length == 0 || ext.Length == fileName.Length) return fileName; // ".gitignore" fica como está
        bool hide = NeverShowExt.Contains(ext) || (hideKnownExt && IsKnownType(ext));
        return hide ? fileName[..^ext.Length] : fileName;
    }

    private static bool IsKnownType(string ext)
    {
        lock (KnownExtGate)
        {
            if (KnownExtCache.TryGetValue(ext, out bool known)) return known;
            try { using var k = Registry.ClassesRoot.OpenSubKey(ext); known = k != null; }
            catch { known = false; }
            KnownExtCache[ext] = known;
            return known;
        }
    }
}

/// <summary>
/// Ícones nativos via SHGetFileInfo. Tipos "genéricos" são cacheados por extensão
/// (sem tocar no disco); .exe/.lnk/.ico e pastas personalizadas são por arquivo.
/// Arquivos-placeholder da nuvem (OneDrive) nunca são lidos, para não disparar download.
/// Uso só na thread de UI.
/// </summary>
internal static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "", ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".scr", ".cpl", ".appref-ms",
    };

    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN | OFFLINE
    private const int CloudMask = 0x00400000 | 0x00040000 | 0x00001000;

    public static ImageSource? Get(string path, bool isDir, FileAttributes attrs)
    {
        bool cloud = ((int)attrs & CloudMask) != 0;
        bool generic;
        string key;
        if (isDir)
        {
            // Pastas com desktop.ini (ícone customizado) têm ReadOnly ou System.
            generic = cloud || (attrs & (FileAttributes.ReadOnly | FileAttributes.System)) == 0;
            key = generic ? "<dir>" : path;
        }
        else
        {
            var ext = Path.GetExtension(path);
            generic = cloud || !PerFileExtensions.Contains(ext);
            key = generic ? "*" + ext : path;
        }

        if (Cache.TryGetValue(key, out var img)) return img;
        if (Cache.Count > 3000) Cache.Clear();
        img = Load(path, isDir, generic);
        Cache[key] = img;
        return img;
    }

    public static ImageSource? ForFolder(string path)
    {
        FileAttributes a = FileAttributes.Directory;
        try { a = File.GetAttributes(path); } catch { }
        return Get(path, true, a);
    }

    private static ImageSource? Load(string path, bool isDir, bool generic)
    {
        var info = new SHFILEINFO();
        uint flags = Win32.SHGFI_ICON | Win32.SHGFI_SMALLICON;
        uint fileAttrs = 0;
        if (generic)
        {
            flags |= Win32.SHGFI_USEFILEATTRIBUTES;
            fileAttrs = isDir ? Win32.FILE_ATTRIBUTE_DIRECTORY : Win32.FILE_ATTRIBUTE_NORMAL;
        }

        if (Win32.SHGetFileInfo(path, fileAttrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;
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
