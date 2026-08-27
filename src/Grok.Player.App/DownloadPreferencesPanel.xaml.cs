using Grok.Player.Core.Download;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Grok.Player.App;

public sealed partial class DownloadPreferencesPanel : UserControl
{
    private bool _syncing;

    public DownloadPreferencesPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => Bind();
    }

    public void Bind()
    {
        _syncing = true;
        var settings = App.Downloads.Settings;
        FolderBox.Text = settings.Folder;
        HeightBox.Items.Clear();
        foreach (var item in new[] { "360p", "480p", "720p", "1080p", "1440p", "2160p", "Best" })
        {
            HeightBox.Items.Add(item);
        }

        HeightBox.SelectedItem = settings.MaxHeight == 0 ? "Best" : settings.MaxHeight + "p";
        ParallelBox.Items.Clear();
        foreach (var n in new[] { "1", "2", "3", "4" })
        {
            ParallelBox.Items.Add(n);
        }

        ParallelBox.SelectedItem = settings.MaxParallel.ToString();
        ContainerBox.Items.Clear();
        foreach (var item in new[] { "MP4", "MKV", "TS" })
        {
            ContainerBox.Items.Add(item);
        }

        ContainerBox.SelectedItem = settings.Container.ToUpperInvariant();
        _syncing = false;
    }

    private void FolderBox_LostFocus(object sender, RoutedEventArgs e) => SaveFolder(FolderBox.Text);

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(App.Main);
        if (hwnd != 0)
        {
            InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        FolderBox.Text = folder.Path;
        SaveFolder(folder.Path);
    }

    private void HeightBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || HeightBox.SelectedItem is not string text)
        {
            return;
        }

        App.Downloads.Settings.MaxHeight = text == "Best" ? 0 : int.Parse(text.TrimEnd('p'));
        App.Downloads.Settings.Save();
    }

    private void ParallelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ParallelBox.SelectedItem is not string text || !int.TryParse(text, out var n))
        {
            return;
        }

        App.Downloads.Settings.MaxParallel = n;
        App.Downloads.Settings.Save();
    }

    private void ContainerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ContainerBox.SelectedItem is not string text)
        {
            return;
        }

        App.Downloads.Settings.Container = DownloadSettings.NormalizeContainer(text);
        App.Downloads.Settings.Save();
    }

    private void SaveFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        App.Downloads.Settings.Folder = folder.Trim();
        App.Downloads.Settings.Save();
    }
}
