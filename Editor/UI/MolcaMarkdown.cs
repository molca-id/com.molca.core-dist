using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.UI
{
    /// <summary>Structured block kinds produced from Markdown text for the UI Toolkit renderer.</summary>
    public enum MolcaMarkdownBlockKind
    {
        /// <summary>Regular prose.</summary>
        Paragraph,
        /// <summary>A short Markdown heading (<c>#</c>/<c>##</c>).</summary>
        Heading,
        /// <summary>An unordered list item.</summary>
        Bullet,
        /// <summary>An ordered list item.</summary>
        Numbered,
        /// <summary>A fenced code block.</summary>
        Code,
        /// <summary>
        /// A fenced diagram block (an <c>info string</c> of <c>mermaid</c>); its source is in
        /// <see cref="MolcaMarkdownBlock.Text"/> and rendered by <see cref="MolcaMermaid"/>.
        /// </summary>
        Diagram,
        /// <summary>A warning line (<c>Warning: ...</c>).</summary>
        Warning,
        /// <summary>An error line (<c>Error: ...</c>).</summary>
        Error,
        /// <summary>A blockquote line/paragraph (<c>&gt; ...</c>).</summary>
        Quote,
        /// <summary>A task-list item (<c>- [ ] ...</c> / <c>- [x] ...</c>); see <see cref="MolcaMarkdownBlock.Checked"/>.</summary>
        Task,
        /// <summary>A simple Markdown table; rows are in <see cref="MolcaMarkdownBlock.TableRows"/> (row 0 = header).</summary>
        Table,
        /// <summary>A horizontal rule (<c>---</c> / <c>***</c> / <c>___</c>).</summary>
        Rule
    }

    /// <summary>One parsed, renderable Markdown block.</summary>
    public sealed class MolcaMarkdownBlock
    {
        /// <summary>The block type.</summary>
        public MolcaMarkdownBlockKind Kind { get; }

        /// <summary>The visible block text, with inline Markdown markers stripped (for plain-text/copy use).</summary>
        public string Text { get; }

        /// <summary>
        /// The block text with inline Markdown markers preserved, for the inline-span renderer. Equal to
        /// <see cref="Text"/> when there is nothing to tokenize (e.g. code blocks).
        /// </summary>
        public string RawText { get; }

        /// <summary>The list number for <see cref="MolcaMarkdownBlockKind.Numbered"/> blocks.</summary>
        public int Number { get; }

        /// <summary>For <see cref="MolcaMarkdownBlockKind.Task"/>: whether the task box is checked.</summary>
        public bool Checked { get; }

        /// <summary>
        /// For <see cref="MolcaMarkdownBlockKind.Table"/>: the table rows, each a list of cell strings.
        /// Row 0 is the header. Each cell's inline markers are preserved for the inline-span renderer.
        /// <c>null</c> for non-table blocks.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<string>> TableRows { get; }

        /// <summary>Creates a block whose raw and cleaned text are identical.</summary>
        public MolcaMarkdownBlock(MolcaMarkdownBlockKind kind, string text, int number = 0)
            : this(kind, text, text, number)
        {
        }

        /// <summary>Creates a block, preserving the raw (un-stripped) text for inline rendering.</summary>
        public MolcaMarkdownBlock(MolcaMarkdownBlockKind kind, string text, string rawText, int number)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            RawText = rawText ?? Text;
            Number = number;
        }

        /// <summary>Creates a task-list block carrying its checked state.</summary>
        public MolcaMarkdownBlock(MolcaMarkdownBlockKind kind, string text, string rawText, bool isChecked)
            : this(kind, text, rawText, 0)
        {
            Checked = isChecked;
        }

        /// <summary>Creates a table block from its parsed rows (row 0 = header).</summary>
        public MolcaMarkdownBlock(IReadOnlyList<IReadOnlyList<string>> tableRows)
        {
            Kind = MolcaMarkdownBlockKind.Table;
            Text = string.Empty;
            RawText = string.Empty;
            TableRows = tableRows;
        }
    }

    /// <summary>The kind of an inline run within a Markdown block.</summary>
    public enum MolcaMarkdownInlineKind
    {
        /// <summary>Plain text.</summary>
        Text,
        /// <summary>An <c>`inline code`</c> run.</summary>
        Code,
        /// <summary>A <c>**bold**</c> run.</summary>
        Bold,
        /// <summary>An <c>_italic_</c> run.</summary>
        Italic,
        /// <summary>A clickable file/path link, optionally with a line number.</summary>
        Link,
        /// <summary>A clickable <c>http</c>/<c>https</c> web link (opens via <c>Application.OpenURL</c>).</summary>
        Url,
        /// <summary>
        /// A clickable custom-scheme action link (e.g. <c>molca-context://...</c>), produced only when the
        /// caller opts in via <see cref="MolcaMarkdownOptions.ActionScheme"/>. Dispatched to
        /// <see cref="MolcaMarkdownOptions.OnAction"/>. See <see cref="MolcaMarkdownInline.ActionUri"/>.
        /// </summary>
        Action
    }

    /// <summary>One inline run produced by <see cref="MolcaMarkdown.ParseInline"/>.</summary>
    public sealed class MolcaMarkdownInline
    {
        /// <summary>The run kind.</summary>
        public MolcaMarkdownInlineKind Kind { get; }

        /// <summary>The visible text of the run.</summary>
        public string Text { get; }

        /// <summary>
        /// For <see cref="MolcaMarkdownInlineKind.Link"/>: the file/asset path (no line suffix). For
        /// <see cref="MolcaMarkdownInlineKind.Url"/>: the web target.
        /// </summary>
        public string LinkPath { get; }

        /// <summary>For <see cref="MolcaMarkdownInlineKind.Link"/>: the 1-based line number, or 0 if none.</summary>
        public int LinkLine { get; }

        /// <summary>For <see cref="MolcaMarkdownInlineKind.Action"/>: the custom-scheme action URI.</summary>
        public string ActionUri { get; }

        /// <summary>Creates an inline run.</summary>
        public MolcaMarkdownInline(MolcaMarkdownInlineKind kind, string text, string linkPath = null, int linkLine = 0, string actionUri = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            LinkPath = linkPath;
            LinkLine = linkLine;
            ActionUri = actionUri;
        }
    }

    /// <summary>Text-tint variant applied to a whole rendered Markdown block.</summary>
    public enum MolcaMarkdownVariant
    {
        /// <summary>Default label color for the surface.</summary>
        Default,
        /// <summary>Muted/secondary text (e.g. tool output).</summary>
        Muted,
        /// <summary>Error text.</summary>
        Error
    }

    /// <summary>
    /// Optional behavior for <see cref="MolcaMarkdown.Render(VisualElement, string, MolcaMarkdownOptions)"/>:
    /// text tint, link handling, and an opt-in custom action-link scheme. All members are optional; the
    /// defaults render plain Markdown with file links (open the asset) and web links (open the browser).
    /// </summary>
    public sealed class MolcaMarkdownOptions
    {
        /// <summary>Tint applied to every span and marker in the rendered block(s).</summary>
        public MolcaMarkdownVariant Variant = MolcaMarkdownVariant.Default;

        /// <summary>
        /// When set (e.g. <c>"molca-context://"</c>), a <c>[label](scheme...)</c> link whose target starts
        /// with this scheme becomes a clickable <see cref="MolcaMarkdownInlineKind.Action"/> run dispatched
        /// to <see cref="OnAction"/>. When <c>null</c>, such links degrade to plain text (standard Markdown
        /// behavior for unrecognized schemes).
        /// </summary>
        public string ActionScheme;

        /// <summary>Invoked with the full action URI when an <see cref="MolcaMarkdownInlineKind.Action"/> run is clicked.</summary>
        public Action<string> OnAction;

        /// <summary>Optional tooltip shown on <see cref="MolcaMarkdownInlineKind.Action"/> runs (explains what clicking does).</summary>
        public string ActionTooltip;

        /// <summary>
        /// Overrides how a file/path link opens. Receives the path and 1-based line (0 if none). Defaults to
        /// <see cref="MolcaMarkdown.OpenFile"/> (open the asset, or the external file at the line).
        /// </summary>
        public Action<string, int> OnOpenFile;

        /// <summary>Overrides how a web link opens. Defaults to <see cref="Application.OpenURL"/>.</summary>
        public Action<string> OnOpenUrl;
    }

    /// <summary>
    /// A small, deterministic, dependency-free Markdown renderer for Molca editor surfaces. It parses the
    /// subset of Markdown the editor UI needs — headings, ordered/unordered lists, task lists, fenced code,
    /// inline <c>`code`</c>/<c>**bold**</c>/<c>_italic_</c>, blockquotes, tables, horizontal rules, and
    /// <c>file:line</c>/web/custom-scheme links — into <see cref="VisualElement"/> trees.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/UI/</c> (shared editor design layer; forks inherit it).
    /// Styling requires <see cref="MolcaEditorUi.Apply"/> on an ancestor root (the standard contract for all
    /// shared Molca editor UI) so the <c>molca-md-*</c> rules and <c>--molca-*</c> tokens resolve. The
    /// Molca Assistant transcript is one consumer; any Hub or fork surface can call
    /// <see cref="Render(VisualElement, string, MolcaMarkdownOptions)"/> to render Markdown inline. Not
    /// thread-safe; main thread only (builds UI Toolkit elements).
    /// </remarks>
    public static class MolcaMarkdown
    {
        private static readonly Regex BoldRegex = new Regex(
            "\\*\\*([^*]+)\\*\\*",
            RegexOptions.Compiled);

        private static readonly Regex InlineCodeRegex = new Regex(
            "`([^`]+)`",
            RegexOptions.Compiled);

        private static readonly Regex MarkdownLinkRegex = new Regex(
            @"\[(?<label>[^\]\r\n]+)\]\((?<target>[^)\s]+)\)",
            RegexOptions.Compiled);

        private static readonly Regex LinkRegex = new Regex(
            @"(?<path>(?:[A-Za-z]:[\\/])?[\w./\\\-]+\.(?:cs|jsonl|js|ts|tsx|jsx|json|md|asmdef|uxml|uss|shader|cginc|hlsl|txt|yaml|yml|xml|csproj|cs?proj))(?::(?<line>\d+))?",
            RegexOptions.Compiled);

        // ---- Parsing --------------------------------------------------------------------------------

        /// <summary>
        /// Splits a leading YAML front-matter block (a <c>---</c>-fenced set of flat <c>key: value</c>
        /// lines at the very start of the text) from the Markdown body. Only a leading block is treated as
        /// front-matter — a <c>---</c> elsewhere is a normal horizontal rule. The parser is intentionally
        /// minimal (flat scalar keys, no nested YAML) — enough for doc metadata like title/category/order.
        /// </summary>
        /// <param name="text">The raw Markdown source, possibly front-matter-prefixed.</param>
        /// <param name="meta">Receives the parsed keys (lower-cased), or an empty map when there is none.</param>
        /// <returns>The Markdown body with any front-matter block removed; the input unchanged when none.</returns>
        public static string StripFrontMatter(string text, out IReadOnlyDictionary<string, string> meta)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            meta = map;
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return text;

            // Find the closing fence: a line that is exactly "---" after the opening one.
            var lines = normalized.Split('\n');
            var close = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { close = i; break; }
            }
            if (close < 0) return text; // no closing fence — treat the whole thing as body

            for (var i = 1; i < close; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim().Trim('"', '\'');
                if (key.Length > 0) map[key] = value;
            }

            // Return the body after the closing fence, trimming a single leading blank line.
            var start = close + 1;
            if (start < lines.Length && lines[start].Trim().Length == 0) start++;
            return string.Join("\n", lines, start, lines.Length - start);
        }

        /// <summary>Parses Markdown text into renderable blocks.</summary>
        /// <param name="text">The Markdown source.</param>
        /// <returns>Ordered blocks; empty for null/whitespace input.</returns>
        public static IReadOnlyList<MolcaMarkdownBlock> Parse(string text)
        {
            var blocks = new List<MolcaMarkdownBlock>();
            if (string.IsNullOrWhiteSpace(text)) return blocks;

            // Strip a leading YAML front-matter block so its keys are metadata, not rendered content. This
            // must happen before the loop: once inside it, a leading `---` is swallowed as a Rule by IsRule.
            text = StripFrontMatter(text, out _);
            if (string.IsNullOrWhiteSpace(text)) return blocks;

            var paragraph = new StringBuilder();
            var code = new StringBuilder();
            var quote = new StringBuilder();
            var tableLines = new List<string>();
            var inCode = false;
            var codeLang = string.Empty; // fence info string, e.g. "mermaid"; routes the block on close

            // Flushes whichever multi-line accumulator is active before a different block type starts.
            void FlushPending()
            {
                FlushParagraph(blocks, paragraph);
                FlushQuote(blocks, quote);
                FlushTable(blocks, tableLines);
            }

            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.TrimEnd();
                var trimmed = line.Trim();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    if (inCode)
                    {
                        blocks.Add(MakeFencedBlock(code.ToString().TrimEnd('\n'), codeLang));
                        code.Length = 0;
                        codeLang = string.Empty;
                        inCode = false;
                    }
                    else
                    {
                        FlushPending();
                        // The info string after the opening fence selects the block kind on close.
                        codeLang = trimmed.Substring(3).Trim();
                        inCode = true;
                    }
                    continue;
                }

                if (inCode)
                {
                    code.AppendLine(line);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    FlushPending();
                    continue;
                }

                // Blockquote: merge a contiguous run of '>' lines into one Quote block.
                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    FlushParagraph(blocks, paragraph);
                    FlushTable(blocks, tableLines);
                    if (quote.Length > 0) quote.Append(' ');
                    quote.Append(trimmed.Substring(1).Trim());
                    continue;
                }

                // Table: accumulate a contiguous run of pipe rows; validated/emitted on flush.
                if (IsTableRow(trimmed))
                {
                    FlushParagraph(blocks, paragraph);
                    FlushQuote(blocks, quote);
                    tableLines.Add(trimmed);
                    continue;
                }

                // Any non-quote, non-table line ends those runs.
                FlushQuote(blocks, quote);
                FlushTable(blocks, tableLines);

                if (IsRule(trimmed))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(new MolcaMarkdownBlock(MolcaMarkdownBlockKind.Rule, string.Empty));
                    continue;
                }

                if (TryReadTask(trimmed, out var isChecked, out var taskText))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(new MolcaMarkdownBlock(MolcaMarkdownBlockKind.Task, CleanInline(taskText), taskText, isChecked));
                    continue;
                }

                if (TryReadNumbered(trimmed, out var number, out var numberedText))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Numbered, numberedText, number));
                    continue;
                }

                if (TryReadHeading(trimmed, out var headingLevel, out var headingText))
                {
                    FlushParagraph(blocks, paragraph);
                    // The level (1–6) is carried in Number so the renderer can size the heading.
                    blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Heading, headingText, headingLevel));
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Bullet, trimmed.Substring(2).Trim()));
                    continue;
                }

                if (StartsWithLabel(trimmed, "Warning:"))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Warning, trimmed));
                    continue;
                }

                if (StartsWithLabel(trimmed, "Error:"))
                {
                    FlushParagraph(blocks, paragraph);
                    blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Error, trimmed));
                    continue;
                }

                // Accumulate the raw line; inline markers are stripped only when the paragraph is flushed.
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(trimmed);
            }

            if (inCode && code.Length > 0)
                blocks.Add(MakeFencedBlock(code.ToString().TrimEnd('\n'), codeLang));
            FlushPending();
            return blocks;
        }

        /// <summary>
        /// Builds the block for a closed fence: a <see cref="MolcaMarkdownBlockKind.Diagram"/> when the info
        /// string is <c>mermaid</c> (rendered by <see cref="MolcaMermaid"/>), otherwise a plain
        /// <see cref="MolcaMarkdownBlockKind.Code"/> block.
        /// </summary>
        private static MolcaMarkdownBlock MakeFencedBlock(string content, string infoString)
            => string.Equals(infoString, "mermaid", StringComparison.OrdinalIgnoreCase)
                ? new MolcaMarkdownBlock(MolcaMarkdownBlockKind.Diagram, content)
                : new MolcaMarkdownBlock(MolcaMarkdownBlockKind.Code, content);

        /// <summary>
        /// Tokenizes a single block's text into inline runs: <c>`code`</c>, <c>**bold**</c>, <c>_italic_</c>,
        /// <c>file:line</c>/path links, web links, and — when <see cref="MolcaMarkdownOptions.ActionScheme"/>
        /// is set — custom-scheme action links. Deterministic and dependency-free; unrecognized syntax is
        /// left as plain text.
        /// </summary>
        /// <param name="text">The raw block text with inline markers preserved (use <see cref="MolcaMarkdownBlock.RawText"/>).</param>
        /// <param name="options">Optional settings; only <see cref="MolcaMarkdownOptions.ActionScheme"/> affects parsing.</param>
        /// <returns>Ordered inline runs; a single <see cref="MolcaMarkdownInlineKind.Text"/> run for plain input.</returns>
        public static IReadOnlyList<MolcaMarkdownInline> ParseInline(string text, MolcaMarkdownOptions options = null)
        {
            var spans = new List<MolcaMarkdownInline>();
            if (string.IsNullOrEmpty(text)) return spans;

            var actionScheme = options?.ActionScheme;
            var buffer = new StringBuilder();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];

                // [label](scheme...): an opted-in custom scheme becomes an Action run (checked before the
                // general link handling so it isn't degraded to plain text as an unknown scheme).
                if (c == '[' && !string.IsNullOrEmpty(actionScheme)
                    && TryReadMarkdownLink(text, i, out var aLabel, out var aTarget, out var aEnd)
                    && aTarget.StartsWith(actionScheme, StringComparison.Ordinal))
                {
                    FlushTextRun(spans, buffer);
                    spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Action, aLabel, actionUri: aTarget));
                    i = aEnd + 1;
                    continue;
                }

                // [label](path-or-url): web links become Url runs, file paths become Link runs, and an
                // unsafe/unknown target degrades to the plain label text (the link syntax is dropped).
                if (c == '[' && TryReadMarkdownLink(text, i, out var mdLabel, out var mdTarget, out var mdEnd))
                {
                    FlushTextRun(spans, buffer);
                    if (IsWebUrl(mdTarget))
                    {
                        spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Url, mdLabel, linkPath: mdTarget));
                    }
                    else if (TrySplitFileTarget(mdTarget, out var filePath, out var fileLine))
                    {
                        spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Link, mdLabel, filePath, fileLine));
                    }
                    else
                    {
                        buffer.Append(mdLabel); // unknown scheme → plain text, no link
                    }
                    i = mdEnd + 1;
                    continue;
                }

                // `inline code` — content is literal, no nested parsing.
                if (c == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        FlushTextRun(spans, buffer);
                        spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Code, text.Substring(i + 1, end - i - 1)));
                        i = end + 1;
                        continue;
                    }
                }

                // **bold**
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 1)
                    {
                        FlushTextRun(spans, buffer);
                        spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Bold, text.Substring(i + 2, end - i - 2)));
                        i = end + 2;
                        continue;
                    }
                }

                // _italic_ — only at a word boundary so snake_case identifiers are left intact.
                if (c == '_' && IsWordBoundaryBefore(text, i))
                {
                    var end = FindItalicClose(text, i + 1);
                    if (end > i)
                    {
                        FlushTextRun(spans, buffer);
                        spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Italic, text.Substring(i + 1, end - i - 1)));
                        i = end + 1;
                        continue;
                    }
                }

                buffer.Append(c);
                i++;
            }

            FlushTextRun(spans, buffer);
            return spans;
        }

        /// <summary>
        /// Removes lightweight inline Markdown markers that UI Toolkit labels cannot style inline, reducing a
        /// string to its visible plain text (link → label, drop <c>**</c>/<c>`</c>, unescape <c>\_</c>).
        /// </summary>
        public static string CleanInline(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // Reduce a Markdown link to its visible label so copied/plain text isn't cluttered with the
            // target. (Custom-scheme links match this shape too and collapse to their label, the right
            // plain-text fallback for a non-representable action.)
            var clean = MarkdownLinkRegex.Replace(text, "${label}");
            clean = BoldRegex.Replace(clean, "$1");
            clean = InlineCodeRegex.Replace(clean, "$1");
            return clean.Replace("\\_", "_");
        }

        // ---- Rendering ------------------------------------------------------------------------------

        /// <summary>Builds a standalone <see cref="VisualElement"/> rendering <paramref name="text"/> as Markdown.</summary>
        /// <param name="text">The Markdown source.</param>
        /// <param name="options">Optional tint/link behavior.</param>
        /// <returns>A container element ready to add to a UI Toolkit hierarchy.</returns>
        public static VisualElement Create(string text, MolcaMarkdownOptions options = null)
        {
            var container = new VisualElement();
            Render(container, text, options);
            return container;
        }

        /// <summary>Renders <paramref name="text"/> as Markdown into <paramref name="parent"/>.</summary>
        /// <param name="parent">The element to append rendered blocks to.</param>
        /// <param name="text">The Markdown source.</param>
        /// <param name="options">Optional tint/link behavior.</param>
        /// <remarks>
        /// Requires <see cref="MolcaEditorUi.Apply"/> on an ancestor root for styling (the <c>molca-md-*</c>
        /// rules live in the shared components stylesheet).
        /// </remarks>
        public static void Render(VisualElement parent, string text, MolcaMarkdownOptions options = null)
        {
            if (parent == null) return;

            var blocks = Parse(text);
            if (blocks.Count == 0)
            {
                parent.Add(CreateInlineBlock(text, options));
                return;
            }

            foreach (var block in blocks)
            {
                if (block.Kind == MolcaMarkdownBlockKind.Bullet || block.Kind == MolcaMarkdownBlockKind.Numbered)
                {
                    parent.Add(CreateListRow(block, options));
                    continue;
                }

                if (block.Kind == MolcaMarkdownBlockKind.Code)
                {
                    parent.Add(CreateCodeBlock(block.Text));
                    continue;
                }

                if (block.Kind == MolcaMarkdownBlockKind.Diagram)
                {
                    parent.Add(MolcaMermaid.Create(block.Text));
                    continue;
                }

                if (block.Kind == MolcaMarkdownBlockKind.Task)
                {
                    parent.Add(CreateTaskRow(block, options));
                    continue;
                }

                if (block.Kind == MolcaMarkdownBlockKind.Table)
                {
                    parent.Add(CreateTable(block, options));
                    continue;
                }

                if (block.Kind == MolcaMarkdownBlockKind.Rule)
                {
                    var rule = new VisualElement();
                    rule.AddToClassList("molca-md-rule");
                    parent.Add(rule);
                    continue;
                }

                if (block.Kind == MolcaMarkdownBlockKind.Quote)
                {
                    var quote = CreateInlineBlock(block.RawText, options);
                    quote.AddToClassList("molca-md-quote");
                    parent.Add(quote);
                    continue;
                }

                var element = CreateInlineBlock(block.RawText, options);
                ApplyBlockStyle(element, block);
                parent.Add(element);
            }
        }

        /// <summary>
        /// Default file-link handler: opens the path as a Unity asset (at <paramref name="line"/> if &gt; 0),
        /// falling back to the external editor for files outside the AssetDatabase.
        /// </summary>
        public static void OpenFile(string path, int line)
        {
            if (string.IsNullOrEmpty(path)) return;
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj != null)
            {
                AssetDatabase.OpenAsset(obj, line > 0 ? line : -1);
                return;
            }

            var filePath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", path);
            InternalEditorUtility.OpenFileAtLineExternal(filePath, line > 0 ? line : 0);
        }

        private static VisualElement CreateListRow(MolcaMarkdownBlock block, MolcaMarkdownOptions options)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-md-list-row");
            var marker = new Label(block.Kind == MolcaMarkdownBlockKind.Bullet ? "•" : $"{block.Number}.");
            marker.AddToClassList("molca-md-list-marker");
            ApplyVariant(marker, options);

            var text = CreateInlineBlock(block.RawText, options);
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;

            row.Add(marker);
            row.Add(text);
            return row;
        }

        /// <summary>
        /// Builds the inline content of a block. A block that is a single non-link run — the common case of
        /// plain prose — renders as one wrapping <see cref="Label"/> instead of one label per word. Mixed or
        /// link-bearing blocks fall back to a wrapping container of per-run elements so links stay clickable.
        /// </summary>
        /// <param name="forceContainer">
        /// When <c>true</c>, always return the wrapping container even for a single run. Table cells need this:
        /// the container is the stretch-filled cell, and its runs (e.g. an inline-code chip) sit inside as
        /// content-sized children — a lone code chip returned as the cell itself would take the whole cell's
        /// background instead of hugging its text.
        /// </param>
        private static VisualElement CreateInlineBlock(string rawText, MolcaMarkdownOptions options, bool forceContainer = false)
        {
            var spans = ParseInline(rawText, options);

            // Fast path: a single non-interactive run is one Label with white-space:normal, which wraps on its own.
            if (!forceContainer && spans.Count == 1 && spans[0].Kind != MolcaMarkdownInlineKind.Link
                && spans[0].Kind != MolcaMarkdownInlineKind.Action && spans[0].Kind != MolcaMarkdownInlineKind.Url)
            {
                var s = spans[0];
                return MakeSpanLabel(s.Text, options,
                    isCode: s.Kind == MolcaMarkdownInlineKind.Code,
                    bold: s.Kind == MolcaMarkdownInlineKind.Bold,
                    italic: s.Kind == MolcaMarkdownInlineKind.Italic);
            }

            var container = new VisualElement();
            container.AddToClassList("molca-md-inline-container");

            foreach (var span in spans)
            {
                switch (span.Kind)
                {
                    case MolcaMarkdownInlineKind.Link:
                        container.Add(MakeLinkLabel(span, options));
                        break;
                    case MolcaMarkdownInlineKind.Action:
                        container.Add(MakeActionLabel(span, options));
                        break;
                    case MolcaMarkdownInlineKind.Url:
                        container.Add(MakeUrlLabel(span, options));
                        break;
                    case MolcaMarkdownInlineKind.Code:
                        container.Add(MakeSpanLabel(span.Text, options, isCode: true, bold: false, italic: false));
                        break;
                    case MolcaMarkdownInlineKind.Bold:
                        AddWords(container, span.Text, options, bold: true, italic: false);
                        break;
                    case MolcaMarkdownInlineKind.Italic:
                        AddWords(container, span.Text, options, bold: false, italic: true);
                        break;
                    default:
                        AddWords(container, span.Text, options, bold: false, italic: false);
                        break;
                }
            }
            return container;
        }

        private static void AddWords(VisualElement container, string text, MolcaMarkdownOptions options, bool bold, bool italic)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Split on spaces, keeping a trailing space on each word so wrapping looks natural.
            var words = text.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                var word = i < words.Length - 1 ? words[i] + " " : words[i];
                if (word.Length == 0) { container.Add(MakeSpanLabel(" ", options, false, bold, italic)); continue; }
                container.Add(MakeSpanLabel(word, options, false, bold, italic));
            }
        }

        private static Label MakeSpanLabel(string text, MolcaMarkdownOptions options, bool isCode, bool bold, bool italic)
        {
            var label = new Label(text);
            label.AddToClassList("molca-md-span");
            ApplyVariant(label, options);
            if (bold) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            if (italic) label.style.unityFontStyleAndWeight = bold ? FontStyle.BoldAndItalic : FontStyle.Italic;
            if (isCode) { label.AddToClassList("molca-md-inline-code"); ApplyMonospace(label); }
            return label;
        }

        private static Label MakeLinkLabel(MolcaMarkdownInline span, MolcaMarkdownOptions options)
        {
            // Hover color is handled by the .molca-md-link:hover USS rule.
            var label = new Label(span.Text)
            {
                tooltip = span.LinkLine > 0 ? $"Open {span.LinkPath}:{span.LinkLine}" : $"Open {span.LinkPath}"
            };
            label.AddToClassList("molca-md-link");
            label.RegisterCallback<ClickEvent>(_ => (options?.OnOpenFile ?? OpenFile)(span.LinkPath, span.LinkLine));
            return label;
        }

        private static Label MakeActionLabel(MolcaMarkdownInline span, MolcaMarkdownOptions options)
        {
            var label = new Label(span.Text);
            label.AddToClassList("molca-md-link");
            label.AddToClassList("molca-md-action");
            if (!string.IsNullOrEmpty(options?.ActionTooltip)) label.tooltip = options.ActionTooltip;
            if (options?.OnAction != null)
                label.RegisterCallback<ClickEvent>(_ => options.OnAction(span.ActionUri));
            return label;
        }

        private static Label MakeUrlLabel(MolcaMarkdownInline span, MolcaMarkdownOptions options)
        {
            // span.LinkPath carries the http/https target; opening is an explicit click only. The parser
            // already rejected non-web schemes, so this is always a safe URL.
            var label = new Label(span.Text) { tooltip = $"Open {span.LinkPath}" };
            label.AddToClassList("molca-md-link");
            label.RegisterCallback<ClickEvent>(_ =>
            {
                if (!string.IsNullOrEmpty(span.LinkPath)) (options?.OnOpenUrl ?? Application.OpenURL)(span.LinkPath);
            });
            return label;
        }

        private static VisualElement CreateTaskRow(MolcaMarkdownBlock block, MolcaMarkdownOptions options)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-md-list-row");
            row.AddToClassList("molca-md-task");

            var box = new Label(block.Checked ? "☑" : "☐");
            box.AddToClassList("molca-md-list-marker");
            box.AddToClassList("molca-md-task__box");
            if (block.Checked) box.AddToClassList("molca-md-task__box--checked");
            ApplyVariant(box, options);

            var text = CreateInlineBlock(block.RawText, options);
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            if (block.Checked) text.AddToClassList("molca-md-task__text--checked");

            row.Add(box);
            row.Add(text);
            return row;
        }

        /// <summary>Bolds an inline block whether it rendered as a single <see cref="Label"/> or a container.</summary>
        private static void BoldContent(VisualElement element)
        {
            if (element is Label label) { label.style.unityFontStyleAndWeight = FontStyle.Bold; return; }
            foreach (var child in element.Children())
                if (child is Label l) l.style.unityFontStyleAndWeight = FontStyle.Bold;
        }

        private static VisualElement CreateTable(MolcaMarkdownBlock block, MolcaMarkdownOptions options)
        {
            var table = new VisualElement();
            table.AddToClassList("molca-md-table");
            if (block.TableRows == null) return table;

            // Columns are content-weighted: each column's weight is the longest visible cell text in that
            // column (clamped). The weight is used as flex-grow with a zero flex-basis, so columns divide the
            // table width in proportion to their content — a 1-char "#" column stays tight while a text-heavy
            // column takes the bulk. Because the weight is keyed by column index (not per cell), columns stay
            // aligned across rows; because an outlier is clamped (not unbounded), a very long cell wraps within
            // its share instead of starving the others or widening the table past its container.
            var weights = ComputeColumnWeights(block.TableRows, options);

            for (var r = 0; r < block.TableRows.Count; r++)
            {
                var cells = block.TableRows[r];
                if (cells == null) continue;

                var rowEl = new VisualElement();
                rowEl.AddToClassList("molca-md-table__row");
                if (r == 0) rowEl.AddToClassList("molca-md-table__row--header");

                for (var c = 0; c < cells.Count; c++)
                {
                    // forceContainer: the cell element is the stretch-filled column slot, so its inline runs
                    // must be content-sized children — a lone code chip returned as the cell would otherwise
                    // paint its background across the whole cell.
                    var cellEl = CreateInlineBlock(cells[c] ?? string.Empty, options, forceContainer: true);
                    cellEl.AddToClassList("molca-md-table__cell");
                    cellEl.style.flexGrow = c < weights.Count ? weights[c] : DefaultColumnWeight;
                    cellEl.style.flexBasis = 0;   // ratio comes purely from flex-grow; basis stays 0
                    cellEl.style.flexShrink = 1;  // over-wide content wraps (white-space:normal) rather than overflowing
                    if (r == 0) BoldContent(cellEl);
                    rowEl.Add(cellEl);
                }
                table.Add(rowEl);
            }
            return table;
        }

        /// <summary>Flex-grow weight for a table column whose content is empty or absent.</summary>
        private const float DefaultColumnWeight = 1f;

        /// <summary>Floor on a column's weight so a very short column keeps a legible minimum share of the width.</summary>
        private const float MinColumnWeight = 3f;

        /// <summary>
        /// Ceiling on a column's weight so one overlong cell can't dominate the ratio; past this the cell wraps
        /// within its (capped) share instead of starving the other columns.
        /// </summary>
        private const float MaxColumnWeight = 40f;

        /// <summary>
        /// Computes a per-column flex-grow weight for a table: the longest visible cell text in each column,
        /// clamped to <see cref="MinColumnWeight"/>..<see cref="MaxColumnWeight"/>. See <see cref="CreateTable"/>
        /// for how the weights drive column widths.
        /// </summary>
        /// <param name="rows">The parsed table rows (row 0 = header); may contain <c>null</c> rows.</param>
        /// <param name="options">Render options, forwarded to inline parsing so link labels weigh their visible text.</param>
        /// <returns>One clamped weight per column index, sized to the widest row.</returns>
        private static IReadOnlyList<float> ComputeColumnWeights(
            IReadOnlyList<IReadOnlyList<string>> rows, MolcaMarkdownOptions options)
        {
            var weights = new List<float>();
            foreach (var row in rows)
            {
                if (row == null) continue;
                for (var c = 0; c < row.Count; c++)
                {
                    float len = VisibleCellLength(row[c], options);
                    if (c >= weights.Count) weights.Add(len);
                    else weights[c] = Mathf.Max(weights[c], len);
                }
            }
            for (var c = 0; c < weights.Count; c++)
                weights[c] = Mathf.Clamp(weights[c], MinColumnWeight, MaxColumnWeight);
            return weights;
        }

        /// <summary>
        /// The rendered length of a table cell, counting only visible glyphs — Markdown markup (emphasis,
        /// code fences, link/URL syntax) is excluded so a link like <c>[Label](target)</c> weighs its visible
        /// <c>Label</c>, not the target. Used as a cheap, layout-free proxy for a cell's rendered width.
        /// </summary>
        private static int VisibleCellLength(string cell, MolcaMarkdownOptions options)
        {
            if (string.IsNullOrEmpty(cell)) return 0;
            var spans = ParseInline(cell, options);
            var len = 0;
            for (var i = 0; i < spans.Count; i++)
                len += spans[i].Text?.Length ?? 0;
            return len;
        }

        private static VisualElement CreateCodeBlock(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-md-code-block");
            label.style.whiteSpace = WhiteSpace.Pre; // preserve indentation and line breaks
            ApplyMonospace(label);
            return label;
        }

        // A monospace OS font so code reads as code. Resolved once and cached; null (rare) leaves the label
        // on the surface's default font. Kept off the USS layer because no monospace font ships in the package.
        private static Font _monoFont;
        private static bool _monoResolved;

        private static Font MonoFont
        {
            get
            {
                if (_monoResolved) return _monoFont;
                _monoResolved = true;
                try
                {
                    _monoFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New", "monospace" }, 12);
                }
                catch
                {
                    _monoFont = null;
                }
                return _monoFont;
            }
        }

        private static void ApplyMonospace(Label label)
        {
            var font = MonoFont;
            if (font != null) label.style.unityFontDefinition = FontDefinition.FromFont(font);
        }

        private static void ApplyVariant(Label label, MolcaMarkdownOptions options)
        {
            switch (options?.Variant ?? MolcaMarkdownVariant.Default)
            {
                case MolcaMarkdownVariant.Muted:
                    label.AddToClassList("molca-md--muted");
                    break;
                case MolcaMarkdownVariant.Error:
                    label.AddToClassList("molca-md--error");
                    break;
            }
        }

        private static void ApplyBlockStyle(VisualElement element, MolcaMarkdownBlock block)
        {
            switch (block.Kind)
            {
                case MolcaMarkdownBlockKind.Heading:
                    element.style.marginTop = 4;
                    element.style.marginBottom = 4;
                    // Bold + size by level, whether the block rendered as a single Label (plain heading, the
                    // common case) or a container of inline runs.
                    BoldContent(element);
                    var size = HeadingFontSize(block.Number);
                    if (size > 0) ApplyFontSize(element, size);
                    break;
                case MolcaMarkdownBlockKind.Warning:
                    ApplyColor(element, MolcaEditorColors.StatusWarn);
                    break;
                case MolcaMarkdownBlockKind.Error:
                    ApplyColor(element, MolcaEditorColors.StatusError);
                    break;
            }
        }

        /// <summary>Font size (px) for a heading level 1–6; 0 leaves the label at the surface default.</summary>
        private static int HeadingFontSize(int level) => level switch
        {
            1 => 16,
            2 => 14,
            3 => 13,
            _ => 0
        };

        private static void ApplyFontSize(VisualElement element, int size)
        {
            if (element is Label label) { label.style.fontSize = size; return; }
            foreach (var child in element.Children())
                if (child is Label l) l.style.fontSize = size;
        }

        private static void ApplyColor(VisualElement element, Color color)
        {
            if (element is Label label) { label.style.color = color; return; }
            foreach (var child in element.Children())
                if (child is Label l) l.style.color = color;
        }

        // ---- Parse helpers --------------------------------------------------------------------------

        private static bool TryReadMarkdownLink(string text, int start, out string label, out string target, out int endIndex)
        {
            label = null;
            target = null;
            endIndex = -1;

            var match = MarkdownLinkRegex.Match(text, start);
            if (!match.Success || match.Index != start) return false;

            label = match.Groups["label"].Value;
            target = match.Groups["target"].Value;
            endIndex = match.Index + match.Length - 1;
            return !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(target);
        }

        private static bool IsWebUrl(string target)
            => !string.IsNullOrEmpty(target)
               && (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        /// <summary>Parses a Markdown-link target as a known file path with optional <c>:line</c> suffix.</summary>
        private static bool TrySplitFileTarget(string target, out string path, out int line)
        {
            path = null;
            line = 0;
            if (string.IsNullOrEmpty(target)) return false;

            var m = LinkRegex.Match(target);
            if (!m.Success || m.Index != 0 || m.Length != target.Length) return false;

            path = m.Groups["path"].Value;
            line = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var n) ? n : 0;
            return true;
        }

        /// <summary>Flushes a plain-text buffer, splitting out any file/path links into their own runs.</summary>
        private static void FlushTextRun(ICollection<MolcaMarkdownInline> spans, StringBuilder buffer)
        {
            if (buffer.Length == 0) return;
            var text = buffer.ToString();
            buffer.Length = 0;

            var last = 0;
            foreach (Match m in LinkRegex.Matches(text))
            {
                if (m.Index > last)
                    spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Text, text.Substring(last, m.Index - last)));

                var path = m.Groups["path"].Value;
                var line = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var n) ? n : 0;
                spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Link, m.Value, path, line));
                last = m.Index + m.Length;
            }

            if (last < text.Length)
                spans.Add(new MolcaMarkdownInline(MolcaMarkdownInlineKind.Text, last == 0 ? text : text.Substring(last)));
        }

        private static bool IsWordBoundaryBefore(string text, int index)
        {
            if (index == 0) return true;
            var prev = text[index - 1];
            return !char.IsLetterOrDigit(prev) && prev != '_';
        }

        private static int FindItalicClose(string text, int start)
        {
            for (var j = start; j < text.Length; j++)
            {
                if (text[j] != '_') continue;
                var next = j + 1 < text.Length ? text[j + 1] : ' ';
                if (j > start && !char.IsLetterOrDigit(next) && next != '_')
                    return j;
            }
            return -1;
        }

        private static void FlushParagraph(ICollection<MolcaMarkdownBlock> blocks, StringBuilder paragraph)
        {
            if (paragraph.Length == 0) return;
            blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Paragraph, paragraph.ToString()));
            paragraph.Length = 0;
        }

        private static void FlushQuote(ICollection<MolcaMarkdownBlock> blocks, StringBuilder quote)
        {
            if (quote.Length == 0) return;
            blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Quote, quote.ToString().Trim()));
            quote.Length = 0;
        }

        /// <summary>
        /// Emits a buffered run of pipe lines as a <see cref="MolcaMarkdownBlockKind.Table"/> block when it is
        /// a valid Markdown table (header + separator row), otherwise degrades each line to a paragraph.
        /// </summary>
        private static void FlushTable(ICollection<MolcaMarkdownBlock> blocks, List<string> tableLines)
        {
            if (tableLines.Count == 0) return;

            if (tableLines.Count >= 2 && IsTableSeparator(tableLines[1]))
            {
                var rows = new List<IReadOnlyList<string>>();
                for (var i = 0; i < tableLines.Count; i++)
                {
                    if (i == 1) continue; // skip the separator row
                    rows.Add(ParseTableRow(tableLines[i]));
                }
                blocks.Add(new MolcaMarkdownBlock(rows));
            }
            else
            {
                // Not a real table (e.g. a prose line containing pipes) — keep it readable as plain text.
                foreach (var line in tableLines)
                    blocks.Add(MakeBlock(MolcaMarkdownBlockKind.Paragraph, line));
            }
            tableLines.Clear();
        }

        /// <summary>A candidate table row: contains at least two pipe characters.</summary>
        private static bool IsTableRow(string line)
        {
            var pipes = 0;
            foreach (var ch in line)
                if (ch == '|') pipes++;
            return pipes >= 2;
        }

        /// <summary>True if every cell of <paramref name="line"/> is a table separator cell (<c>:?-+:?</c>).</summary>
        private static bool IsTableSeparator(string line)
        {
            var cells = ParseTableRow(line);
            if (cells.Count == 0) return false;
            foreach (var cell in cells)
            {
                var c = cell.Trim();
                if (c.Length == 0) return false;
                var body = c.TrimStart(':').TrimEnd(':');
                if (body.Length == 0) return false;
                foreach (var ch in body)
                    if (ch != '-') return false;
            }
            return true;
        }

        /// <summary>Splits a pipe row into trimmed cells, dropping the empty cells from leading/trailing borders.</summary>
        private static IReadOnlyList<string> ParseTableRow(string line)
        {
            var parts = line.Split('|');
            var cells = new List<string>(parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                // A leading/trailing pipe produces an empty boundary cell; drop only those.
                if ((i == 0 || i == parts.Length - 1) && parts[i].Trim().Length == 0) continue;
                cells.Add(parts[i].Trim());
            }
            return cells;
        }

        /// <summary>A horizontal rule: three or more of a single <c>-</c>/<c>*</c>/<c>_</c> char, nothing else.</summary>
        private static bool IsRule(string line)
        {
            if (line.Length < 3) return false;
            var c = line[0];
            if (c != '-' && c != '*' && c != '_') return false;
            foreach (var ch in line)
                if (ch != c) return false;
            return true;
        }

        /// <summary>Reads a task-list item (<c>- [ ] text</c> / <c>- [x] text</c>); checked for x/X.</summary>
        private static bool TryReadTask(string line, out bool isChecked, out string text)
        {
            isChecked = false;
            text = null;
            if (line.Length < 6) return false;
            if ((line[0] != '-' && line[0] != '*') || line[1] != ' ' || line[2] != '[' || line[4] != ']') return false;

            var mark = line[3];
            if (mark != ' ' && mark != 'x' && mark != 'X') return false;

            isChecked = mark == 'x' || mark == 'X';
            text = line.Substring(5).Trim();
            return text.Length > 0;
        }

        /// <summary>Builds a block holding both the cleaned text and the raw (marker-preserving) text.</summary>
        private static MolcaMarkdownBlock MakeBlock(MolcaMarkdownBlockKind kind, string raw, int number = 0)
            => new MolcaMarkdownBlock(kind, CleanInline(raw), raw, number);

        /// <summary>Reads an ATX heading (<c># </c> … <c>###### </c>), returning its level (1–6) and text.</summary>
        private static bool TryReadHeading(string line, out int level, out string text)
        {
            level = 0;
            text = null;

            var hashes = 0;
            while (hashes < line.Length && line[hashes] == '#') hashes++;
            if (hashes < 1 || hashes > 6 || hashes >= line.Length || line[hashes] != ' ') return false;

            level = hashes;
            text = line.Substring(hashes + 1).Trim();
            return text.Length > 0;
        }

        private static bool TryReadNumbered(string line, out int number, out string text)
        {
            number = 0;
            text = null;

            var dot = line.IndexOf('.');
            if (dot <= 0 || dot + 1 >= line.Length || !char.IsWhiteSpace(line[dot + 1])) return false;
            for (var i = 0; i < dot; i++)
                if (!char.IsDigit(line[i]))
                    return false;

            if (!int.TryParse(line.Substring(0, dot), out number)) return false;
            text = line.Substring(dot + 1).Trim();
            return text.Length > 0;
        }

        private static bool StartsWithLabel(string text, string label)
            => text.StartsWith(label, StringComparison.OrdinalIgnoreCase);
    }
}
