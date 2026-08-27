using System.Globalization;
using Grok.Player.Core.Presentation;
using Grok.Player.Core.Video;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Grok.Player.App;

public sealed partial class ResizePreferencesPanel : UserControl
{
    private PlaybackViewModel? _view;
    private bool _syncing;
    private bool _ready;

    public ResizePreferencesPanel()
    {
        InitializeComponent();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        FillQuality();
        FillResize();
        HookComboCursors();
        _ready = true;
    }

    public void Bind(PlaybackViewModel view)
    {
        _view = view;
        Pull();
    }

    private void FillQuality()
    {
        FillEnum(QualityPresetBox, Enum.GetValues<ScalingPreset>(), ScalingQualitySpec.Label, _ => "");
        FillEnum(UpscaleBox, Enum.GetValues<ScaleKernel>(), ScalingQualitySpec.Label, ScalingQualitySpec.KernelTip);
        FillEnum(DownscaleBox, Enum.GetValues<ScaleKernel>(), ScalingQualitySpec.Label, ScalingQualitySpec.KernelTip);
        FillEnum(ChromaBox, Enum.GetValues<ScaleKernel>(), ScalingQualitySpec.Label, ScalingQualitySpec.KernelTip);
        FillEnum(AntiRingBox, Enum.GetValues<ScaleStrength>(), ScalingQualitySpec.Label, strength => strength switch
        {
            ScaleStrength.Off => "No anti-ringing. Sharp scalers may show halos.",
            ScaleStrength.Low => "Light halo reduction. Small extra GPU cost.",
            ScaleStrength.Medium => "Noticeable cleanup on Lanczos/Spline edges.",
            _ => "Strongest cleanup. A bit softer, more GPU."
        });
        FillEnum(DebandBox, Enum.GetValues<ScaleStrength>(), ScalingQualitySpec.Label, strength => strength switch
        {
            ScaleStrength.Off => "Leave banding alone.",
            ScaleStrength.Low => "Light flatten of flat-color steps. Cheap.",
            ScaleStrength.Medium => "Default-strength deband for dark gradients.",
            _ => "Strong deband. Hides more banding, costs more GPU."
        });
    }

    private void FillResize()
    {
        FillEnum(PolicyBox, Enum.GetValues<VideoResizePolicy>(), VideoResizeSpec.Label, VideoResizeSpec.PolicyTip);
        FillEnum(SizingBox, Enum.GetValues<VideoSizingMode>(), VideoResizeSpec.Label, VideoResizeSpec.SizingTip);
        FillEnum(MultiplierBox, Enum.GetValues<VideoScaleMultiplier>(), VideoResizeSpec.Label, _ => "");
        FillEnum(AspectBox, Enum.GetValues<VideoAspectMode>(), VideoResizeSpec.Label, VideoResizeSpec.AspectTip);
        StepBox.Items.Clear();
        foreach (var percent in new[] { 0.5, 1.0, 2.0, 5.0, 10.0 })
        {
            StepBox.Items.Add(TipItem(
                percent.ToString("0.#", CultureInfo.InvariantCulture) + "%",
                percent / 100.0,
                "How far Ctrl+1–4 stretch or compress the picture."));
        }
    }

    private static void FillEnum<T>(ComboBox box, T[] values, Func<T, string> label, Func<T, string> tip)
        where T : struct, Enum
    {
        box.Items.Clear();
        foreach (var value in values)
        {
            box.Items.Add(TipItem(label(value), value, tip(value)));
        }
    }

    private static ComboBoxItem TipItem(string label, object tag, string tip)
    {
        var text = new TextBlock { Text = label };
        var item = new ComboBoxItem { Content = text, Tag = tag };
        if (tip.Length > 0)
        {
            ToolTipService.SetToolTip(text, new ToolTip { Content = tip, Placement = PlacementMode.Right });
            ToolTipService.SetToolTip(item, new ToolTip { Content = tip, Placement = PlacementMode.Right });
        }

        return item;
    }

