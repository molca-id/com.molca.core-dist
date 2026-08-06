using System;
using System.Globalization;
using UnityEngine;

namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// Pure formatting/parsing helpers for the ClickUp DTO fields that arrive in awkward shapes: epoch
    /// milliseconds carried as JSON strings, and CSS hex colors.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/ClickUp/</c>.
    /// Registration: static utility; not an asset.
    /// <para>
    /// Shared by the Hub Tasks section (which wants relative, human due dates and real colors) and the MCP
    /// ClickUp tools (which want ISO-8601 an agent can reason about), so neither re-derives the parsing. Every
    /// method is total: malformed or absent input yields <c>false</c>/empty rather than throwing, because this
    /// runs on remote data that is not worth a try/catch at each call site.
    /// </para>
    /// <para>
    /// The <c>now</c> parameter is explicit on the relative formatter so tests are deterministic; the
    /// convenience overload supplies <see cref="DateTimeOffset.UtcNow"/>.
    /// </para>
    /// </remarks>
    internal static class ClickUpTaskFormat
    {
        /// <summary>
        /// Parses a ClickUp timestamp — Unix epoch milliseconds in a JSON string — into a UTC instant.
        /// </summary>
        /// <param name="raw">The raw field value, e.g. <c>"1508369194377"</c>; null/empty yields <c>false</c>.</param>
        /// <param name="instant">The parsed instant when this returns <c>true</c>.</param>
        /// <returns><c>true</c> when <paramref name="raw"/> was a parseable epoch-millisecond value.</returns>
        internal static bool TryParseEpochMillis(string raw, out DateTimeOffset instant)
        {
            instant = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long millis))
                return false;

            // Guard the conversion: ClickUp has been seen to send 0 for "no date", and an out-of-range value
            // would otherwise throw out of a formatting call on the render path.
            try
            {
                if (millis <= 0) return false;
                instant = DateTimeOffset.FromUnixTimeMilliseconds(millis);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>
        /// Renders a ClickUp timestamp as ISO-8601 UTC, for machine consumers (the MCP tools).
        /// </summary>
        /// <param name="raw">The raw epoch-millisecond string.</param>
        /// <returns>An ISO-8601 string, or <c>null</c> when there is no parseable date.</returns>
        internal static string ToIso8601(string raw)
            => TryParseEpochMillis(raw, out var instant)
                ? instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : null;

        /// <summary>
        /// Renders a due date the way a developer skimming a list wants to read it: "Today", "Tomorrow",
        /// "in 3d", "2d overdue".
        /// </summary>
        /// <param name="raw">The raw epoch-millisecond string.</param>
        /// <param name="now">The instant to measure against.</param>
        /// <param name="overdue">Set when the due date is in the past, so the caller can color it.</param>
        /// <returns>A short label, or an empty string when there is no parseable due date.</returns>
        /// <remarks>
        /// Comparison is by calendar day in UTC, not by elapsed hours — a task due at 23:00 today should read
        /// "Today", not "in 0d", and one due at 01:00 today should read "Today", not "overdue".
        /// </remarks>
        internal static string FormatRelativeDue(string raw, DateTimeOffset now, out bool overdue)
        {
            overdue = false;
            if (!TryParseEpochMillis(raw, out var due)) return string.Empty;

            int days = (int)(due.UtcDateTime.Date - now.UtcDateTime.Date).TotalDays;
            overdue = days < 0;

            return days switch
            {
                0 => "Today",
                1 => "Tomorrow",
                -1 => "1d overdue",
                < 0 => $"{-days}d overdue",
                < 7 => $"in {days}d",
                _ => due.UtcDateTime.ToString("MMM d", CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc cref="FormatRelativeDue(string, DateTimeOffset, out bool)"/>
        internal static string FormatRelativeDue(string raw, out bool overdue)
            => FormatRelativeDue(raw, DateTimeOffset.UtcNow, out overdue);

        /// <summary>
        /// Parses a CSS hex color as ClickUp sends them for statuses, priorities, and tags.
        /// </summary>
        /// <param name="hex">A <c>#rgb</c>, <c>#rrggbb</c>, or <c>#rrggbbaa</c> string; the <c>#</c> is optional.</param>
        /// <param name="color">The parsed color when this returns <c>true</c>.</param>
        /// <returns><c>true</c> when the string was a parseable hex color.</returns>
        /// <remarks>
        /// Delegates to <see cref="ColorUtility.TryParseHtmlString"/> rather than hand-rolling the digit math,
        /// but normalizes the missing leading <c>#</c> first — Unity's parser requires it for hex input, and
        /// ClickUp is not consistent about sending it.
        /// </remarks>
        internal static bool TryParseHexColor(string hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            string normalized = hex.Trim();
            if (!normalized.StartsWith("#", StringComparison.Ordinal))
                normalized = "#" + normalized;

            return ColorUtility.TryParseHtmlString(normalized, out color);
        }

        /// <summary>
        /// Picks a readable foreground for a colored pill, by the perceived luminance of its background.
        /// </summary>
        /// <param name="background">The pill's background color.</param>
        /// <returns>Near-black on light backgrounds, near-white on dark ones.</returns>
        /// <remarks>
        /// ClickUp status colors span the full range from pale grey to saturated red, so a fixed foreground is
        /// illegible against one end or the other. Uses the Rec. 601 luma weights — cheap, and accurate enough
        /// for a contrast decision on a badge.
        /// </remarks>
        internal static Color ReadableForeground(Color background)
        {
            float luma = (0.299f * background.r) + (0.587f * background.g) + (0.114f * background.b);
            return luma > 0.6f ? new Color(0.11f, 0.11f, 0.11f) : new Color(0.96f, 0.96f, 0.96f);
        }

        /// <summary>
        /// Builds the initials shown in an assignee chip: one letter for a single-word name, two for a name
        /// that splits.
        /// </summary>
        /// <param name="user">The assignee; may be <c>null</c>.</param>
        /// <returns>One or two uppercase characters, or <c>"?"</c> when nothing usable is present.</returns>
        internal static string Initials(ClickUpModels.User user)
        {
            string source = user?.username;
            if (string.IsNullOrWhiteSpace(source)) source = user?.email;
            if (string.IsNullOrWhiteSpace(source)) return "?";

            source = source.Trim();

            // An email has no useful second word — take the first two characters of the local part instead.
            int at = source.IndexOf('@');
            if (at > 0) source = source.Substring(0, at);

            var parts = source.Split(new[] { ' ', '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1)
                return parts[0].Length == 1
                    ? parts[0].ToUpperInvariant()
                    : parts[0].Substring(0, 2).ToUpperInvariant();

            return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
        }
    }
}
