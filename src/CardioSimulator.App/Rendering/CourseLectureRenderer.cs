using System.Collections.Generic;
using System.Linq;
using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace CardioSimulator.App.Rendering;

/// <summary>
/// Renders a parsed <see cref="Lecture"/> into a WinUI UIElement tree. Each <see cref="CourseBlock"/>
/// becomes its own block: Markdown is parsed by Markdig and walked into TextBlocks with inline
/// formatting; EcgEmbed becomes a single-lead <see cref="EcgMonitorControl"/>; EditableTable
/// becomes a read-only Grid. KaTeX math (`$...$`) is currently passed through as plain text;
/// the WebView2-based renderer is a P10 follow-up.
/// </summary>
public static class CourseLectureRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>Builds a scrollable rendering of <paramref name="lecture"/>.</summary>
    public static UIElement Render(Lecture lecture, AppViewModel appVm)
    {
        var stack = new StackPanel { Padding = new Thickness(24, 16, 24, 24), Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(lecture.FrontMatter.Title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = lecture.FrontMatter.Title,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }
        foreach (var block in lecture.Blocks)
        {
            switch (block)
            {
                case CourseBlock.Markdown md:
                    stack.Children.Add(RenderMarkdown(md.Text));
                    break;
                case CourseBlock.EcgEmbed ecg:
                    stack.Children.Add(RenderEcgEmbed(ecg, appVm));
                    break;
                case CourseBlock.EditableTable table:
                    stack.Children.Add(RenderEditableTable(table));
                    break;
            }
        }
        return new ScrollViewer
        {
            Content = stack,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    // ── Markdown → WinUI ────────────────────────────────────────────────────

    /// <summary>
    /// Renders raw Markdown as a scrollable UI element (no EcgEmbed/EditableTable). Used by the
    /// constructor's live preview while the author types.
    /// </summary>
    public static UIElement RenderMarkdownText(string text) => new ScrollViewer
    {
        Content = RenderMarkdown(text),
        VerticalScrollMode = ScrollMode.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static UIElement RenderMarkdown(string text)
    {
        var doc = Markdown.Parse(text, Pipeline);
        var stack = new StackPanel { Spacing = 8 };
        foreach (var block in doc)
        {
            var element = RenderMarkdownBlock(block);
            if (element is not null) stack.Children.Add(element);
        }
        return stack;
    }

    private static UIElement? RenderMarkdownBlock(MdBlock block) => block switch
    {
        HeadingBlock h => RenderHeading(h),
        ParagraphBlock p => RenderParagraph(p),
        ListBlock l => RenderList(l),
        QuoteBlock q => RenderQuote(q),
        FencedCodeBlock c => RenderCodeBlock(c),
        CodeBlock c => RenderCodeBlock(c),
        ThematicBreakBlock => new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Colors.LightGray),
            Margin = new Thickness(0, 6, 0, 6),
        },
        Table table => RenderGfmTable(table),
        _ => null,
    };

    private static UIElement RenderHeading(HeadingBlock h)
    {
        var size = h.Level switch
        {
            1 => 22.0,
            2 => 19.0,
            3 => 17.0,
            _ => 15.0,
        };
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4),
        };
        AppendInlines(tb.Inlines, h.Inline);
        return tb;
    }

    private static UIElement RenderParagraph(ParagraphBlock p)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AppendInlines(tb.Inlines, p.Inline);
        return tb;
    }

    private static UIElement RenderList(ListBlock list)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 0, 0) };
        var index = 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock li) continue;
            var bullet = list.IsOrdered ? $"{index}." : "•";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = bullet, MinWidth = 18 });
            var inner = new StackPanel { Spacing = 4 };
            foreach (var b in li)
            {
                var rendered = RenderMarkdownBlock(b);
                if (rendered is not null) inner.Children.Add(rendered);
            }
            row.Children.Add(inner);
            stack.Children.Add(row);
            index++;
        }
        return stack;
    }

    private static UIElement RenderQuote(QuoteBlock q)
    {
        var inner = new StackPanel { Spacing = 4 };
        foreach (var b in q)
        {
            var rendered = RenderMarkdownBlock(b);
            if (rendered is not null) inner.Children.Add(rendered);
        }
        return new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 8, 4),
            Child = inner,
        };
    }

    private static UIElement RenderCodeBlock(CodeBlock code)
    {
        var text = string.Join("\n", code.Lines.Lines.Select(l => l.ToString()));
        return new Border
        {
            Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0xF4, G = 0xF4, B = 0xF6 }),
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap,
            },
        };
    }

    private static UIElement RenderGfmTable(Table table)
    {
        var rows = new List<List<string>>();
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row) continue;
            var cells = new List<string>();
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell) continue;
                cells.Add(BlockToPlainText(cell));
            }
            rows.Add(cells);
        }
        return BuildTableGrid(rows, headerRow: 0);
    }

    private static string BlockToPlainText(ContainerBlock container)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in container)
        {
            if (b is ParagraphBlock p && p.Inline is not null)
            {
                foreach (var i in p.Inline) sb.Append(InlineToPlainText(i));
            }
        }
        return sb.ToString();
    }

    private static string InlineToPlainText(MdInline inline) => inline switch
    {
        LiteralInline lit => lit.Content.ToString(),
        CodeInline ci => ci.Content,
        EmphasisInline em => string.Concat(em.Select(InlineToPlainText)),
        LinkInline link => string.Concat(link.Select(InlineToPlainText)),
        LineBreakInline => "\n",
        _ => string.Empty,
    };

    private static void AppendInlines(InlineCollection collection, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            AppendInline(collection, inline);
        }
    }

    private static void AppendInline(InlineCollection collection, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                collection.Add(new Run { Text = lit.Content.ToString() });
                break;
            case CodeInline code:
                collection.Add(new Run { Text = code.Content, FontFamily = new FontFamily("Consolas") });
                break;
            case EmphasisInline em:
                var span = new Span();
                foreach (var child in em) AppendInline(span.Inlines, child);
                if (em.DelimiterCount >= 2)
                {
                    foreach (var child in span.Inlines.OfType<Run>()) child.FontWeight = FontWeights.Bold;
                }
                else
                {
                    foreach (var child in span.Inlines.OfType<Run>()) child.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }
                collection.Add(span);
                break;
            case LinkInline link:
                var hyperlink = new Hyperlink();
                if (Uri.TryCreate(link.Url ?? string.Empty, UriKind.Absolute, out var uri))
                {
                    hyperlink.NavigateUri = uri;
                }
                foreach (var child in link) AppendInline(hyperlink.Inlines, child);
                collection.Add(hyperlink);
                break;
            case LineBreakInline:
                collection.Add(new LineBreak());
                break;
            default:
                // Fallback: extract any text we can.
                if (inline is ContainerInline ci)
                {
                    foreach (var child in ci) AppendInline(collection, child);
                }
                break;
        }
    }

    // ── Custom blocks ──────────────────────────────────────────────────────

    private static UIElement RenderEcgEmbed(CourseBlock.EcgEmbed ecg, AppViewModel appVm)
    {
        var lead = ecg.Lead ?? Lead.II;
        var points = appVm.Repository.LeadWaveform(ecg.PathologyId, lead);
        var waveforms = points is null
            ? new Dictionary<Lead, Points>()
            : new Dictionary<Lead, Points> { [lead] = points };

        var monitor = new EcgMonitorControl
        {
            Height = 220,
            Mode = new MonitorModeModel(Count: 1, SeriesScheme: SeriesScheme.OneColumn),
            Waveforms = waveforms,
        };

        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 6) };
        if (!string.IsNullOrWhiteSpace(ecg.Caption))
        {
            stack.Children.Add(new TextBlock
            {
                Text = ecg.Caption,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.Gray),
            });
        }
        stack.Children.Add(monitor);
        return stack;
    }

    private static UIElement RenderEditableTable(CourseBlock.EditableTable table)
    {
        var lines = table.Raw.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var rows = lines
            .Where(l => !l.TrimStart().StartsWith("|---") && !l.TrimStart().StartsWith("---"))
            .Select(SplitGfmRow)
            .ToList();
        return BuildTableGrid(rows, headerRow: 0);
    }

    private static List<string> SplitGfmRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|")) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(s => s.Trim()).ToList();
    }

    private static UIElement BuildTableGrid(List<List<string>> rows, int headerRow)
    {
        if (rows.Count == 0) return new TextBlock { Text = "(empty table)" };

        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        var maxCols = rows.Max(r => r.Count);
        for (var c = 0; c < maxCols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cellText = new TextBlock
                {
                    Text = rows[r][c],
                    FontWeight = r == headerRow ? FontWeights.SemiBold : FontWeights.Normal,
                    Padding = new Thickness(6),
                    TextWrapping = TextWrapping.Wrap,
                };
                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Colors.LightGray),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = cellText,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1, 1, 0, 0),
            Child = grid,
        };
    }
}
