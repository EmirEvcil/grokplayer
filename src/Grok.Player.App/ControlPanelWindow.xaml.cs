using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Grok.Player.App.Native;
using Grok.Player.Core.Audio;
using Grok.Player.Core.Player;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Subtitles;
using Grok.Player.Core.Video;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class ControlPanelWindow : Window
{
    public const int WidthPx = 600;
    public const int HeightPx = 460;

    private readonly nint _playerHwnd;
    private readonly PlaybackViewModel _view;
    private readonly Action<string> _log;
    private readonly Slider[] _bandSliders = new Slider[EqualizerSpec.BandCount];
    private readonly (Button Tab, Border Chrome)[] _tabs;
    private bool _stayAbovePlayer;
    private bool _playerAlwaysOnTop;
    private double _opacity = 1;
    private bool _dragging;
    private bool _syncingEq;
    private bool _syncingVideo;
    private bool _syncingSubtitle;
    private DispatcherTimer? _eqDebounce;
    private DispatcherTimer? _videoDebounce;
    private AddPresetWindow? _addPreset;
    private Point32 _dragMouse;
    private PointInt32 _dragWindow;

    public ControlPanelWindow(nint playerHwnd, bool playerAlwaysOnTop, PlaybackViewModel view, Action<string> log)
    {
        _playerHwnd = playerHwnd;
        _playerAlwaysOnTop = playerAlwaysOnTop;
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        InitializeComponent();
        _tabs =
        [
            (AudioTab, AudioTabChrome),
            (VideoTab, VideoTabChrome),
            (SubtitleTab, SubtitleTabChrome),
            (PlaybackTab, PlaybackTabChrome)
        ];

        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
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
        AppWindow.Title = "Control panel";
        AppWindow.Resize(new SizeInt32(WidthPx, HeightPx));
        AppWindow.Closing += OnAppWindowClosing;

        var hwnd = WindowNative.GetWindowHandle(this);
        WindowChrome.ApplyLook(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        BuildBandSliders();
        _eqDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _eqDebounce.Tick += EqDebounce_Tick;
        _videoDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _videoDebounce.Tick += VideoDebounce_Tick;
        _view.PropertyChanged += View_PropertyChanged;
        Closed += (_, _) =>
        {
            FlushEqualizer();
            FlushVideo();
            _eqDebounce?.Stop();
            _videoDebounce?.Stop();
            _view.PropertyChanged -= View_PropertyChanged;
            CloseAddPreset();
        };
        BuildSubtitleCombos();
        SelectTab(AudioTab);
        SyncEqualizerUi();
        SyncPlaybackUi();
        UpdatePinVisual();
        ApplyOpacity();
        SyncTopmost();
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

        FlushEqualizer();
        FlushVideo();
        CloseAddPreset();
        AppWindow.Hide();
        OpenChanged?.Invoke(false);
    }

    public void SyncPlayerAlwaysOnTop(bool value)
    {
        _playerAlwaysOnTop = value;
        SyncTopmost();
        if (_stayAbovePlayer)
        {
            PlaceAbovePlayer();
        }
        else
        {
            KeepPlayerInFront();
        }
    }

    public void PlaceAbovePlayerIfPinned()
    {
        if (_stayAbovePlayer)
        {
            PlaceAbovePlayer();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        SetOpen(false);
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button tab)
        {
            SelectTab(tab);
        }
    }

    private void SelectTab(Button selected)
    {
        var panel = (Brush)Application.Current.Resources["GrokPanelBrush"];
        var chrome = (Brush)Application.Current.Resources["GrokChromeBrush"];
        var accent = (Brush)Application.Current.Resources["GrokAccentBrush"];
        var muted = (Brush)Application.Current.Resources["GrokMutedBrush"];
        var last = _tabs.Length - 1;
        for (var i = 0; i < _tabs.Length; i++)
        {
            var on = ReferenceEquals(_tabs[i].Tab, selected);
            var right = i == last ? 0 : 1;
            _tabs[i].Tab.Foreground = on ? accent : muted;
            _tabs[i].Chrome.Background = on ? panel : chrome;
            _tabs[i].Chrome.BorderThickness = new Thickness(0, 0, right, on ? 0 : 1);
        }

        var tag = selected.Tag as string ?? string.Empty;
        var audio = string.Equals(tag, "Audio", StringComparison.Ordinal);
        var video = string.Equals(tag, "Video", StringComparison.Ordinal);
        var subtitle = string.Equals(tag, "Subtitle", StringComparison.Ordinal);
        var playback = string.Equals(tag, "Playback", StringComparison.Ordinal);
        AudioPane.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        VideoPane.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        SubtitlePane.Visibility = subtitle ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPane.Visibility = playback ? Visibility.Visible : Visibility.Collapsed;
        TabPlaceholder.Visibility = Visibility.Collapsed;
        if (audio)
        {
            SyncEqualizerUi();
        }
        else if (video)
        {
            SyncVideoUi();
        }
        else if (subtitle)
        {
            SyncSubtitleUi();
        }
        else if (playback)
        {
            SyncPlaybackUi();
        }
    }

    private void BuildBandSliders()
    {
        BandHost.ColumnDefinitions.Clear();
        BandHost.Children.Clear();
        for (var i = 0; i < EqualizerSpec.BandCount; i++)
        {
            BandHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var slider = new Slider
            {
                Minimum = EqualizerSpec.MinUi,
                Maximum = EqualizerSpec.MaxUi,
                StepFrequency = 1,
                Style = (Style)Application.Current.Resources["VerticalSquareSlider"],
                Tag = i,
                Value = 0
            };
            slider.ValueChanged += BandSlider_ValueChanged;
            Grid.SetRow(slider, 0);
            var label = new TextBlock
            {
                Text = EqualizerSpec.Labels[i],
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["GrokMutedBrush"],
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(label, 1);
            column.Children.Add(slider);
            column.Children.Add(label);
            Grid.SetColumn(column, i);
            BandHost.Children.Add(column);
            _bandSliders[i] = slider;
        }
    }

    private void View_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.Volume) or null)
        {
            SyncMasterSlider();
        }

        if (e.PropertyName is nameof(PlaybackViewModel.Speed) or nameof(PlaybackViewModel.LoopA) or nameof(PlaybackViewModel.LoopB) or null)
        {
            SyncPlaybackUi();
        }
    }

    private void SyncEqualizerUi()
    {
        _syncingEq = true;
        try
        {
            EqEnabledBox.IsChecked = _view.Equalizer.Enabled;
            PresetDropText.Text = _view.Equalizer.SelectedName;
            PresetSaveItem.IsEnabled = _view.Equalizer.CanSaveSelected;
            PresetDeleteItem.IsEnabled = _view.Equalizer.CanDeleteSelected;
            for (var i = 0; i < _bandSliders.Length; i++)
            {
                _bandSliders[i].Value = _view.Equalizer.Bands[i];
            }

            SyncMasterSlider();
        }
        finally
        {
            _syncingEq = false;
        }
    }

    private void SyncMasterSlider()
    {
        _syncingEq = true;
        try
        {
            MasterSlider.Value = _view.Volume;
        }
        finally
        {
            _syncingEq = false;
        }
    }

    private void EqEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingEq || _bandSliders[0] is null)
        {
            return;
        }

        var on = EqEnabledBox.IsChecked == true;
        _view.Equalizer.SetEnabled(on);
        _log(ActionFeedback.EqualizerEnabled(on));
    }

    private void BandSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEq || sender is not Slider slider || slider.Tag is not int index)
        {
            return;
        }

        _view.Equalizer.SetBand(index, e.NewValue, notify: false);
        _log(ActionFeedback.EqualizerBand(EqualizerSpec.Labels[index], e.NewValue));
        _eqDebounce?.Stop();
        _eqDebounce?.Start();
    }

    private void EqDebounce_Tick(object? sender, object e)
    {
        FlushEqualizer();
    }

    private void FlushEqualizer()
    {
        _eqDebounce?.Stop();
        _view.ApplyEqualizer();
    }

    private void MasterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEq || _bandSliders[0] is null)
        {
            return;
        }

        _view.Volume = e.NewValue;
        _log(ActionFeedback.Volume(e.NewValue));
    }

    private void PresetList_Opening(object sender, object e)
    {
        PresetListFlyout.Items.Clear();
        foreach (var preset in _view.Equalizer.AllPresets)
        {
            var item = new MenuFlyoutItem
            {
                Text = preset.Name,
                Style = (Style)Application.Current.Resources["CompactMenuItem"]
            };
            if (string.Equals(preset.Name, _view.Equalizer.SelectedName, StringComparison.OrdinalIgnoreCase))
            {
                item.Icon = new FontIcon { Glyph = "\uE73E", FontSize = 11 };
            }

            var name = preset.Name;
            item.Click += (_, _) => ApplyPreset(name);
            PresetListFlyout.Items.Add(item);
        }
    }

    private void ApplyPreset(string name)
    {
        _view.Equalizer.SelectPreset(name);
        SyncEqualizerUi();
        _log(ActionFeedback.EqualizerPreset(name));
    }

    private void PresetAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_addPreset is not null)
        {
            _addPreset.Activate();
            _addPreset.PlaceAbove();
            return;
        }

        var owner = WindowNative.GetWindowHandle(this);
        _addPreset = new AddPresetWindow(owner, IsLifted);
        _addPreset.Accepted += name =>
        {
            if (_view.Equalizer.AddPreset(name))
            {
                SyncEqualizerUi();
                _log("EQ preset added");
            }
        };
        _addPreset.Closed += (_, _) => _addPreset = null;
        var here = AppWindow.Position;
        _addPreset.AppWindow.Move(new PointInt32(here.X + 48, here.Y + 88));
        _addPreset.Activate();
        _addPreset.PlaceAbove();
    }

    private void CloseAddPreset()
    {
        if (_addPreset is null)
        {
            return;
        }

        var dialog = _addPreset;
        _addPreset = null;
        try
        {
            dialog.Close();
        }
        catch (Exception)
        {
        }
    }

    private void PresetSave_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Equalizer.SaveSelected())
        {
            SyncEqualizerUi();
            _log("EQ preset saved");
        }
    }

    private void PresetDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Equalizer.DeleteSelected())
        {
            SyncEqualizerUi();
            _log("EQ preset deleted");
        }
    }

    private void PresetDefault_Click(object sender, RoutedEventArgs e) => ApplyPreset(EqualizerPresets.DefaultName);

    private void SyncVideoUi()
    {
        _syncingVideo = true;
        try
        {
            BrightnessSlider.Value = _view.Video.Brightness;
            ContrastSlider.Value = _view.Video.Contrast;
            SaturationSlider.Value = _view.Video.Saturation;
            HueSlider.Value = _view.Video.Hue;
            SofterToggle.IsChecked = _view.Video.Softer;
            SharpenToggle.IsChecked = _view.Video.Sharpen;
            DeblockToggle.IsChecked = _view.Video.Deblock;
        }
        finally
        {
            _syncingVideo = false;
        }
    }

    private void PictureSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVideo || sender is not Slider slider)
        {
            return;
        }

        string? label = null;
        if (ReferenceEquals(slider, BrightnessSlider))
        {
            _view.Video.SetBrightness(e.NewValue, notify: false);
            label = "Brightness";
        }
        else if (ReferenceEquals(slider, ContrastSlider))
        {
            _view.Video.SetContrast(e.NewValue, notify: false);
            label = "Contrast";
        }
        else if (ReferenceEquals(slider, SaturationSlider))
        {
            _view.Video.SetSaturation(e.NewValue, notify: false);
            label = "Saturation";
        }
        else if (ReferenceEquals(slider, HueSlider))
        {
            _view.Video.SetHue(e.NewValue, notify: false);
            label = "Color";
        }

        if (label is null)
        {
            return;
        }

        _log(ActionFeedback.VideoPicture(label, e.NewValue));
        _videoDebounce?.Stop();
        _videoDebounce?.Start();
    }

    private void VideoDebounce_Tick(object? sender, object e) => FlushVideo();

    private void FlushVideo()
    {
        _videoDebounce?.Stop();
        _view.ApplyVideo();
    }

    private void BrightnessDefault_Click(object sender, RoutedEventArgs e) =>
        ResetPicture("Brightness", VideoPictureSpec.DefaultUi, _view.Video.ResetBrightness);

    private void ContrastDefault_Click(object sender, RoutedEventArgs e) =>
        ResetPicture("Contrast", VideoPictureSpec.DefaultUi, _view.Video.ResetContrast);

    private void SaturationDefault_Click(object sender, RoutedEventArgs e) =>
        ResetPicture("Saturation", VideoPictureSpec.DefaultUi, _view.Video.ResetSaturation);

    private void HueDefault_Click(object sender, RoutedEventArgs e) =>
        ResetPicture("Color", VideoPictureSpec.DefaultUi, _view.Video.ResetHue);

    private void ResetPicture(string label, double value, Action reset)
    {
        _videoDebounce?.Stop();
        reset();
        SyncVideoUi();
        _log(ActionFeedback.VideoPicture(label, value));
    }

    private void SofterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingVideo)
        {
            return;
        }

        var on = SofterToggle.IsChecked == true;
        _view.Video.SetSofter(on);
        _log(ActionFeedback.VideoFilter("Softer", on));
    }

    private void SharpenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingVideo)
        {
            return;
        }

        var on = SharpenToggle.IsChecked == true;
        _view.Video.SetSharpen(on);
        _log(ActionFeedback.VideoFilter("Sharpen", on));
    }

    private static readonly string[] SubFonts =
    [
        "Segoe UI", "Arial", "Calibri", "Cascadia Mono", "Consolas", "Georgia",
        "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana"
    ];

    private static readonly double[] SubSizes = [24, 32, 40, 48, 55, 64, 72, 80, 96];

    private void BuildSubtitleCombos()
    {
        _syncingSubtitle = true;
        try
        {
            SubFontBox.Items.Clear();
            foreach (var font in SubFonts)
            {
                SubFontBox.Items.Add(font);
            }

            SubSizeBox.Items.Clear();
            foreach (var size in SubSizes)
            {
                SubSizeBox.Items.Add(size.ToString("0"));
            }
        }
        finally
        {
            _syncingSubtitle = false;
        }

        SyncSubtitleUi();
    }

    private void SyncSubtitleUi()
    {
        _syncingSubtitle = true;
        try
        {
            if (!SubFontBox.Items.Contains(_view.SubFont))
            {
                SubFontBox.Items.Insert(0, _view.SubFont);
            }

            SubFontBox.SelectedItem = _view.SubFont;
            var size = _view.SubFontSize.ToString("0");
            if (!SubSizeBox.Items.Contains(size))
            {
                SubSizeBox.Items.Add(size);
            }

            SubSizeBox.SelectedItem = size;
        }
        finally
        {
            _syncingSubtitle = false;
        }
    }

    private void SyncPlaybackUi()
    {
        SpeedValueText.Text = ActionFeedback.Speed(_view.Speed)[6..];
        LoopAButton.Content = _view.LoopA is { } a ? SrtTime.Format(a) : "—";
        LoopBButton.Content = _view.LoopB is { } b ? SrtTime.Format(b) : "—";
    }

    private void SubFontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSubtitle || SubFontBox.SelectedItem is not string font)
        {
            return;
        }

        _view.SetSubFont(font);
        _log(ActionFeedback.SubtitleFont(font));
    }

    private void SubSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSubtitle || SubSizeBox.SelectedItem is not string text ||
            !double.TryParse(text, out var size))
        {
            return;
        }

        _view.SetSubFontSize(size);
        _log(ActionFeedback.SubtitleSize(size));
    }

    private void SubUp_Click(object sender, RoutedEventArgs e)
    {
        _view.NudgeSubPos(-4);
        _log("Subtitle up");
    }

    private void SubDown_Click(object sender, RoutedEventArgs e)
    {
        _view.NudgeSubPos(4);
        _log("Subtitle down");
    }

    private void SubLeft_Click(object sender, RoutedEventArgs e)
    {
        _view.NudgeSubShiftX(-1);
        _log("Subtitle left");
    }

    private void SubRight_Click(object sender, RoutedEventArgs e)
    {
        _view.NudgeSubShiftX(1);
        _log("Subtitle right");
    }

    private void SubSlower_Click(object sender, RoutedEventArgs e)
    {
        _view.Subtitles.NudgeDelay(-0.5);
        _log(ActionFeedback.SubtitleSync(_view.Subtitles.DelaySeconds));
    }

    private void SubFaster_Click(object sender, RoutedEventArgs e)
    {
        _view.Subtitles.NudgeDelay(0.5);
        _log(ActionFeedback.SubtitleSync(_view.Subtitles.DelaySeconds));
    }

    private void SubSyncDefault_Click(object sender, RoutedEventArgs e)
    {
        _view.Subtitles.ResetDelay();
        _log(ActionFeedback.SubtitleSync(0));
    }

    private void SeekBackMin_Click(object sender, RoutedEventArgs e) => SeekDelta(TimeSpan.FromMinutes(-1));

    private void SeekBackHalf_Click(object sender, RoutedEventArgs e) => SeekDelta(TimeSpan.FromSeconds(-0.5));

    private void SeekFwdHalf_Click(object sender, RoutedEventArgs e) => SeekDelta(TimeSpan.FromSeconds(0.5));

    private void SeekFwdMin_Click(object sender, RoutedEventArgs e) => SeekDelta(TimeSpan.FromMinutes(1));

    private void SeekDelta(TimeSpan delta)
    {
        _view.SeekBy(delta);
        _log(ActionFeedback.Skip(delta));
    }

    private void SpeedSlower_Click(object sender, RoutedEventArgs e)
    {
        _view.NudgeSpeed(-PlaybackSpec.SpeedStep);
        _log(ActionFeedback.Speed(_view.Speed));
    }

    private void SpeedFaster_Click(object sender, RoutedEventArgs e)
    {
        _view.NudgeSpeed(PlaybackSpec.SpeedStep);
        _log(ActionFeedback.Speed(_view.Speed));
    }

    private void SpeedDefault_Click(object sender, RoutedEventArgs e)
    {
        _view.SetSpeed(PlaybackSpec.DefaultSpeed);
        _log(ActionFeedback.Speed(_view.Speed));
    }

    private void LoopA_Click(object sender, RoutedEventArgs e)
    {
        if (_view.MarkLoopA())
        {
            _log(ActionFeedback.LoopPoint("A", _view.LoopA!.Value));
            return;
        }

        _log("A not set");
    }

    private void LoopB_Click(object sender, RoutedEventArgs e)
    {
        if (_view.MarkLoopB())
        {
            _log(ActionFeedback.LoopPoint("B", _view.LoopB!.Value));
            return;
        }

        _log("B not set");
    }

    private void LoopUndo_Click(object sender, RoutedEventArgs e)
    {
        _view.ClearLoopPoints();
        _log(ActionFeedback.LoopCleared());
    }

    private void DeblockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingVideo)
        {
            return;
        }

        var on = DeblockToggle.IsChecked == true;
        _view.Video.SetDeblock(on);
        _log(ActionFeedback.VideoFilter("Deblock", on));
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (!_view.HasMedia)
        {
            _log("Nothing to capture");
            return;
        }

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.SuggestedFileName = SuggestCaptureName();
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        picker.FileTypeChoices.Add("JPEG image", [".jpg"]);
        picker.FileTypeChoices.Add("Bitmap image", [".bmp"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var path = file.Path;
            if (string.Equals(file.FileType, ".bmp", StringComparison.OrdinalIgnoreCase))
            {
                var temp = Path.Combine(Path.GetTempPath(), $"grok-cap-{Guid.NewGuid():N}.png");
                try
                {
                    _view.CaptureFrame(temp);
                    await EncodePngAsBmpAsync(temp, path);
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }
            else
            {
                _view.CaptureFrame(path);
            }

            _log(ActionFeedback.CapturedFrame());
        }
        catch (Exception)
        {
            _log("Could not capture");
        }
    }

    private string SuggestCaptureName()
    {
        var title = _view.TitleName;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "frame";
        }

        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            title = title.Replace(bad, '_');
        }

        return $"{title}-{DateTime.Now:yyyyMMdd-HHmmss}";
    }

    private static async Task EncodePngAsBmpAsync(string pngPath, string bmpPath)
    {
        using var input = File.OpenRead(pngPath);
        using var inputWin = input.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(inputWin);
        var pixels = await decoder.GetPixelDataAsync();

        using var output = File.Create(bmpPath);
        using var outputWin = output.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputWin);
        encoder.SetPixelData(
            decoder.BitmapPixelFormat,
            decoder.BitmapAlphaMode,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels.DetachPixelData());
        await encoder.FlushAsync();
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _stayAbovePlayer = !_stayAbovePlayer;
        UpdatePinVisual();
        SyncTopmost();
        if (_stayAbovePlayer)
        {
            PlaceAbovePlayer();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SetOpen(false);

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
            if (current is Button or ToggleButton or Slider or Thumb or CheckBox or ComboBox or TextBox)
            {
                return true;
            }
        }

        return false;
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _opacity = Math.Clamp(e.NewValue, 0.2, 1);
        ApplyOpacity();
    }

    private void UpdatePinVisual()
    {
        PinIcon.Foreground = _stayAbovePlayer
            ? (Brush)Application.Current.Resources["GrokAccentBrush"]
            : (Brush)Application.Current.Resources["GrokMutedBrush"];
    }

    private bool IsLifted => _playerAlwaysOnTop || _stayAbovePlayer;

    private void SyncTopmost()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = IsLifted;
        }

        _addPreset?.SyncTopmost(IsLifted);
    }

    private void PlaceAbovePlayer()
    {
        var dialog = WindowNative.GetWindowHandle(this);
        if (dialog == 0)
        {
            return;
        }

        SetWindowPos(dialog, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        _addPreset?.PlaceAbove();
    }

    private void KeepPlayerInFront()
    {
        if (_playerHwnd == 0)
        {
            return;
        }

        SetWindowPos(_playerHwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void ApplyOpacity()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == 0)
        {
            return;
        }

        var ex = GetWindowLongPtr(hwnd, GwlExStyle);
        if ((ex & WsExLayered) == 0)
        {
            SetWindowLongPtr(hwnd, GwlExStyle, ex | WsExLayered);
        }

        var alpha = (byte)Math.Clamp(Math.Round(_opacity * 255), 51, 255);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
    }

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int LwaAlpha = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTop = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }
}
