using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AdbDesktop
{
    /// <summary>
    /// Renders GitHub release notes into WPF elements for the update prompt. The release
    /// template is a small markdown subset, so this is a line-based renderer rather than a
    /// full markdown engine:
    ///   # heading    -> large title with an underline rule
    ///   ## heading   -> smaller title with a thinner underline rule
    ///   - item / *   -> bullet rows (leading markers may stack, e.g. "- * PR by ...")
    ///   **bold**     -> bold inline
    ///   [text](url)  -> clickable link (theme accent)
    ///   bare https:// URLs -> clickable link
    ///   @person      -> orange link to that user's GitHub profile
    /// Anything unrecognized falls through as a plain wrapped paragraph, and any parse
    /// failure falls back to showing the raw text, so notes are never lost.
    /// </summary>
    internal static class PatchNotesRenderer
    {
        private static readonly Brush MentionBrush = CreateFrozen(Color.FromRgb(0xF0, 0x88, 0x3E));

        private static Brush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // One pass over the text finds every inline token; plain runs fill the gaps.
        // Alternation order matters: markdown links before bare URLs, bold before both
        // asterisk-adjacent cases.
        private static readonly Regex InlineToken = new Regex(
            @"\[(?<linkText>[^\]]+)\]\((?<linkHref>https?://[^\s)]+)\)" +
            @"|\*\*(?<boldText>.+?)\*\*" +
            @"|(?<url>https?://[^\s<>)\]]+)" +
            @"|(?<mention>@[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38})",
            RegexOptions.Compiled);

        public static FrameworkElement Render(string notes)
        {
            try
            {
                return RenderCore(notes ?? string.Empty);
            }
            catch
            {
                return new TextBlock { Text = notes ?? string.Empty, TextWrapping = TextWrapping.Wrap };
            }
        }

        private static FrameworkElement RenderCore(string notes)
        {
            var panel = new StackPanel();
            bool first = true;

            foreach (var raw in notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue; // spacing comes from element margins

                if (line.StartsWith("## ", StringComparison.Ordinal))
                    AddHeading(panel, line.Substring(3).Trim(), level: 2, first);
                else if (line.StartsWith("# ", StringComparison.Ordinal))
                    AddHeading(panel, line.Substring(2).Trim(), level: 1, first);
                else if (IsBullet(line))
                    AddBullet(panel, StripBulletMarkers(line));
                else
                    AddParagraph(panel, line);

                first = false;
            }

            if (panel.Children.Count == 0)
                panel.Children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap });

            return panel;
        }

        private static bool IsBullet(string line)
            => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);

        // The "What's Changed" section GitHub generates can end up as "- * PR by ..."
        // in the template, so stacked leading markers are all stripped.
        private static string StripBulletMarkers(string line)
        {
            while (IsBullet(line))
                line = line.Substring(2).TrimStart();
            return line;
        }

        private static void AddHeading(StackPanel panel, string text, int level, bool first)
        {
            var tb = new TextBlock
            {
                FontSize = level == 1 ? 16 : 13.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, first ? 0 : (level == 1 ? 12 : 8), 0, 0)
            };
            AppendInlines(tb.Inlines, text);
            panel.Children.Add(tb);

            var rule = new Rectangle
            {
                Height = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Opacity = level == 1 ? 0.35 : 0.2,
                Margin = new Thickness(0, 3, 0, level == 1 ? 8 : 6)
            };
            rule.SetResourceReference(Shape.FillProperty, "TextBrush");
            panel.Children.Add(rule);
        }

        private static void AddBullet(StackPanel panel, string text)
        {
            var grid = new Grid { Margin = new Thickness(6, 0, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 7, 0) };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AppendInlines(tb.Inlines, text);
            Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);

            panel.Children.Add(grid);
        }

        private static void AddParagraph(StackPanel panel, string text)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            AppendInlines(tb.Inlines, text);
            panel.Children.Add(tb);
        }

        private static void AppendInlines(InlineCollection inlines, string text)
        {
            int pos = 0;
            foreach (Match m in InlineToken.Matches(text))
            {
                if (m.Index > pos)
                    inlines.Add(new Run(text.Substring(pos, m.Index - pos)));

                if (m.Groups["linkHref"].Success)
                {
                    inlines.Add(MakeLink(m.Groups["linkText"].Value, m.Groups["linkHref"].Value));
                }
                else if (m.Groups["boldText"].Success)
                {
                    var bold = new Bold();
                    AppendInlines(bold.Inlines, m.Groups["boldText"].Value);
                    inlines.Add(bold);
                }
                else if (m.Groups["url"].Success)
                {
                    // Trailing sentence punctuation isn't part of the URL.
                    string url = m.Groups["url"].Value;
                    string trimmed = url.TrimEnd('.', ',', ';', ':');
                    inlines.Add(MakeLink(trimmed, trimmed));
                    if (trimmed.Length < url.Length)
                        inlines.Add(new Run(url.Substring(trimmed.Length)));
                }
                else if (m.Groups["mention"].Success)
                {
                    inlines.Add(MakeMention(m.Value));
                }

                pos = m.Index + m.Length;
            }

            if (pos < text.Length)
                inlines.Add(new Run(text.Substring(pos)));
        }

        private static Inline MakeLink(string text, string url)
        {
            var link = new Hyperlink(new Run(text)) { ToolTip = url };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            WireNavigate(link, url);
            return link;
        }

        private static Inline MakeMention(string mention)
        {
            var link = new Hyperlink(new Run(mention))
            {
                Foreground = MentionBrush,
                TextDecorations = null
            };
            string url = "https://github.com/" + mention.TrimStart('@');
            link.ToolTip = url;
            WireNavigate(link, url);
            return link;
        }

        private static void WireNavigate(Hyperlink link, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;
            link.NavigateUri = uri;
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { }
                e.Handled = true;
            };
        }
    }
}
