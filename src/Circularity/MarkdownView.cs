using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Circularity;

/// <summary>
/// Shows Markdown (a GitHub release's notes) as WPF text: headings, paragraphs, bold, italic,
/// strikethrough, code, bullet and numbered lists, quotes, code blocks and links. Links open in the
/// browser, and only web ones; anything else is shown as its text.
/// </summary>
internal static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseEmphasisExtras().UseAutoLinks().Build();
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Brush CodeBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));

    /// <param name="skipHeading">Leaves out a first heading containing this (the version, already in the window's title).</param>
    public static FrameworkElement Render(string markdown, string? skipHeading = null)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel();
        bool first = true;
        foreach (var block in document)
        {
            if (first && skipHeading != null && block is HeadingBlock heading && PlainText(heading.Inline).Contains(skipHeading, StringComparison.OrdinalIgnoreCase))
            {
                first = false;
                continue;
            }
            first = false;
            if (Block(block) is { } element)
                panel.Children.Add(element);
        }
        // No gap above the first thing shown.
        if (panel.Children.Count > 0 && panel.Children[0] is FrameworkElement top)
            top.Margin = new Thickness(top.Margin.Left, 0, top.Margin.Right, top.Margin.Bottom);
        return panel;
    }

    private static FrameworkElement? Block(Markdig.Syntax.Block block) => block switch
    {
        HeadingBlock heading => Text(heading.Inline, block =>
        {
            block.FontWeight = FontWeights.SemiBold;
            block.FontSize = heading.Level <= 2 ? 16 : 14;
            block.Margin = new Thickness(0, 12, 0, 4);
        }),
        ParagraphBlock paragraph => Text(paragraph.Inline, block => block.Margin = new Thickness(0, 0, 0, 8)),
        ListBlock list => List(list),
        QuoteBlock quote => new Border
        {
            BorderBrush = CodeBackground,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0.8,
            Child = Blocks(quote),
        },
        CodeBlock code => new Border
        {
            Background = CodeBackground,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { Text = code.Lines.ToString().TrimEnd(), FontFamily = Mono, TextWrapping = TextWrapping.Wrap },
        },
        ThematicBreakBlock => new Separator { Margin = new Thickness(0, 6, 0, 10), Opacity = 0.5 },
        // HTML and anything unusual: shown as written rather than dropped.
        LeafBlock leaf when leaf.Lines.Count > 0 => new TextBlock { Text = leaf.Lines.ToString().Trim(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) },
        _ => null,
    };

    private static StackPanel Blocks(ContainerBlock container)
    {
        var panel = new StackPanel();
        foreach (var child in container)
        {
            if (Block(child) is { } element)
                panel.Children.Add(element);
        }
        return panel;
    }

    private static FrameworkElement List(ListBlock list)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int number = int.TryParse(list.OrderedStart, out int start) ? start : 1;
        int row = 0;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var marker = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", Margin = new Thickness(2, 0, 8, 0), Opacity = 0.7 };
            var content = Blocks(item);
            // A list item's paragraphs sit closer together than the page's.
            foreach (var paragraph in content.Children.OfType<TextBlock>())
                paragraph.Margin = new Thickness(0, 0, 0, 3);
            Grid.SetRow(marker, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(marker);
            grid.Children.Add(content);
            row++;
        }
        return grid;
    }

    private static TextBlock Text(ContainerInline? inlines, Action<TextBlock> style)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (inlines != null)
            AddInlines(block.Inlines, inlines);
        style(block);
        return block;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
            target.Add(ToWpf(inline));
    }

    private static System.Windows.Documents.Inline ToWpf(Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case CodeInline code:
                return new Run(code.Content) { FontFamily = Mono, Background = CodeBackground };
            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");
            case EmphasisInline emphasis:
            {
                var span = new Span();
                AddInlines(span.Inlines, emphasis);
                if (emphasis.DelimiterChar == '~')
                    span.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2)
                    span.FontWeight = FontWeights.SemiBold;
                else
                    span.FontStyle = FontStyles.Italic;
                return span;
            }
            case LinkInline { IsImage: true } image:
                // No pictures in the window; their description stands in for them.
                return new Run(PlainText(image));
            case LinkInline link:
                return Link(link.Url, span => AddInlines(span.Inlines, link));
            case AutolinkInline autolink:
                return Link(autolink.Url, span => span.Inlines.Add(new Run(autolink.Url)));
            case ContainerInline container:
            {
                var span = new Span();
                AddInlines(span.Inlines, container);
                return span;
            }
            default:
                // HTML tags and the like: left out.
                return new Run();
        }
    }

    /// <summary>A link that opens in the browser, if it's a web address; otherwise just its text.</summary>
    private static Span Link(string? url, Action<Span> fill)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            var plain = new Span();
            fill(plain);
            return plain;
        }
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.ToString() };
        fill(link);
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Write($"Couldn't open {e.Uri}: {ex.Message}");
            }
            e.Handled = true;
        };
        return link;
    }

    private static string PlainText(ContainerInline? inlines) => inlines == null ? "" : string.Concat(inlines.Select(i => i switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        ContainerInline container => PlainText(container),
        _ => "",
    }));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
