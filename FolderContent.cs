using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Media;
using FolderPeek.Native;
using Microsoft.Win32;

namespace FolderPeek;

internal sealed class FolderEntry : INotifyPropertyChanged
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Tamanho do ícone em pixels físicos (48 DIP × escala do monitor).</summary>
    public static int IconPx { get; set; } = 48;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public FileAttributes Attributes { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private ImageSource? _icon;
    private bool _requested;

    /// <summary>Ícone grande/miniatura, carregado em background; avisa a UI quando chega.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _icon = ShellImageLoader.Get(FullPath, IsDirectory, Attributes, IconPx, img =>
                {
                    _icon = img;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                });
            }
            return _icon;
        }
    }

    /// <summary>Linha do rodapé quando o mouse está sobre o item.</summary>
    public string Details => IsDirectory
        ? $"{Name}  ·  Pasta  ·  {Modified.ToString("dd/MM/yyyy HH:mm", PtBr)}"
        : $"{Name}  ·  {FormatSize(Size)}  ·  {Modified.ToString("dd/MM/yyyy HH:mm", PtBr)}";

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
