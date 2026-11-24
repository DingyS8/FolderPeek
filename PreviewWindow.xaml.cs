using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using FolderPeek.Native;
using Microsoft.Win32;

namespace FolderPeek;

/// <summary>
/// Popup do preview. Uma única instância, reaproveitada (Show/Hide).
///
/// Visual: acrílico do Windows 11 (DWM) + cantos/sombra do sistema, grade de ícones,
/// caminho clicável, cor de destaque do Windows e animação de entrada (fade + slide).
/// Nunca ativa: WS_EX_NOACTIVATE + MA_NOACTIVATE + nada focável.
/// Posicionamento em pixels físicos via SetWindowPos (PMv2).
/// </summary>
public partial class PreviewWindow : Window
{
    private const double GapDip = 8;
    private const double IconDip = 48;

    // Backdrop do sistema existe a partir do Windows 11 22H2 (build 22621).
    private static readonly bool BackdropSupported = Environment.OSVersion.Version.Build >= 22621;
    private static readonly bool RoundCornersSupported = Environment.OSVersion.Version.Build >= 22000;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private DesktopHit? _hit;
    private string _rootFolder = string.Empty;
    private readonly List<string> _parts = new(); // subpastas navegadas a partir da raiz
    private CancellationTokenSource? _loadCts;
    private bool _closing;
    private bool _needsEnter;
    private ListBoxItem? _pressedItem;
    private string _summary = string.Empty;
    private IntPtr _hwnd;

    public int MaxItems { get; set; } = 500;

    internal IntPtr Handle => _hwnd;
    internal DesktopHit? Current => _hit;
    internal bool IsOpen => IsVisible && !_closing;

    internal RECT ScreenRect =>
        _hwnd != IntPtr.Zero && IsVisible && Win32.GetWindowRect(_hwnd, out var r) ? r : default;

    private string CurrentFolder => _parts.Count == 0 ? _rootFolder : Path.Combine(new[] { _rootFolder }.Concat(_parts).ToArray());

