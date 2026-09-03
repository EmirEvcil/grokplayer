namespace Grok.Player.Core.Video;

public sealed class VideoModel
{
    private double _brightness = VideoPictureSpec.DefaultUi;
    private double _contrast = VideoPictureSpec.DefaultUi;
    private double _saturation = VideoPictureSpec.DefaultUi;
    private double _hue = VideoPictureSpec.DefaultUi;
    private bool _softer;
    private bool _sharpen;
    private bool _deblock;
    private bool _superResolution;
    private HdrOutputMode _hdr = HdrOutputMode.Native;

    public event Action? Changed;

    public double Brightness => _brightness;

    public double Contrast => _contrast;

    public double Saturation => _saturation;

    public double Hue => _hue;

    public bool Softer => _softer;

    public bool Sharpen => _sharpen;

    public bool Deblock => _deblock;

    public bool SuperResolution => _superResolution;

    public HdrOutputMode Hdr => _hdr;

    public void SetBrightness(double value, bool notify = true) =>
        SetPicture(ref _brightness, value, notify);

    public void SetContrast(double value, bool notify = true) =>
        SetPicture(ref _contrast, value, notify);

    public void SetSaturation(double value, bool notify = true) =>
        SetPicture(ref _saturation, value, notify);

    public void SetHue(double value, bool notify = true) =>
        SetPicture(ref _hue, value, notify);

    public void ResetBrightness() => SetBrightness(VideoPictureSpec.DefaultUi);

    public void ResetContrast() => SetContrast(VideoPictureSpec.DefaultUi);

    public void ResetSaturation() => SetSaturation(VideoPictureSpec.DefaultUi);

    public void ResetHue() => SetHue(VideoPictureSpec.DefaultUi);

    public void SetSofter(bool value) => SetFilter(ref _softer, value);

    public void SetSharpen(bool value) => SetFilter(ref _sharpen, value);

    public void SetDeblock(bool value) => SetFilter(ref _deblock, value);

    public void SetSuperResolution(bool value, bool notify = true) =>
        SetFilter(ref _superResolution, value, notify);

    public void SetHdr(HdrOutputMode value, bool notify = true)
    {
        if (_hdr == value)
        {
            return;
        }

        _hdr = value;
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    public string FilterGraph => VideoFilterGraph.Build(_softer, _sharpen, _deblock);

    private void SetPicture(ref double field, double value, bool notify)
    {
        var clamped = VideoPictureSpec.ClampUi(value);
        if (Math.Abs(field - clamped) < 0.01)
        {
            return;
        }

        field = clamped;
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    private void SetFilter(ref bool field, bool value, bool notify = true)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        if (notify)
        {
            Changed?.Invoke();
        }
    }
}
