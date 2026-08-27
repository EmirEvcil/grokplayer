using System.Runtime.InteropServices;
using Grok.Player.App.Native;
using Grok.Player.Core.Subtitles;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.Text;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class SubtitleStyleWindow : Window
{
    private static readonly string[] PaletteColors =
    [
        "#FFFFFF", "#E5E5E5", "#F0C93A", "#D9899C", "#FF0000", "#FFA500",
        "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#000000"
    ];

    private readonly nint _owner;
    private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _playerAlwaysOnTop;
    private bool _stayAbove;
    private bool _dragging;
    private bool _syncing;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;
    private string _color = "#FFFFFF";
    private string _sample = "Sample subtitle";

    public SubtitleStyleWindow(nint owner, bool topmost, IReadOnlyList<CaptionSpan> current)
    {
        _owner = owner;
        _playerAlwaysOnTop = topmost;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = topmost;
        }

        SetTitleBar(TitleDrag);
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.Title = "Subtitle style";

        var hwnd = WindowNative.GetWindowHandle(this);
        Root.Loaded += (_, _) => FitOnce();
        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (owner != 0)
        {
            SetWindowLongPtr(hwnd, GwlpHwndParent, owner);
        }

        var combined = CaptionMarkup.Combine(current);
        _sample = string.IsNullOrWhiteSpace(CaptionMarkup.Plain(current)) ? "Sample subtitle" : CaptionMarkup.Plain(current);
        _color = string.IsNullOrWhiteSpace(combined.Color) ? "#FFFFFF" : combined.Color!;
        foreach (var tag in CaptionMarkup.SelectedTags(combined))
        {
            _tags.Add(tag);
        }

        BuildPalette();
        BuildTags();
        HexBox.Text = _color;
        PaintSwatch();
        SyncSliders();
        UpdatePreview();
        UpdatePinVisual();
    }

    public event Action<CaptionSpan>? Applied;

    public void SyncTopmost(bool topmost)
    {
        _playerAlwaysOnTop = topmost;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _playerAlwaysOnTop || _stayAbove;
        }
    }

    private bool _fitted;
    private int _fitTries;

    private void FitOnce()
    {
        if (_fitted)
        {
            return;
        }

        try
        {
            BodyScroll.UpdateLayout();
            var content = BodyScroll.Content as FrameworkElement;
            content?.UpdateLayout();
            var contentHeight = content?.ActualHeight ?? 0;
            if (contentHeight < 80 && _fitTries++ < 4)
            {
                DispatcherQueue.TryEnqueue(FitOnce);
                return;
            }

            var wanted = 40 + 56 + Math.Max(420, (int)Math.Ceiling(contentHeight + 28));
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var width = Math.Min(500, Math.Max(420, Math.Min(500, area.Width - 24)));
            var height = Math.Min(wanted, Math.Max(420, area.Height - 24));
            if (width >= 200 && height >= 160)
            {
                AppWindow.Resize(new SizeInt32(width, height));
            }

            _fitted = true;
        }
        catch (Exception)
        {
            _fitted = true;
        }
    }

    public void PlaceAbove()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == 0)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void BuildPalette()
    {
        Palette.ColumnDefinitions.Clear();
        Palette.Children.Clear();
        for (var i = 0; i < PaletteColors.Length; i++)
        {
            Palette.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var hex = PaletteColors[i];
            var swatch = new Button
            {
                MinWidth = 22,
                Height = 22,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["GrokLineBrush"],
                Background = BrushOf(hex),
                Tag = hex
            };
            swatch.Click += (_, _) => SetColor(hex);
            Grid.SetColumn(swatch, i);
            Palette.Children.Add(swatch);
        }
    }

    private void BuildTags()
    {
        TagList.Children.Clear();
        _boxes.Clear();
        foreach (var option in CaptionMarkup.TagOptions)
        {
            var box = new CheckBox
            {
                Content = option.Label + "  <" + option.Tag + ">",
                IsChecked = _tags.Contains(option.Id),
                FontSize = 12,
                MinHeight = 28,
                Tag = option.Id
            };
            box.Checked += TagBox_Changed;
            box.Unchecked += TagBox_Changed;
            _boxes[option.Id] = box;
            TagList.Children.Add(box);
        }

        RefreshTagSummary();
    }

    private void TagBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } box)
        {
            return;
        }

        if (box.IsChecked == true)
        {
            _tags.Add(id);
        }
        else
        {
            _tags.Remove(id);
        }

        RefreshTagSummary();
        UpdatePreview();
    }

    private void TagDrop_Click(object sender, RoutedEventArgs e)
    {
        TagFlyout.ShowAt(TagDrop);
    }

    private void RefreshTagSummary()
    {
        if (_tags.Count == 0)
        {
            TagSummary.Text = "Select tags";
            TagHint.Text = "Multiple tags can stay selected.";
            return;
        }

        var labels = CaptionMarkup.TagOptions
            .Where(option => _tags.Contains(option.Id))
            .Select(option => option.Label)
            .ToArray();
        TagSummary.Text = string.Join(", ", labels);
        TagHint.Text = labels.Length == 1 ? "1 tag selected." : labels.Length + " tags selected.";
    }

    private CaptionSpan CurrentStyle() => CaptionMarkup.WithTags(_sample, _color, _tags);

    private void UpdatePreview()
    {
        var style = CurrentStyle();
        PreviewText.Text = style.Quote ? "“" + _sample + "”" : _sample;
        PreviewText.FontFamily = new FontFamily(style.Pre || style.Code ? "Cascadia Mono, Consolas" : "Segoe UI");
        PreviewText.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
        PreviewText.FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
        PreviewText.TextDecorations = (style.Underline ? TextDecorations.Underline : TextDecorations.None) |
                                      (style.Strike ? TextDecorations.Strikethrough : TextDecorations.None);
        PreviewText.FontSize = style.Super || style.Sub ? 13 : style.Small ? 14 : 18;
        PreviewText.Foreground = style.Mark && string.Equals(_color, "#FFFFFF", StringComparison.OrdinalIgnoreCase)
            ? (Brush)Application.Current.Resources["GrokAccentBrush"]
            : BrushOf(_color);
    }

    private void SetColor(string hex)
    {
        _color = hex;
        HexBox.Text = hex;
        PaintSwatch();
        SyncSliders();
        UpdatePreview();
    }

    private void SyncSliders()
    {
        if (!TryRgb(_color, out var r, out var g, out var b))
        {
            return;
        }

        _syncing = true;
        RedSlider.Value = r;
        GreenSlider.Value = g;
        BlueSlider.Value = b;
        RedValue.Text = r.ToString();
        GreenValue.Text = g.ToString();
        BlueValue.Text = b.ToString();
        _syncing = false;
    }

    private void Rgb_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var r = (byte)Math.Clamp(RedSlider.Value, 0, 255);
        var g = (byte)Math.Clamp(GreenSlider.Value, 0, 255);
        var b = (byte)Math.Clamp(BlueSlider.Value, 0, 255);
        SetColor($"#{r:X2}{g:X2}{b:X2}");
    }

    private void PaintSwatch() => PreviewSwatch.Background = BrushOf(_color);

    private void HexBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = (HexBox.Text ?? "").Trim();
        if (!text.StartsWith('#') && text.Length is 6)
        {
            text = "#" + text;
        }

        if (text.Length == 7)
        {
            SetColor(text.ToUpperInvariant());
        }
        else
        {
            HexBox.Text = _color;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Emit();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Emit();
        Close();
    }

    private void Emit() => Applied?.Invoke(CurrentStyle());

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _stayAbove = !_stayAbove;
        UpdatePinVisual();
        SyncTopmost(_playerAlwaysOnTop);
        if (_stayAbove)
        {
            PlaceAbove();
        }
    }

    private void UpdatePinVisual()
    {
        PinIcon.Foreground = _stayAbove
            ? (Brush)Application.Current.Resources["GrokAccentBrush"]
            : (Brush)Application.Current.Resources["GrokMutedBrush"];
    }

    private static bool TryRgb(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 255;
        return hex.Length == 7 &&
               byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out r) &&
               byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out g) &&
               byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    private static SolidColorBrush BrushOf(string hex)
    {
        return TryRgb(hex, out var r, out var g, out var b)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
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

    private static bool IsInteractive(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or ToggleButton or TextBox or Slider or CheckBox or DropDownButton)
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
