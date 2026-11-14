using System.Text;
using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Store;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Viewers;

/// <summary>A Markdown file, read-only, in its own window. Links open in the browser.</summary>
internal sealed partial class MarkdownWindow : Window
{
    public static void Show(AppServices services, ReferenceEntry entry) => WindowFrames.Present(new MarkdownWindow(services, entry));

    private MarkdownWindow(AppServices services, ReferenceEntry entry)
    {
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(App.IconPath);
        WindowFrames.Restore(this, services, "markdown", 820, 900);
        Closed += (_, _) => WindowFrames.Save(this, services, "markdown");
        Title = ReferenceText.FallbackTitle(entry);

        var text = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 760, LineHeight = 22 };
        var path = entry.Asset is { } asset ? services.Location.FullPath(asset.OriginalRelativePath) : null;
        if (path is null || !File.Exists(path))
        {
            text.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "The saved copy of this file is missing from the library." } } });
        }
        else
        {
            try
            {
                MarkdownRenderer.Render(File.ReadAllText(path, Encoding.UTF8), text);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                text.Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"The file couldn't be read ({error.Message})." } } });
            }
        }
        Content = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(40, 28, 40, 40), Child = text, HorizontalAlignment = HorizontalAlignment.Center },
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
        };
    }
}

/// <summary>
/// Draws Markdown with WinUI's own text elements: system fonts and colours,
/// selectable text, and links that open in the browser. Never runs scripts or
/// shows HTML; pictures show as their description.
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseAutoLinks().UseTaskLists().Build();

    private static readonly FontFamily Code = new("Cascadia Mono, Consolas, Courier New");

    public static void Render(string markdown, RichTextBlock target)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var block in document) AddBlock(block, target, indent: 0);
    }

    private static void AddBlock(Markdig.Syntax.Block block, RichTextBlock target, double indent, string? bullet = null)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Paragraph(indent, top: heading.Level <= 2 ? 18 : 12);
                title.FontSize = heading.Level switch { 1 => 28, 2 => 22, 3 => 18, _ => 15 };
                title.FontWeight = FontWeights.SemiBold;
                AddInlines(heading.Inline, title.Inlines);
                target.Blocks.Add(title);
                break;

            case ParagraphBlock paragraph:
                var text = Paragraph(indent);
                if (bullet is not null) text.Inlines.Add(new Run { Text = bullet });
                AddInlines(paragraph.Inline, text.Inlines);
                target.Blocks.Add(text);
                break;

            case ListBlock list:
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var first = true;
                    foreach (var child in item)
                    {
                        var mark = first ? (list.IsOrdered ? $"{number}.  " : "•  ") : null;
                        AddBlock(child, target, indent + 20, mark);
                        first = false;
                    }
                    number++;
                }
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    var before = target.Blocks.Count;
                    AddBlock(child, target, indent + 20);
                    foreach (var added in target.Blocks.Skip(before)) added.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                }
                break;

            case CodeBlock code:
                var lines = Paragraph(indent + 12);
                lines.FontFamily = Code;
                lines.FontSize = 13;
                var all = code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString()).ToList();
                for (var i = 0; i < all.Count; i++)
                {
                    if (i > 0) lines.Inlines.Add(new LineBreak());
                    lines.Inlines.Add(new Run { Text = all[i] });
                }
                target.Blocks.Add(lines);
                break;

            case ThematicBreakBlock:
                target.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(indent, 8, 0, 8),
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Inlines = { new Run { Text = new string('─', 40) } },
                });
                break;

            case Table table:
                foreach (var row in table.OfType<TableRow>())
                {
                    var line = Paragraph(indent, top: 2);
                    if (row.IsHeader) line.FontWeight = FontWeights.SemiBold;
                    var firstCell = true;
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        if (!firstCell) line.Inlines.Add(new Run { Text = "   │   ", Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
                        foreach (var inner in cell.OfType<ParagraphBlock>()) AddInlines(inner.Inline, line.Inlines);
                        firstCell = false;
                    }
                    target.Blocks.Add(line);
                }
                break;

            case ContainerBlock container:
                foreach (var child in container) AddBlock(child, target, indent);
                break;

            case LeafBlock leaf when leaf.Inline is not null:
                var plain = Paragraph(indent);
                AddInlines(leaf.Inline, plain.Inlines);
                target.Blocks.Add(plain);
                break;
        }
    }

    private static Paragraph Paragraph(double indent, double top = 6) => new() { Margin = new Thickness(indent, top, 0, 4) };

    private static void AddInlines(ContainerInline? container, InlineCollection target)
    {
        if (container is null) return;
        foreach (var inline in container) AddInline(inline, target);
    }

    private static void AddInline(Markdig.Syntax.Inlines.Inline inline, InlineCollection target)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run { Text = literal.Content.ToString() });
                break;
            case CodeInline code:
                target.Add(new Run { Text = code.Content, FontFamily = Code });
                break;
            case LineBreakInline lineBreak:
                if (lineBreak.IsHard) target.Add(new LineBreak()); else target.Add(new Run { Text = " " });
                break;
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(emphasis, span.Inlines);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } image:
                // Pictures aren't fetched: their description shows instead.
                var alt = string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                target.Add(new Run { Text = $"[{(alt.Length > 0 ? alt : "Image")}]", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                break;
            case LinkInline link:
                var hyperlink = new Hyperlink();
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https") hyperlink.NavigateUri = url;
                AddInlines(link, hyperlink.Inlines);
                if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run { Text = link.Url ?? "" });
                target.Add(hyperlink);
                break;
            case AutolinkInline autolink:
                var auto = new Hyperlink { Inlines = { new Run { Text = autolink.Url } } };
                if (!autolink.IsEmail && Uri.TryCreate(autolink.Url, UriKind.Absolute, out var autoUrl) && autoUrl.Scheme is "http" or "https") auto.NavigateUri = autoUrl;
                target.Add(auto);
                break;
            case HtmlInline or HtmlEntityInline:
                if (inline is HtmlEntityInline entity) target.Add(new Run { Text = entity.Transcoded.ToString() });
                break;
            case ContainerInline container:
                AddInlines(container, target);
                break;
        }
    }
}
