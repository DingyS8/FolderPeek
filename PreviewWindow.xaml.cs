using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FolderPeek.Native;

namespace FolderPeek;

/// <summary>
/// Popup do preview. Uma única instância, reaproveitada (Show/Hide).
/// Nunca ativa: WS_EX_NOACTIVATE + MA_NOACTIVATE + nada focável — a janela
/// em que o usuário estava continua com o foco e o teclado.
/// Posicionamento é feito em pixels físicos via SetWindowPos (PMv2).
/// </summary>
public partial class PreviewWindow : Window
{
    private const double ShadowMarginDip = 12; // = Margin do Grid raiz no XAML
    private const double GapDip = 6;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly Stack<string> _history = new();
    private DesktopHit? _hit;
    private string _currentFolder = string.Empty;
    private CancellationTokenSource? _loadCts;
    private bool _closing;
    private bool _needsFadeIn;
    private ListBoxItem? _pressedItem;
    private IntPtr _hwnd;

    public int MaxItems { get; set; } = 500;

    internal IntPtr Handle => _hwnd;
    internal DesktopHit? Current => _hit;
    internal bool IsOpen => IsVisible && !_closing;

    internal RECT ScreenRect =>
        _hwnd != IntPtr.Zero && IsVisible && Win32.GetWindowRect(_hwnd, out var r) ? r : default;

    public PreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => { if (IsVisible) Reposition(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW; // sem foco, fora do Alt+Tab
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ abrir / fechar

    internal void ShowFor(DesktopHit hit)
    {
        _hit = hit;
        _closing = false;
        _history.Clear();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        _needsFadeIn = true;

        if (!IsVisible) Show(); // ShowActivated=False → SW_SHOWNA

        // Leva a janela pro monitor do ícone ANTES do layout, para ela assumir o DPI certo.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, hit.ItemRect.Right, hit.ItemRect.Top, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        Navigate(hit.FolderPath);
    }

    internal void CloseAnimated()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        _loadCts?.Cancel();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (!_closing) return; // foi reaberto durante o fade
            Hide();
            EntryList.ItemsSource = null;
            _hit = null;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void FadeIn()
    {
        _needsFadeIn = false;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ------------------------------------------------------------------ conteúdo

    private async void Navigate(string folder)
    {
        try { await NavigateAsync(folder); }
        catch (Exception ex) { Log.Write($"navegar '{folder}': {ex}"); }
    }

    private async Task NavigateAsync(string folder)
    {
        Log.Debug($"preview: carregando {folder}");
        _currentFolder = folder;
        bool atRoot = _history.Count == 0;

        TitleText.Text = atRoot && _hit != null ? _hit.DisplayName : FolderName(folder);
        HeaderIcon.Source = IconCache.ForFolder(folder);
        BackButton.Visibility = atRoot ? Visibility.Collapsed : Visibility.Visible;
        SubtitleText.Text = "Carregando…";

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        FolderListing listing;
        try
        {
            listing = await Task.Run(() => FolderLister.List(folder, MaxItems, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Write($"listar '{folder}': {ex}");
            listing = new FolderListing([], 0, false, "Não foi possível ler esta pasta.");
        }
        if (cts.IsCancellationRequested || _closing) return;

        EntryList.ItemsSource = listing.Entries;
        ScrollToTop();

        string count = listing.TotalCapped ? $"{listing.Total.ToString("N0", PtBr)}+ itens"
            : listing.Total == 1 ? "1 item" : $"{listing.Total.ToString("N0", PtBr)} itens";
        SubtitleText.Text = atRoot && _hit is { IsShortcut: true } ? $"Atalho · {count}" : count;
        if (listing.Error != null) SubtitleText.Text = "Indisponível";

        StatusText.Text = listing.Error ?? "Pasta vazia";
        StatusText.Visibility = listing.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool truncated = listing.Total > listing.Entries.Count;
        FooterText.Text = truncated
            ? $"Mostrando {listing.Entries.Count.ToString("N0", PtBr)} de {listing.Total.ToString("N0", PtBr)} — abra no Explorador para ver tudo"
            : string.Empty;
        FooterText.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;

        UpdateLayout();
        Reposition();
        if (_needsFadeIn) FadeIn();
    }

    private void ScrollToTop()
    {
        if (VisualTreeHelper.GetChildrenCount(EntryList) > 0
            && VisualTreeHelper.GetChild(EntryList, 0) is ScrollViewer sv)
            sv.ScrollToTop();
        else if (EntryList.Template?.FindName("SV", EntryList) is ScrollViewer sv2)
            sv2.ScrollToTop();
    }

    private static string FolderName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    // ------------------------------------------------------------------ posicionamento

    /// <summary>
    /// À direita do ícone, alinhado pelo topo; se não couber, à esquerda.
    /// Sempre dentro da área de trabalho do monitor do ícone.
    /// </summary>
    private void Reposition()
    {
        if (_hit == null || _hwnd == IntPtr.Zero || !Win32.GetWindowRect(_hwnd, out var wr)) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int margin = (int)Math.Round(ShadowMarginDip * scale);
        int gap = (int)Math.Round(GapDip * scale);
        int w = wr.Width, h = wr.Height;

        var item = _hit.ItemRect;
        var work = Win32.WorkAreaFor(new POINT(item.Left + item.Width / 2, item.Top + item.Height / 2));

        int x = item.Right + gap - margin;
        if (x + w - margin > work.Right) x = item.Left - gap - w + margin;
        x = Math.Max(work.Left - margin, Math.Min(x, work.Right - w + margin));

        int y = item.Top - margin;
        y = Math.Max(work.Top - margin, Math.Min(y, work.Bottom - h + margin));

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ interação

    private void Item_MouseDown(object sender, MouseButtonEventArgs e) => _pressedItem = sender as ListBoxItem;

    private void Item_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || !ReferenceEquals(item, _pressedItem)) return;
        _pressedItem = null;
        if (item.DataContext is not FolderEntry entry) return;
        e.Handled = true;

        // Janela não tem foco de teclado → Keyboard.Modifiers não é confiável; lê o estado físico.
        bool ctrl = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;

        if (entry.IsDirectory && !ctrl)
        {
            _history.Push(_currentFolder);       // clique em subpasta: navega dentro do preview
            Navigate(entry.FullPath);
        }
        else
        {
            ShellOpen(entry.FullPath);           // arquivo (ou Ctrl+clique em pasta): abre
            CloseAnimated();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        Navigate(_history.Pop());
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        ShellOpen(_currentFolder);
        CloseAnimated();
    }

    private static void ShellOpen(string path)
    {
        try
        {
            // Recebemos o último input (o clique), então podemos ceder o foreground
            // para o app que vai abrir — senão ele abriria atrás das janelas.
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            Log.Write($"abrir '{path}': {ex.Message}");
            System.Media.SystemSounds.Beep.Play();
        }
    }
}
