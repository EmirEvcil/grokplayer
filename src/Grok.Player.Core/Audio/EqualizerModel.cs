using System.Text.Json;

namespace Grok.Player.Core.Audio;

public sealed class EqualizerModel
{
    private readonly string _storePath;
    private readonly List<EqualizerPreset> _custom = [];
    private readonly double[] _bands = new double[EqualizerSpec.BandCount];

    public EqualizerModel(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrokPlayer",
            "equalizer-presets.json");
        SelectedName = EqualizerPresets.DefaultName;
        LoadCustom();
    }

    public event Action? Changed;

    public bool Enabled { get; private set; }

    public string SelectedName { get; private set; }

    public IReadOnlyList<double> Bands => _bands;

    public IEnumerable<EqualizerPreset> AllPresets => EqualizerPresets.BuiltIn.Concat(_custom);

    public bool CanSaveSelected => !EqualizerPresets.IsBuiltIn(SelectedName);

    public bool CanDeleteSelected => CanSaveSelected;

    public void SetEnabled(bool value)
    {
        if (Enabled == value)
        {
            return;
        }

        Enabled = value;
        Changed?.Invoke();
    }

    public void SetBand(int index, double value, bool notify = true)
    {
        if (index < 0 || index >= _bands.Length)
        {
            return;
        }

        var clamped = EqualizerSpec.ClampUi(value);
        if (Math.Abs(_bands[index] - clamped) < 0.01)
        {
            return;
        }

        _bands[index] = clamped;
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    public void SelectPreset(string name)
    {
        var preset = Find(name);
        if (preset is null)
        {
            return;
        }

        CopyBands(preset.Bands);
        SelectedName = preset.Name;
        Changed?.Invoke();
    }

    public bool AddPreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || EqualizerPresets.IsBuiltIn(name))
        {
            return false;
        }

        var existing = _custom.FindIndex(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        var preset = new EqualizerPreset(name, _bands, builtIn: false);
        if (existing >= 0)
        {
            _custom[existing] = preset;
        }
        else
        {
            _custom.Add(preset);
        }

        SelectedName = preset.Name;
        SaveCustom();
        Changed?.Invoke();
        return true;
    }

    public bool SaveSelected()
    {
        if (!CanSaveSelected)
        {
            return false;
        }

        var index = _custom.FindIndex(item => string.Equals(item.Name, SelectedName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _custom[index] = new EqualizerPreset(SelectedName, _bands, builtIn: false);
        SaveCustom();
        Changed?.Invoke();
        return true;
    }

    public bool DeleteSelected()
    {
        if (!CanDeleteSelected)
        {
            return false;
        }

        _custom.RemoveAll(item => string.Equals(item.Name, SelectedName, StringComparison.OrdinalIgnoreCase));
        SaveCustom();
        SelectPreset(EqualizerPresets.DefaultName);
        return true;
    }

    public void LoadDefault() => SelectPreset(EqualizerPresets.DefaultName);

    public string FilterGraph()
    {
        if (!Enabled)
        {
            return string.Empty;
        }

        var parts = new string[EqualizerSpec.BandCount];
        for (var i = 0; i < parts.Length; i++)
        {
            var db = EqualizerSpec.ToDb(_bands[i]).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            parts[i] = $"equalizer=f={EqualizerSpec.FrequenciesHz[i]}:t=o:w=1:g={db}";
        }

        return "lavfi=[" + string.Join(",", parts) + "]";
    }

    private EqualizerPreset? Find(string name) =>
        AllPresets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    private void CopyBands(IReadOnlyList<double> source)
    {
        for (var i = 0; i < _bands.Length; i++)
        {
            _bands[i] = EqualizerSpec.ClampUi(source[i]);
        }
    }

    private void LoadCustom()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            var json = File.ReadAllText(_storePath);
            var file = JsonSerializer.Deserialize<StoreFile>(json);
            if (file?.Presets is null)
            {
                return;
            }

            foreach (var entry in file.Presets)
            {
                if (string.IsNullOrWhiteSpace(entry.Name) ||
                    entry.Bands is null ||
                    entry.Bands.Length != EqualizerSpec.BandCount ||
                    EqualizerPresets.IsBuiltIn(entry.Name))
                {
                    continue;
                }

                _custom.Add(new EqualizerPreset(entry.Name, entry.Bands, builtIn: false));
            }
        }
        catch (Exception)
        {
        }
    }

    private void SaveCustom()
    {
        try
        {
            var folder = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var file = new StoreFile
            {
                Presets = _custom.Select(item => new StorePreset
                {
                    Name = item.Name,
                    Bands = item.Bands.ToArray()
                }).ToList()
            };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(file));
        }
        catch (Exception)
        {
        }
    }

    private sealed class StoreFile
    {
        public List<StorePreset> Presets { get; set; } = [];
    }

    private sealed class StorePreset
    {
        public string Name { get; set; } = "";
        public double[] Bands { get; set; } = [];
    }
}
