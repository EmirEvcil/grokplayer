using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Grok.Player.App.Controls;

public sealed class HandCursorHost : Grid
{
    public HandCursorHost()
    {
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

public sealed class ArrowCursorHost : Grid
{
    public ArrowCursorHost()
    {
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }
}
