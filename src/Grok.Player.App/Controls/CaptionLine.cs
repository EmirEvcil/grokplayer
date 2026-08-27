using Grok.Player.Core.Subtitles;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Grok.Player.App.Controls;

public sealed class CaptionLine : UserControl
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.Register(
        nameof(Spans),
        typeof(object),
        typeof(CaptionLine),
        new PropertyMetadata(null, (sender, _) => ((CaptionLine)sender).Rebuild()));

    private readonly RichTextBlock _text = new()
    {
        IsTextSelectionEnabled = false,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 12,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 230, 234))
    };

    public CaptionLine()
    {
        Content = _text;
        IsHitTestVisible = false;
    }

    public object? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    private void Rebuild()
    {
        _text.Blocks.Clear();
        var paragraph = new Paragraph();
        if (Spans is IEnumerable<CaptionSpan> spans)
        {
            foreach (var span in spans)
            {
                if (span.Text.Length == 0)
                {
                    continue;
                }

                var text = span.Text.Replace('\n', ' ');
                if (span.Quote)
                {
                    text = "“" + text + "”";
                }

                var run = new Run { Text = text };
                if (!string.IsNullOrWhiteSpace(span.Color) && TryColor(span.Color, out var color))
                {
                    run.Foreground = new SolidColorBrush(color);
                }
                else if (span.Mark)
                {
                    run.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 201, 58));
                }

                if (span.Bold)
                {
                    run.FontWeight = FontWeights.Bold;
                }

                if (span.Italic)
                {
                    run.FontStyle = FontStyle.Italic;
                }

                run.TextDecorations = (span.Underline ? TextDecorations.Underline : TextDecorations.None) |
                                      (span.Strike ? TextDecorations.Strikethrough : TextDecorations.None);
                if (span.Pre || span.Code)
                {
                    run.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                }

                if (span.Small || span.Super || span.Sub)
                {
                    run.FontSize = span.Super || span.Sub ? 10 : 11;
                }

                paragraph.Inlines.Add(run);
            }
        }

        if (paragraph.Inlines.Count == 0 && DataContext is SubtitleBrowserWindow.CueRow row)
        {
            paragraph.Inlines.Add(new Run { Text = row.Text });
        }

        _text.Blocks.Add(paragraph);
    }

    private static bool TryColor(string hex, out Windows.UI.Color color)
    {
        color = default;
        if (hex.Length != 7 || hex[0] != '#')
        {
            return false;
        }

        if (!byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        color = Windows.UI.Color.FromArgb(255, r, g, b);
        return true;
    }
}