    private void HookComboCursors()
    {
        foreach (var box in new[]
                 {
                     QualityPresetBox, UpscaleBox, DownscaleBox, ChromaBox, AntiRingBox, DebandBox,
                     PolicyBox, SizingBox, MultiplierBox, AspectBox, StepBox
                 })
        {
            box.DropDownOpened += (_, _) => RestoreArrowCursor();
            box.DropDownClosed += (_, _) =>
            {
                RestoreArrowCursor();
                SyncBoxTip(box);
            };
            box.PointerEntered += (_, _) => RestoreArrowCursor();
        }
    }

    private void RestoreArrowCursor()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private static void SyncBoxTip(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            ToolTipService.SetToolTip(box, ToolTipService.GetToolTip(item));
        }
    }

    private void Pull()
    {
        if (_view is null)
        {
            return;
        }

        _syncing = true;
        var quality = _view.Scaling.Draft;
        Select(QualityPresetBox, quality.Preset);
        Select(UpscaleBox, quality.Upscale);
        Select(DownscaleBox, quality.Downscale);
        Select(ChromaBox, quality.Chroma);
        Select(AntiRingBox, quality.AntiRing);
        Select(DebandBox, quality.Deband);

        var resize = _view.Resize.Draft;
        Select(PolicyBox, resize.Policy);
        Select(SizingBox, resize.Sizing);
        Select(MultiplierBox, resize.Multiplier);
        CustomMultiplierBox.Text = resize.CustomMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
        WidthBox.Text = resize.CustomWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = resize.CustomHeight.ToString(CultureInfo.InvariantCulture);
        KeepAspectBox.IsChecked = resize.KeepCustomAspect;
        Select(AspectBox, resize.Aspect);
        RatioXBox.Text = resize.CustomAspectX.ToString(CultureInfo.InvariantCulture);
        RatioYBox.Text = resize.CustomAspectY.ToString(CultureInfo.InvariantCulture);
        SelectStep(resize.ShortcutStep);
        foreach (var box in new[]
                 {
                     QualityPresetBox, UpscaleBox, DownscaleBox, ChromaBox, AntiRingBox, DebandBox,
                     PolicyBox, SizingBox, MultiplierBox, AspectBox, StepBox
                 })
        {
            SyncBoxTip(box);
        }

        _syncing = false;
        UpdateResizeChrome();
    }

    private static void Select<T>(ComboBox box, T value)
        where T : struct, Enum
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem { Tag: T tag } choice && EqualityComparer<T>.Default.Equals(tag, value))
            {
                box.SelectedItem = choice;
                return;
            }
        }
    }

    private static bool Read<T>(ComboBox box, out T value)
        where T : struct, Enum
    {
        if (box.SelectedItem is ComboBoxItem { Tag: T tag })
        {
            value = tag;
            return true;
        }

        value = default;
        return false;
    }

    private void SelectStep(double step)
    {
        foreach (var item in StepBox.Items)
        {
            if (item is ComboBoxItem { Tag: double value } choice && Math.Abs(value - step) < 0.0001)
            {
                StepBox.SelectedItem = choice;
                return;
            }
        }
    }

    private int SourceW => _view?.CurrentResizeContext().SourceW ?? 0;

    private int SourceH => _view?.CurrentResizeContext().SourceH ?? 0;

    private void UpdateResizeChrome()
    {
        var enabled = _view is { Resize.Draft.Policy: not VideoResizePolicy.Never };
        var sizing = _view?.Resize.Draft.Sizing ?? VideoSizingMode.Fit;
        var multiplier = _view?.Resize.Draft.Multiplier ?? VideoScaleMultiplier.One;
        var aspect = _view?.Resize.Draft.Aspect ?? VideoAspectMode.KeepSource;
        var showMultiplier = enabled && sizing == VideoSizingMode.Multiplier;
        var showCustomFactor = showMultiplier && multiplier == VideoScaleMultiplier.Custom;
        var showResolution = enabled && sizing == VideoSizingMode.CustomResolution;
        var showRatio = aspect == VideoAspectMode.Custom;

        SizingLabel.Opacity = enabled ? 1 : 0.45;
        SizingBox.IsEnabled = enabled;
        SetRow(MultiplierLabel, MultiplierBox, showMultiplier);
        SetRow(CustomMultiplierLabel, CustomMultiplierBox, showCustomFactor);
        SetRow(ResolutionLabel, ResolutionHost, showResolution);
        SetRow(CustomRatioLabel, CustomRatioHost, showRatio);
    }

    private static void SetRow(FrameworkElement label, UIElement field, bool on)
    {
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        label.Visibility = vis;
        field.Visibility = vis;
        if (field is Control control)
        {
            control.IsEnabled = on;
        }
    }

    private void QualityPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing || _view is null || !Read(QualityPresetBox, out ScalingPreset preset))
        {
            return;
        }

        _view.Scaling.SelectPreset(preset);
        _view.Note(ActionFeedback.ScalingPreset(ScalingQualitySpec.Label(preset)));
        Pull();
    }

    private void UpscaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PatchQuality(box => Read(box, out ScaleKernel kernel) ? () => _view!.Scaling.SetUpscale(kernel) : null, UpscaleBox);

    private void DownscaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PatchQuality(box => Read(box, out ScaleKernel kernel) ? () => _view!.Scaling.SetDownscale(kernel) : null, DownscaleBox);

    private void ChromaBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PatchQuality(box => Read(box, out ScaleKernel kernel) ? () => _view!.Scaling.SetChroma(kernel) : null, ChromaBox);

    private void AntiRingBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PatchQuality(box => Read(box, out ScaleStrength strength) ? () => _view!.Scaling.SetAntiRing(strength) : null, AntiRingBox);

    private void DebandBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PatchQuality(box => Read(box, out ScaleStrength strength) ? () => _view!.Scaling.SetDeband(strength) : null, DebandBox);

    private void PatchQuality<T>(Func<T, Action?> build, T box)
        where T : ComboBox
    {
        if (!_ready || _syncing || _view is null)
        {
            return;
        }

        build(box)?.Invoke();
        _syncing = true;
        Select(QualityPresetBox, _view.Scaling.Draft.Preset);
        _syncing = false;
        if (ReferenceEquals(box, UpscaleBox) && Read(UpscaleBox, out ScaleKernel up))
        {
            _view.Note(ActionFeedback.ScaleKernel("Upscaling", ScalingQualitySpec.Label(up)));
        }
        else if (ReferenceEquals(box, DownscaleBox) && Read(DownscaleBox, out ScaleKernel down))
        {
            _view.Note(ActionFeedback.ScaleKernel("Downscaling", ScalingQualitySpec.Label(down)));
        }
        else if (ReferenceEquals(box, ChromaBox) && Read(ChromaBox, out ScaleKernel chroma))
        {
            _view.Note(ActionFeedback.ScaleKernel("Chroma", ScalingQualitySpec.Label(chroma)));
        }
        else if (ReferenceEquals(box, AntiRingBox) && Read(AntiRingBox, out ScaleStrength ring))
        {
            _view.Note(ActionFeedback.ScaleStrength("Anti-ringing", ScalingQualitySpec.Label(ring)));
        }
        else if (ReferenceEquals(box, DebandBox) && Read(DebandBox, out ScaleStrength deband))
        {
            _view.Note(ActionFeedback.ScaleStrength("Deband", ScalingQualitySpec.Label(deband)));
        }
    }

    private void PolicyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing || _view is null || !Read(PolicyBox, out VideoResizePolicy policy))
        {
            return;
        }

        _view.Resize.SetPolicy(policy);
        UpdateResizeChrome();
        _view.Note(ActionFeedback.ResizePolicy(VideoResizeSpec.Label(policy)));
    }

    private void SizingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing || _view is null || !Read(SizingBox, out VideoSizingMode sizing))
        {
            return;
        }

        _view.Resize.SetSizing(sizing);
        UpdateResizeChrome();
        _view.Note(ActionFeedback.ResizeSizing(VideoResizeSpec.Label(sizing)));
    }

    private void MultiplierBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing || _view is null || !Read(MultiplierBox, out VideoScaleMultiplier multiplier))
        {
            return;
        }

        _view.Resize.SetMultiplier(multiplier);
        UpdateResizeChrome();
        _view.Note(ActionFeedback.ResizeSizing(VideoResizeSpec.Label(multiplier)));
    }

    private void CustomMultiplierBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_view is null ||
            _view.Resize.Draft.Sizing != VideoSizingMode.Multiplier ||
            _view.Resize.Draft.Multiplier != VideoScaleMultiplier.Custom)
        {
            return;
        }

        if (!VideoResizeSpec.TryPositiveDouble(CustomMultiplierBox.Text, out var value) ||
            !_view.Resize.SetCustomMultiplier(value))
        {
            CustomMultiplierBox.Text = _view.Resize.Draft.CustomMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    private void WidthBox_TextChanged(object sender, TextChangedEventArgs e) =>
        TryApplyWidth(revert: false);

    private void HeightBox_TextChanged(object sender, TextChangedEventArgs e) =>
        TryApplyHeight(revert: false);

    private void WidthBox_LostFocus(object sender, RoutedEventArgs e) => TryApplyWidth(revert: true);

    private void HeightBox_LostFocus(object sender, RoutedEventArgs e) => TryApplyHeight(revert: true);

    private void TryApplyWidth(bool revert)
    {
        if (_syncing || _view is null || _view.Resize.Draft.Sizing != VideoSizingMode.CustomResolution)
        {
            return;
        }

        if (!VideoResizeSpec.TryPositiveInt(WidthBox.Text, out var width) ||
            !_view.Resize.SetCustomWidth(width, SourceW, SourceH))
        {
            if (revert)
            {
                WriteSizeBoxes();
            }

            return;
        }

        if (_view.Resize.Draft.KeepCustomAspect)
        {
            WriteHeightOnly();
        }

        _view.Note(ActionFeedback.ResizeSize(_view.Resize.Draft.CustomWidth, _view.Resize.Draft.CustomHeight));
    }

    private void TryApplyHeight(bool revert)
    {
        if (_syncing || _view is null || _view.Resize.Draft.Sizing != VideoSizingMode.CustomResolution)
        {
            return;
        }

        if (!VideoResizeSpec.TryPositiveInt(HeightBox.Text, out var height) ||
            !_view.Resize.SetCustomHeight(height, SourceW, SourceH))
        {
            if (revert)
            {
                WriteSizeBoxes();
            }

            return;
        }

        if (_view.Resize.Draft.KeepCustomAspect)
        {
            WriteWidthOnly();
        }

        _view.Note(ActionFeedback.ResizeSize(_view.Resize.Draft.CustomWidth, _view.Resize.Draft.CustomHeight));
    }

    private void WriteSizeBoxes()
    {
        if (_view is null)
        {
            return;
        }

        _syncing = true;
        WidthBox.Text = _view.Resize.Draft.CustomWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _view.Resize.Draft.CustomHeight.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void WriteHeightOnly()
    {
        if (_view is null)
        {
            return;
        }

        _syncing = true;
        HeightBox.Text = _view.Resize.Draft.CustomHeight.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void WriteWidthOnly()
    {
        if (_view is null)
        {
            return;
        }

        _syncing = true;
        WidthBox.Text = _view.Resize.Draft.CustomWidth.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void KeepAspectBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing || _view is null)
        {
            return;
        }

        _view.Resize.SetKeepCustomAspect(KeepAspectBox.IsChecked == true, SourceW, SourceH);
        WriteSizeBoxes();
        if (_view.Resize.Draft.KeepCustomAspect)
        {
            _view.Note(ActionFeedback.ResizeSize(_view.Resize.Draft.CustomWidth, _view.Resize.Draft.CustomHeight));
        }
    }

    private void AspectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing || _view is null || !Read(AspectBox, out VideoAspectMode aspect))
        {
            return;
        }

        _view.Resize.SetAspect(aspect);
        UpdateResizeChrome();
        SyncLinkedResolution();
        _view.Note(ActionFeedback.ResizeAspect(VideoResizeSpec.Label(aspect)));
        if (_view.Resize.Draft is { Sizing: VideoSizingMode.CustomResolution, KeepCustomAspect: true })
        {
            _view.Note(ActionFeedback.ResizeSize(_view.Resize.Draft.CustomWidth, _view.Resize.Draft.CustomHeight));
        }
    }

    private void CustomRatio_TextChanged(object sender, TextChangedEventArgs e) => TryApplyRatio(revert: false);

    private void CustomRatio_LostFocus(object sender, RoutedEventArgs e) => TryApplyRatio(revert: true);

    private void TryApplyRatio(bool revert)
    {
        if (_syncing || _view is null || _view.Resize.Draft.Aspect != VideoAspectMode.Custom)
        {
            return;
        }

        if (!VideoResizeSpec.TryPositiveInt(RatioXBox.Text, out var x) ||
            !VideoResizeSpec.TryPositiveInt(RatioYBox.Text, out var y) ||
            !_view.Resize.SetCustomAspect(x, y))
        {
            if (revert)
            {
                _syncing = true;
                RatioXBox.Text = _view.Resize.Draft.CustomAspectX.ToString(CultureInfo.InvariantCulture);
                RatioYBox.Text = _view.Resize.Draft.CustomAspectY.ToString(CultureInfo.InvariantCulture);
                _syncing = false;
            }

            return;
        }

        SyncLinkedResolution();
        _view.Note(ActionFeedback.ResizeAspect($"{x}:{y}"));
        if (_view.Resize.Draft is { Sizing: VideoSizingMode.CustomResolution, KeepCustomAspect: true })
        {
            _view.Note(ActionFeedback.ResizeSize(_view.Resize.Draft.CustomWidth, _view.Resize.Draft.CustomHeight));
        }
    }

    private void SyncLinkedResolution()
    {
        if (_view is not { Resize.Draft: { Sizing: VideoSizingMode.CustomResolution, KeepCustomAspect: true } draft })
        {
            return;
        }

        _view.Resize.SetCustomWidth(draft.CustomWidth, SourceW, SourceH);
        WriteSizeBoxes();
    }

    private void StepBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing || _view is null || StepBox.SelectedItem is not ComboBoxItem { Tag: double step })
        {
            return;
        }

        if (_view.Resize.SetShortcutStep(step))
        {
            _view.Note(ActionFeedback.ShortcutStep(step * 100));
        }
    }

    private void ResizePreview_Click(object sender, RoutedEventArgs e)
    {
        CommitVisibleFields();
        _view?.Resize.Preview();
        _view?.ApplyResizeLive();
        UpdateResizeChrome();
        _view?.Note(ActionFeedback.ResizePreview());
    }

    private void ResizeApply_Click(object sender, RoutedEventArgs e)
    {
        CommitVisibleFields();
        _view?.Resize.Apply();
        _view?.ApplyResizeLive();
        UpdateResizeChrome();
        _view?.Note(ActionFeedback.ResizeApplied());
    }

    private void ResizeReset_Click(object sender, RoutedEventArgs e)
    {
        _view?.Resize.Reset();
        Pull();
        _view?.ApplyResizeLive();
        _view?.Note(ActionFeedback.ResizeReset());
    }

    private void CommitVisibleFields()
    {
        if (_view is null)
        {
            return;
        }

        var draft = _view.Resize.Draft;
        if (draft.Sizing == VideoSizingMode.Multiplier && draft.Multiplier == VideoScaleMultiplier.Custom)
        {
            CustomMultiplierBox_LostFocus(CustomMultiplierBox, new RoutedEventArgs());
        }

        if (draft.Sizing == VideoSizingMode.CustomResolution)
        {
            WidthBox_LostFocus(WidthBox, new RoutedEventArgs());
            HeightBox_LostFocus(HeightBox, new RoutedEventArgs());
        }

        if (draft.Aspect == VideoAspectMode.Custom)
        {
            CustomRatio_LostFocus(RatioXBox, new RoutedEventArgs());
        }
    }
}