    public PreviewWindow()
    {
        InitializeComponent();

        // Área cliente cobrindo a janela toda, com o frame do DWM estendido por trás (= acrílico).
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = BackdropSupported ? new Thickness(-1) : new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        if (!BackdropSupported) Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => { if (IsVisible) Reposition(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW; // sem foco, fora do Alt+Tab
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook(WndProc);

        if (BackdropSupported)
        {
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            Win32.SetDwm(_hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);           // acrílico escuro
            Win32.SetDwm(_hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, Win32.DWMSBT_TRANSIENTWINDOW);
        }
        if (RoundCornersSupported) Win32.SetDwm(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_ROUND);
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
        _rootFolder = hit.FolderPath;
        _parts.Clear();
        ApplyAccent();

        ContentRoot.BeginAnimation(OpacityProperty, null);
        ContentRoot.Opacity = 0;
        _needsEnter = true;

        // Cria o HWND e leva pro monitor do ícone (para assumir o DPI certo) sem mostrar ainda.
        new WindowInteropHelper(this).EnsureHandle();
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, hit.ItemRect.Right, hit.ItemRect.Top, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        Navigate(CurrentFolder);
    }

    internal void CloseAnimated()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        _loadCts?.Cancel();
        ShellImageLoader.NewGeneration();

        var fade = new DoubleAnimation(ContentRoot.Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (!_closing) return; // reaberto durante o fade
            Hide();
            EntryList.ItemsSource = null;
            _hit = null;
        };
        ContentRoot.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Mostra a janela "camuflada" (DWM cloak) até o layout e a posição finais
    /// estarem prontos — assim ela nunca aparece no lugar/tamanho errado — e então
    /// entra com fade + deslize de baixo para cima, como os flyouts do Windows 11.
    /// </summary>
    private void Enter()
    {
        _needsEnter = false;
        Win32.SetDwm(_hwnd, Win32.DWMWA_CLOAK, 1);
        if (!IsVisible) Show(); // ShowActivated=False → SW_SHOWNA
        UpdateLayout();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_closing) return;
            Reposition();
            Win32.SetDwm(_hwnd, Win32.DWMWA_CLOAK, 0);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ContentRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
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
        ShellImageLoader.NewGeneration();

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        FolderEntry.IconPx = (int)Math.Round(IconDip * scale);
        int headerPx = (int)Math.Round(20 * scale);
        FileAttributes attrs = FileAttributes.Directory;
        try { attrs = File.GetAttributes(folder); } catch { }
        HeaderIcon.Source = ShellImageLoader.Get(folder, true, attrs, headerPx, img => HeaderIcon.Source = img);

        BuildCrumbs();
        BackButton.Visibility = _parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.ToolTip = "Voltar";
        OpenButton.ToolTip = "Abrir no Explorador de Arquivos";

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

        StatusText.Text = listing.Error ?? "Pasta vazia";
        StatusText.Visibility = listing.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        string count = listing.TotalCapped ? $"{listing.Total.ToString("N0", PtBr)}+ itens"
            : listing.Total == 1 ? "1 item" : $"{listing.Total.ToString("N0", PtBr)} itens";
        if (listing.Total > listing.Entries.Count)
            count = $"Mostrando {listing.Entries.Count.ToString("N0", PtBr)} de {count} — abra no Explorador para ver tudo";
        if (_parts.Count == 0 && _hit is { IsShortcut: true }) count = $"Atalho para {_hit.FolderPath}  ·  {count}";
        _summary = listing.Error != null ? "Indisponível" : count;
        FooterText.Text = _summary;

        if (_needsEnter) Enter();
        else { UpdateLayout(); Reposition(); }
    }

    /// <summary>Raiz › … › penúltima › atual — cada parte volta para aquele nível.</summary>
    private void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var segments = new List<(string Label, int Depth)> { (_hit?.DisplayName ?? FolderName(_rootFolder), 0) };
        for (int i = 0; i < _parts.Count; i++) segments.Add((_parts[i], i + 1));

        bool collapsed = segments.Count > 3;
        var shown = collapsed ? segments.Take(1).Concat(segments.Skip(segments.Count - 2)).ToList() : segments;

        for (int i = 0; i < shown.Count; i++)
        {
            if (i > 0)
            {
                Crumbs.Children.Add(Separator());
                if (collapsed && i == 1)
                {
                    Crumbs.Children.Add(new TextBlock { Text = "…", Foreground = (Brush)FindResource("FgFaint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
                    Crumbs.Children.Add(Separator());
                }
            }
            var (label, depth) = shown[i];
            bool current = depth == _parts.Count;
            var b = new Button
            {
                Content = label,
                Style = (Style)FindResource(current ? "CrumbCurrent" : "Crumb"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!current) b.Click += (_, _) => GoToDepth(depth);
            Crumbs.Children.Add(b);
        }
    }

    private TextBlock Separator() => new()
    {
        Text = "",
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = 9,
        Foreground = (Brush)FindResource("FgFaint"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(1, 1, 1, 0),
    };

    private void GoToDepth(int depth)
    {
        if (depth >= _parts.Count) return;
        _parts.RemoveRange(depth, _parts.Count - depth);
        Navigate(CurrentFolder);
    }

    private void ScrollToTop()
    {
        if (EntryList.Template?.FindName("SV", EntryList) is ScrollViewer sv) sv.ScrollToTop();
    }

    private static string FolderName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    /// <summary>Cor de destaque do Windows (variante clara, a que o tema escuro usa).</summary>
    private void ApplyAccent()
    {
        var c = Color.FromRgb(0x60, 0xCD, 0xFF);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 8) c = Color.FromRgb(p[4], p[5], p[6]); // "Light 2"
            else
            {
                using var d = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (d?.GetValue("AccentColor") is int v) c = Color.FromRgb((byte)v, (byte)(v >> 8), (byte)(v >> 16));
            }
        }
        catch { }

        Resources["AccentSolid"] = Frozen(Color.FromArgb(0xFF, c.R, c.G, c.B));
        Resources["AccentFill"] = Frozen(Color.FromArgb(0x30, c.R, c.G, c.B));
        Resources["AccentStroke"] = Frozen(Color.FromArgb(0x66, c.R, c.G, c.B));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
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
        int gap = (int)Math.Round(GapDip * scale);
        int w = wr.Width, h = wr.Height;

        var item = _hit.ItemRect;
        var work = Win32.WorkAreaFor(new POINT(item.Left + item.Width / 2, item.Top + item.Height / 2));

        int x = item.Right + gap;
        if (x + w > work.Right) x = item.Left - gap - w;
        x = Math.Max(work.Left + gap, Math.Min(x, work.Right - w - gap));

        int y = Math.Max(work.Top + gap, Math.Min(item.Top, work.Bottom - h - gap));

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ interação

    private void Item_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: FolderEntry entry }) FooterText.Text = entry.Details;
    }

    private void Item_MouseLeave(object sender, MouseEventArgs e) => FooterText.Text = _summary;

    private void Item_MouseDown(object sender, MouseButtonEventArgs e) => _pressedItem = sender as ListBoxItem;

    private void Item_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || !ReferenceEquals(item, _pressedItem)) return;
        _pressedItem = null;
        if (item.DataContext is not FolderEntry entry) return;
        e.Handled = true;

        // Janela não tem foco de teclado → Keyboard.Modifiers não é confiável; lê o estado físico.
        bool ctrl = ((ushort)Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;

        if (entry.IsDirectory && !ctrl)
        {
            _parts.Add(entry.Name);              // clique em subpasta: navega dentro do preview
            Navigate(CurrentFolder);
        }
        else
        {
            ShellOpen(entry.FullPath);           // arquivo (ou Ctrl+clique em pasta): abre
            CloseAnimated();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoToDepth(_parts.Count - 1);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        ShellOpen(CurrentFolder);
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
