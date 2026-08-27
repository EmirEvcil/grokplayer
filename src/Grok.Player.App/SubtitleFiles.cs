using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Grok.Player.App;

internal static class SubtitleFiles
{
    public static async Task<string?> PickAsync(nint hwnd)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeFilter.Add(".srt");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
