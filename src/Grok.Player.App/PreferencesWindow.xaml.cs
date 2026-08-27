using System.Runtime.InteropServices;
using Grok.Player.App.Native;
using Grok.Player.Core.Preferences;
using Grok.Player.Core.Presentation;
using Microsoft.UI.Input;
using Windows.Foundation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class PreferencesWindow : Window
{
    public const int WidthPx = 960;
    public const int HeightPx = 600;
    public const int MinWidthPx = 720;
    public const int MinHeightPx = 440;

    private readonly PlaybackViewModel? _view;
    private readonly nint _playerHwnd;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.Ordinal);
    private bool _stayAbove;
    private bool _playerAlwaysOnTop;
    private bool _dragging;
    private bool _tabDragging;
    private bool _tabMoved;
    private double _tabStartOffset;
    private double _tabStartX;
    private string _query = "";
    private string _selectedId = "general";
    private DispatcherTimer? _searchDebounce;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;

    public PreferencesWindow(nint playerHwnd, bool playerAlwaysOnTop, PlaybackViewModel? view = null)
    {
        _view = view;
        _playerHwnd = playerHwnd;
        _playerAlwaysOnTop = playerAlwaysOnTop;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsAlwaysOnTop = playerAlwaysOnTop;
        }

        SetTitleBar(TitleDrag);
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.Title = "Preferences";
        AppWindow.Resize(new SizeInt32(WidthPx, HeightPx));
        AppWindow.Closing += OnClosing;

        var hwnd = WindowNative.GetWindowHandle(this);
        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowChrome.LimitSize(hwnd, MinWidthPx, MinHeightPx);
        if (playerHwnd != 0)
        {
            SetWindowLongPtr(hwnd, GwlpHwndParent, playerHwnd);
        }

        PresetBox.Items.Add(PreferencesCatalog.DefaultPresetName);
        PresetBox.SelectedIndex = 0;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildNav();
        };

        RebuildNav();
        SelectPage("general");
        UpdatePinVisual();
        SyncTopmost();
        Root.Loaded += (_, _) => UpdateInputRegions();
        Root.SizeChanged += (_, _) => UpdateInputRegions();
    }

    public event Action<bool>? OpenChanged;

    public bool IsOpen => AppWindow.IsVisible;

    public void SetOpen(bool open)
    {
        if (open)
        {
            AppWindow.Show();
            AppWindow.Resize(new SizeInt32(WidthPx, HeightPx));
            SyncTopmost();
            Activate();
            PlaceAbovePlayer();
            OpenChanged?.Invoke(true);
            return;
        }

        AppWindow.Hide();
        OpenChanged?.Invoke(false);
    }

    public void SyncPlayerAlwaysOnTop(bool value)
    {
        _playerAlwaysOnTop = value;
        SyncTopmost();
        if (_stayAbove)
        {
            PlaceAbovePlayer();
        }
    }

    public void PlaceAbovePlayerIfPinned()
    {
        if (_stayAbove)
        {
            PlaceAbovePlayer();
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        SetOpen(false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text ?? "";
        _searchDebounce?.Stop();
        _searchDebounce?.Start();
    }

    private void RebuildNav()
    {
        NavHost.Children.Clear();
        _labels.Clear();
        foreach (var root in PreferencesCatalog.Roots)
        {
            AddNavRows(root, 0);
        }

        PaintSelection();
    }

    private void AddNavRows(PreferencesPage page, int depth)
    {
        if (!PreferencesCatalog.Matches(page, _query))
        {
            return;
        }

        var open = page.HasChildren && (_expanded.Contains(page.Id) || _query.Trim().Length > 0);
        var row = new Grid { Height = depth == 0 ? 26 : 22 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8 + depth * 12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (page.HasChildren)
        {
            var toggle = new Button
            {
                Width = 16,
                Height = 16,
                MinWidth = 16,
                MinHeight = 16,
                Padding = new Thickness(0, 0, 0, 0),
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(1, 1, 1, 1),
                BorderBrush = (Brush)Application.Current.Resources["GrokLineBrush"],
                Background = (Brush)Application.Current.Resources["GrokInkBrush"],
                Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Tag = page.Id,
                Content = ExpandGlyph(open)
            };
            toggle.Click += Expand_Click;
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
        }

        var label = new TextBlock
        {
            Text = page.Title,
            FontSize = depth == 0 ? 13 : 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"]
        };
        var hit = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
            Child = label,
            Tag = page.Id
        };
        hit.PointerReleased += NavLabel_PointerReleased;
        hit.DoubleTapped += NavLabel_DoubleTapped;
        Grid.SetColumn(hit, 3);
        row.Children.Add(hit);
        _labels[page.Id] = label;
        NavHost.Children.Add(row);

        if (open)
        {
            foreach (var child in page.Children)
            {
                AddNavRows(child, depth + 1);
            }
        }
    }

    private static Grid ExpandGlyph(bool open)
    {
        var mark = new TextBlock
        {
            Text = open ? "\u2212" : "+",
            FontSize = 11,
            LineHeight = 11,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            OpticalMarginAlignment = OpticalMarginAlignment.TrimSideBearings,
            IsTextScaleFactorEnabled = false
        };
        var box = new Grid
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.Children.Add(mark);
        return box;
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ToggleExpand(id);
        }
    }

    private void NavLabel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } &&
            e.GetCurrentPoint(NavHost).Properties.PointerUpdateKind ==
            PointerUpdateKind.LeftButtonReleased)
        {
            SelectPage(id);
        }
    }

    private void NavLabel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            ToggleExpand(id);
            e.Handled = true;
        }
    }

    private void ToggleExpand(string id)
    {
        var page = PreferencesCatalog.Find(id);
        if (page is null || !page.HasChildren)
        {
            return;
        }

        if (!_expanded.Add(id))
        {
            _expanded.Remove(id);
        }

        RebuildNav();
    }

    private void SelectPage(string id)
    {
        var page = PreferencesCatalog.Find(id) ?? PreferencesCatalog.Find("general");
        if (page is null)
        {
            return;
        }

        _selectedId = page.Id;
        if (page.Parent is { } parent)
        {
            _expanded.Add(parent.Id);
            if (!_labels.ContainsKey(page.Id))
            {
                RebuildNav();
            }
        }

        PaintSelection();
        RebuildTabs(page);
        var resize = page.Id == "video-resize";
        var downloads = page.Id == "downloads";
        SectionPlaceholder.Text = page.Title;
        SectionPlaceholder.Visibility = resize || downloads ? Visibility.Collapsed : Visibility.Visible;
        ResizePanel.Visibility = resize ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = downloads ? Visibility.Visible : Visibility.Collapsed;
        if (resize && _view is not null)
        {
            ResizePanel.Bind(_view);
        }

        if (downloads)
        {
            DownloadPanel.Bind();
        }
    }

    private void PaintSelection()
    {
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        foreach (var (id, label) in _labels)
        {
            label.Foreground = id == _selectedId ? accent : muted;
        }
    }

    private void RebuildTabs(PreferencesPage page)
    {
        var tabs = page.Tabs;
        SectionTabs.Children.Clear();
        SectionTabs.ColumnDefinitions.Clear();
        var panel = (Brush)Application.Current.Resources["GrokPanelBrush"];
        var chrome = (Brush)Application.Current.Resources["GrokChromeBrush"];
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        var line = (Brush)Application.Current.Resources["GrokLineBrush"];
        var last = tabs.Count - 1;
        for (var i = 0; i < tabs.Count; i++)
        {
            SectionTabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tabPage = tabs[i];
            var on = tabPage.Id == page.Id;
            var frame = new Border
            {
                Background = on ? panel : chrome,
                BorderBrush = line,
                BorderThickness = new Thickness(0, 0, i == last ? 0 : 1, on ? 0 : 1)
            };
            frame.Tag = tabPage.Id;
            frame.Child = new TextBlock
            {
                Text = tabPage.Title,
                FontSize = 13,
                Foreground = on ? accent : muted,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };
            frame.MinWidth = 92;
            Grid.SetColumn(frame, i);
            SectionTabs.Children.Add(frame);
        }

        SectionTabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rest = new Border
        {
            Background = chrome,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        Grid.SetColumn(rest, tabs.Count);
        SectionTabs.Children.Add(rest);
    }

    private void TabStrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabScroller).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _tabDragging = true;
        _tabMoved = false;
        _tabStartOffset = TabScroller.HorizontalOffset;
        _tabStartX = e.GetCurrentPoint(TabScroller).Position.X;
        TabScroller.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TabStrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_tabDragging)
        {
            return;
        }

        var x = e.GetCurrentPoint(TabScroller).Position.X;
        var delta = x - _tabStartX;
        if (Math.Abs(delta) > 3)
        {
            _tabMoved = true;
        }

        // Drag left → reveal tabs on the right.
        TabScroller.ChangeView(Math.Max(0, _tabStartOffset - delta), null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void TabStrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_tabDragging)
        {
            return;
        }

        _tabDragging = false;
        try
        {
            TabScroller.ReleasePointerCapture(e.Pointer);
        }
        catch (Exception)
        {
        }

        if (!_tabMoved)
        {
            for (var current = e.OriginalSource as DependencyObject;
                 current is not null;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement { Tag: string id })
                {
                    SelectPage(id);
                    break;
                }
            }
        }

        e.Handled = true;
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _stayAbove = !_stayAbove;
        UpdatePinVisual();
        SyncTopmost();
        if (_stayAbove)
        {
            PlaceAbovePlayer();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SetOpen(false);

    private void UpdatePinVisual()
    {
        PinIcon.Foreground = _stayAbove
            ? (Brush)Application.Current.Resources["GrokAccentBrush"]
            : (Brush)Application.Current.Resources["GrokMutedBrush"];
    }

    private void SyncTopmost()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _playerAlwaysOnTop || _stayAbove;
        }
    }

    private void PlaceAbovePlayer()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == 0)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void EmptyArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed || IsInteractive(e.OriginalSource))
        {
            return;
        }

        if (!GetCursorPos(out _dragMouse))
        {
            return;
        }

        _dragWindow = AppWindow.Position;
        _dragging = true;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void EmptyArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var now))
        {
            return;
        }

        AppWindow.Move(new PointInt32(
            _dragWindow.X + now.X - _dragMouse.X,
            _dragWindow.Y + now.Y - _dragMouse.Y));
    }

    private void EmptyArea_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (sender is UIElement element)
        {
            try
            {
                element.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception)
            {
            }
        }
    }

    private void UpdateInputRegions()
    {
        if (Root.XamlRoot is null)
        {
            return;
        }

        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, [PixelRect(TitleDrag)]);
            source.SetRegionRects(NonClientRegionKind.Passthrough, [PixelRect(BodyHost), PixelRect(ChromeButtons)]);
        }
        catch (Exception)
        {
        }
    }

    private RectInt32 PixelRect(FrameworkElement element)
    {
        var scale = element.XamlRoot?.RasterizationScale ?? 1;
        var origin = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new RectInt32(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            Math.Max(1, (int)Math.Round(element.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(element.ActualHeight * scale)));
    }

    private bool IsInteractive(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or ComboBox or ComboBoxItem or TextBox or CheckBox or ScrollBar or Thumb
                    or ListViewItem or ScrollViewer or FlyoutPresenter ||
                ReferenceEquals(current, NavHost) ||
                ReferenceEquals(current, TabScroller) ||
                ReferenceEquals(current, SectionTabs) ||
                ReferenceEquals(current, SectionBody))
            {
                return true;
            }
        }

        return false;
    }

    private const int GwlpHwndParent = -8;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTop = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }
}
