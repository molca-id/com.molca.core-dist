using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Molca.Editor.Mcp.Assistant;
using Molca.Networking.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only web tools (Sprint 75): <c>molca_web_fetch</c> retrieves a URL as sanitized, size-capped,
    /// secret-redacted text, and <c>molca_web_search</c> queries a configured search backend. Both close the
    /// "world outside the project" gap that KG grounding (Sprint 47) leaves open — a current Unity API
    /// signature, a package version, a changelog — without a full third-party MCP-client stack.
    /// </summary>
    /// <remarks>
    /// Egress is a policy choice: both tools are <b>off by default</b> and gated by
    /// <see cref="AssistantSettings.WebToolsEnabled"/>; <c>molca_web_fetch</c> is further restricted to
    /// <see cref="AssistantSettings.WebHostAllowlist"/>. Transport reuses the assistant's hardened, pause-safe
    /// HTTP path (<see cref="AssistantHttp"/>, Sprints 65/68) which degrades a transport fault to a non-success
    /// result rather than throwing. Results honor <see cref="AssistantSettings.MaxToolResultChars"/>. The search
    /// key lives in <see cref="AssistantWebAuth"/> (scoped prefs / env var), never on the settings asset.
    /// <para>
    /// Follow-up seam (decision e): consuming third-party MCP <b>servers</b> as a client is intentionally out of
    /// scope — these two first-party tools cover the common need. See
    /// <c>Documentation~/reference/ASSISTANT_WEB_TOOLS.md</c>.
    /// </para>
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        /// <summary>Per-request fetch timeout (seconds) — short enough that an unreachable host fails fast.</summary>
        private const int WebFetchTimeoutSeconds = 20;

        /// <summary>Descriptive User-Agent so docs hosts that reject blank agents still serve the page.</summary>
        private const string WebUserAgent = "MolcaEditorAssistant/1.0 (+https://molca.id)";

        // ── molca_web_fetch (read, egress-gated) ─────────────────────────────────────────────

        private static McpToolDefinition CreateWebFetchTool() => new McpToolDefinition(
            name: "molca_web_fetch",
            description: "Fetches a URL and returns its readable text (HTML stripped), size-capped and with "
                       + "secret-looking tokens redacted. Read-only. Requires the web tools to be enabled and the "
                       + "URL's host to be on the fetch allowlist (Hub → Assistant → Advanced → Web). Use it for "
                       + "current API signatures, package versions, changelogs, and external docs; combine with "
                       + "the knowledge-graph tools for project-specific context.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"url\":{\"type\":\"string\",\"description\":\"Absolute http(s) URL to fetch. Host must be allowlisted.\"}}," +
                "\"required\":[\"url\"],\"additionalProperties\":false}",
            executeAsync: ExecuteWebFetch,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteWebFetch(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!TryResolveWebPolicy(out var settings, out string policyError))
                return Error(policyError);

            string rawUrl = args.Value<string>("url");
            if (string.IsNullOrWhiteSpace(rawUrl))
                return Error("'url' is required (an absolute http(s) URL).");

            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Error($"'{rawUrl}' is not an absolute http(s) URL.");

            if (!settings.IsHostAllowed(uri.Host))
                return Error($"Host '{uri.Host}' is not on the fetch allowlist. Add it in "
                           + "Hub → Assistant → Advanced → Web (Host Allowlist), or fetch an allowlisted host. "
                           + $"Currently allowed: {AllowlistSummary(settings)}.");

            var headers = new Dictionary<string, string> { ["User-Agent"] = WebUserAgent };

            AssistantHttpResult result;
            try
            {
                result = await AssistantHttp.GetAsync(uri.AbsoluteUri, headers, WebFetchTimeoutSeconds, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return Error($"Fetch failed: {ex.Message}");
            }

            if (!result.IsSuccess)
                return Error($"Fetch failed (HTTP {result.StatusCode}). {Truncate(result.Body, 400)}".Trim());

            string text = BuildFetchText(result.Body, settings.MaxToolResultChars, out bool truncated);

            return new JObject
            {
                ["url"] = LogRedaction.RedactUrl(uri.AbsoluteUri),
                ["status"] = result.StatusCode,
                ["truncated"] = truncated,
                ["chars"] = text.Length,
                ["text"] = text
            }.ToString(Formatting.None);
        }

        // ── molca_web_search (read, egress-gated, optional provider) ─────────────────────────

        private static McpToolDefinition CreateWebSearchTool() => new McpToolDefinition(
            name: "molca_web_search",
            description: "Searches the web via the configured provider (Brave or Tavily) and returns title/url/"
                       + "snippet rows. Read-only. Requires the web tools to be enabled and a search provider + key "
                       + "configured (Hub → Assistant → Advanced → Web); degrades to a clear 'no search provider "
                       + "configured' result otherwise. Follow up with molca_web_fetch to read a result.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"The search query.\"}," +
                "\"maxResults\":{\"type\":\"integer\",\"description\":\"Optional cap on results (default from settings).\"}}," +
                "\"required\":[\"query\"],\"additionalProperties\":false}",
            executeAsync: ExecuteWebSearch,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteWebSearch(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!TryResolveWebPolicy(out var settings, out string policyError))
                return Error(policyError);

            string query = args.Value<string>("query");
            if (string.IsNullOrWhiteSpace(query))
                return Error("'query' is required.");

            if (!EvaluateSearchPolicy(settings, out string searchError))
                return Error(searchError);

            var providerKind = settings.WebSearchProvider;
            string key = AssistantWebAuth.GetKey(providerKind);

            int max = settings.WebSearchMaxResults;
            var requested = args.Value<int?>("maxResults");
            if (requested is > 0) max = Math.Min(max, requested.Value);

            AssistantHttpResult result;
            try
            {
                result = providerKind == WebSearchProviderKind.Brave
                    ? await BraveSearchAsync(query, key)
                    : await TavilySearchAsync(query, key);
            }
            catch (Exception ex)
            {
                return Error($"Search failed: {ex.Message}");
            }

            if (!result.IsSuccess)
                return Error($"Search failed (HTTP {result.StatusCode}). {Truncate(result.Body, 400)}".Trim());

            var rows = providerKind == WebSearchProviderKind.Brave
                ? ParseBraveResults(result.Body)
                : ParseTavilyResults(result.Body);

            var trimmed = rows.Take(max).ToList();
            var arr = new JArray();
            foreach (var (title, url, snippet) in trimmed)
            {
                arr.Add(new JObject
                {
                    ["title"] = RedactSecrets(title),
                    ["url"] = url,
                    ["snippet"] = RedactSecrets(Truncate(snippet, 500))
                });
            }

            return new JObject
            {
                ["provider"] = providerKind.ToString(),
                ["query"] = query,
                ["count"] = arr.Count,
                ["results"] = arr
            }.ToString(Formatting.None);
        }

        // ── search backends ──────────────────────────────────────────────────────────────────

        private static async Awaitable<AssistantHttpResult> BraveSearchAsync(string query, string key)
        {
            string url = "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query);
            var headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["X-Subscription-Token"] = key,
                ["User-Agent"] = WebUserAgent
            };
            return await AssistantHttp.GetAsync(url, headers, WebFetchTimeoutSeconds, CancellationToken.None);
        }

        private static async Awaitable<AssistantHttpResult> TavilySearchAsync(string query, string key)
        {
            var body = new JObject
            {
                ["api_key"] = key,
                ["query"] = query,
                ["search_depth"] = "basic"
            }.ToString(Formatting.None);
            var headers = new Dictionary<string, string> { ["User-Agent"] = WebUserAgent };
            return await AssistantHttp.PostAsync(
                "https://api.tavily.com/search", headers, body,
                streaming: false, onSseLine: null, timeoutSeconds: WebFetchTimeoutSeconds,
                cancellationToken: CancellationToken.None, maxAttempts: 1);
        }

        /// <summary>Parses a Brave web-search response into title/url/snippet rows (Sprint 75).</summary>
        internal static List<(string title, string url, string snippet)> ParseBraveResults(string json)
        {
            var rows = new List<(string, string, string)>();
            JObject root;
            try { root = JObject.Parse(json ?? "{}"); } catch { return rows; }
            if (root["web"]?["results"] is JArray results)
                foreach (var r in results)
                    rows.Add((r.Value<string>("title") ?? "", r.Value<string>("url") ?? "",
                        r.Value<string>("description") ?? ""));
            return rows;
        }

        /// <summary>Parses a Tavily search response into title/url/snippet rows (Sprint 75).</summary>
        internal static List<(string title, string url, string snippet)> ParseTavilyResults(string json)
        {
            var rows = new List<(string, string, string)>();
            JObject root;
            try { root = JObject.Parse(json ?? "{}"); } catch { return rows; }
            if (root["results"] is JArray results)
                foreach (var r in results)
                    rows.Add((r.Value<string>("title") ?? "", r.Value<string>("url") ?? "",
                        r.Value<string>("content") ?? ""));
            return rows;
        }

        // ── policy + text helpers (pure, testable) ─────────────────────────────────────────────

        /// <summary>
        /// Resolves the web-egress policy without creating settings (Sprint 75): the web tools require an
        /// existing <see cref="AssistantSettings"/> asset with <see cref="AssistantSettings.WebToolsEnabled"/>
        /// on. Returns <c>false</c> with an actionable message when egress is off (the shipped default).
        /// </summary>
        private static bool TryResolveWebPolicy(out AssistantSettings settings, out string error)
        {
            settings = AssistantSettings.FindSettings();
            return EvaluateWebPolicy(settings, out error);
        }

        /// <summary>
        /// Evaluates the web-egress policy for a resolved settings instance (Sprint 75) — extracted pure so the
        /// disabled-by-default and no-settings paths are testable without touching the AssetDatabase. Returns
        /// <c>false</c> with an actionable message when egress is off.
        /// </summary>
        /// <param name="settings">The resolved settings, or <c>null</c> if none exists.</param>
        /// <param name="error">The policy message when egress is refused; <c>null</c> when allowed.</param>
        /// <returns><c>true</c> if web egress is permitted.</returns>
        internal static bool EvaluateWebPolicy(AssistantSettings settings, out string error)
        {
            if (settings == null)
            {
                error = "Web tools are disabled: no Assistant settings exist. Create them in "
                      + "Hub → Assistant and enable Web Tools to allow outbound requests.";
                return false;
            }
            if (!settings.WebToolsEnabled)
            {
                error = "Web tools are disabled. Enable them in Hub → Assistant → Advanced → Web "
                      + "(Enable Web Tools) — off by default because editor network egress is a policy choice.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Evaluates whether <c>molca_web_search</c> can run (Sprint 75): a search provider must be configured
        /// and a key available. Extracted pure so the "no provider" and "no key" degradation paths are testable.
        /// Assumes <see cref="EvaluateWebPolicy"/> already passed.
        /// </summary>
        /// <param name="settings">The resolved settings (non-null).</param>
        /// <param name="error">The degradation message when search is unavailable; <c>null</c> when ready.</param>
        /// <returns><c>true</c> if a provider and key are configured.</returns>
        internal static bool EvaluateSearchPolicy(AssistantSettings settings, out string error)
        {
            if (settings.WebSearchProvider == WebSearchProviderKind.None)
            {
                error = "No search provider configured. Set one in Hub → Assistant → Advanced → Web "
                      + "(Search Provider) and add its API key. molca_web_fetch works without a search provider.";
                return false;
            }
            if (!AssistantWebAuth.HasKey(settings.WebSearchProvider))
            {
                error = $"No API key for the {settings.WebSearchProvider} search provider. Add it in "
                      + $"Hub → Assistant → Advanced → Web, or set the {AssistantWebAuth.EnvVarFor(settings.WebSearchProvider)} env var.";
                return false;
            }
            error = null;
            return true;
        }

        private static string AllowlistSummary(AssistantSettings settings)
        {
            var hosts = settings.WebHostAllowlist.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            return hosts.Count == 0 ? "(none)" : string.Join(", ", hosts);
        }

        private static readonly Regex ScriptStyleBlocks = new Regex(
            @"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex HtmlComments = new Regex("<!--.*?-->", RegexOptions.Singleline);
        private static readonly Regex BlockBreaks = new Regex(
            @"</(p|div|li|ul|ol|tr|h[1-6]|section|article|header|footer|br)\s*>|<br\s*/?>",
            RegexOptions.IgnoreCase);
        private static readonly Regex AnyTag = new Regex("<[^>]+>", RegexOptions.Singleline);
        private static readonly Regex ManyNewlines = new Regex(@"\n{3,}");
        private static readonly Regex ManySpaces = new Regex(@"[ \t]{2,}");

        /// <summary>
        /// Extracts human-readable text from a fetched body (Sprint 75). HTML is stripped — script/style blocks
        /// and comments removed, block-level tags turned into line breaks, remaining tags dropped, entities
        /// decoded, whitespace collapsed. Non-HTML content (JSON, plain text) is returned trimmed and unchanged.
        /// </summary>
        /// <param name="body">The raw response body.</param>
        /// <returns>Readable text, never null.</returns>
        internal static string ExtractReadableText(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            if (!LooksLikeHtml(body)) return body.Trim();

            string text = ScriptStyleBlocks.Replace(body, " ");
            text = HtmlComments.Replace(text, " ");
            text = BlockBreaks.Replace(text, "\n");
            text = AnyTag.Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text);

            // Normalize line endings, trim each line, and collapse runaway blank runs / spaces.
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n').Select(l => ManySpaces.Replace(l, " ").Trim());
            text = string.Join("\n", lines);
            text = ManyNewlines.Replace(text, "\n\n");
            return text.Trim();
        }

        private static bool LooksLikeHtml(string body)
        {
            // Cheap heuristic: any closing tag or a common opening tag marks it as markup worth stripping.
            return body.IndexOf("</", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("<div", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("<p>", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Secret-looking token patterns redacted from fetched/searched content so a page that leaks a key
        // doesn't surface it verbatim through the tool result.
        private static readonly Regex[] SecretPatterns =
        {
            new Regex(@"\b(sk-ant|sk|xai|pk)-[A-Za-z0-9_-]{16,}\b"),
            new Regex(@"\b(ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{16,}\b"),
            new Regex(@"\bAKIA[0-9A-Z]{16}\b"),
            new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
            new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{16,}"),
            new Regex(@"(?i)\b(api[_-]?key|secret|access[_-]?token|token)\b\s*[:=]\s*[""']?[A-Za-z0-9._\-]{16,}"),
        };

        /// <summary>
        /// Redacts secret-looking tokens (API keys, bearer tokens, provider keys) from arbitrary text (Sprint
        /// 75), replacing each match with <c>***</c>. Heuristic, not exhaustive — a defense-in-depth measure so
        /// fetched web content can't trivially exfiltrate a credential that happens to appear on the page.
        /// </summary>
        /// <param name="text">The text to scrub.</param>
        /// <returns>The text with secret-looking spans masked, never null.</returns>
        internal static string RedactSecrets(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            foreach (var pattern in SecretPatterns)
                text = pattern.Replace(text, "***");
            return text;
        }

        /// <summary>
        /// Turns a raw fetched body into the tool's text field (Sprint 75): HTML-stripped, secret-redacted, and
        /// truncated to <paramref name="cap"/>. Extracted pure so the size-cap + redaction pipeline is testable
        /// without a live fetch.
        /// </summary>
        /// <param name="body">The raw response body.</param>
        /// <param name="cap">The maximum character count (from <see cref="AssistantSettings.MaxToolResultChars"/>).</param>
        /// <param name="truncated">Set to <c>true</c> when the text was cut to fit <paramref name="cap"/>.</param>
        /// <returns>The readable, redacted, size-capped text; never null.</returns>
        internal static string BuildFetchText(string body, int cap, out bool truncated)
        {
            string text = RedactSecrets(ExtractReadableText(body));
            truncated = cap > 0 && text.Length > cap;
            if (truncated) text = text.Substring(0, cap);
            return text;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
