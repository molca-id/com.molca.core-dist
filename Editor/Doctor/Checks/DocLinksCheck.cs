using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Molca.Editor.Hub.Docs;
using Molca.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags broken links in the Hub-browsable reference docs (every <c>com.molca.*</c> package's
    /// <c>Documentation~/reference/*.md</c>): sibling <c>.md</c> cross-links, <c>molca://doc/&lt;id&gt;</c>
    /// navigation, <c>molca://asset/&lt;guid-or-path&gt;</c> asset links, and source-file links that no
    /// longer resolve.
    /// </summary>
    /// <remarks>
    /// Validates against how the docs viewer actually resolves links (see
    /// <see cref="MolcaEditorDocLinks"/>): a <c>.md</c> link is good when a reference doc with that file name
    /// exists (it navigates in-viewer) or the file is on disk; <c>molca://doc</c> resolves against the
    /// <see cref="MolcaDocsRegistry"/>; <c>molca://asset</c> and file links resolve through the AssetDatabase
    /// or the filesystem. Web links are not fetched. Fenced code blocks are skipped so syntax examples (like
    /// the ones in <c>DOCS_AUTHORING.md</c>) are not flagged. Findings are
    /// <see cref="DoctorSeverity.Warning"/>. Runs on the main thread (AssetDatabase / Package Manager) and
    /// yields periodically. Surfaced to the assistant via <c>molca_doctor</c>.
    /// </remarks>
    public class DocLinksCheck : IDoctorCheck
    {
        public string Id => "doc-links";
        public string Description => "Reference docs have no broken cross-doc, asset, or file links";

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Stays on the main thread (registry scan + AssetDatabase); this yields one frame up front and
            // then every few docs so a large doc set does not stall the editor.
            await Awaitable.NextFrameAsync(cancellationToken);

            var issues = new List<DoctorIssue>();
            var docs = MolcaDocsRegistry.GetDocs();
            if (docs.Count == 0) return issues;

            var docFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var docIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in docs)
            {
                docFilenames.Add(Path.GetFileName(d.AbsolutePath));
                docIds.Add(d.Id);
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;

            var processed = 0;
            foreach (var doc in docs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string[] rawLines;
                string body;
                try
                {
                    var text = File.ReadAllText(doc.AbsolutePath);
                    rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    body = MolcaMarkdown.StripFrontMatter(text, out _);
                }
                catch (Exception)
                {
                    continue;
                }

                var docDir = Path.GetDirectoryName(doc.AbsolutePath);
                foreach (var target in ExtractLinkTargets(body))
                {
                    if (string.IsNullOrEmpty(target) || IsWebLink(target)) continue;

                    string reason = null;
                    if (target.StartsWith("molca://", StringComparison.Ordinal))
                    {
                        if (!IsMolcaLinkResolvable(target, docIds, out reason)) { /* reason set */ }
                        else continue;
                    }
                    else if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        if (docFilenames.Contains(Path.GetFileName(target)) || FileResolves(target, docDir, projectRoot))
                            continue;
                        reason = "no reference doc or file with that name";
                    }
                    else
                    {
                        if (FileResolves(target, docDir, projectRoot)) continue;
                        reason = "file not found";
                    }

                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"Broken link ({reason}): {target}", NormalizePath(doc.AbsolutePath), FindLine(rawLines, target)));
                }

                if (++processed % 8 == 0) await Awaitable.NextFrameAsync(cancellationToken);
            }

            return issues;
        }

        // ── link extraction (skips code fences via the block parser) ────────────────────────────

        private static IEnumerable<string> ExtractLinkTargets(string body)
        {
            var options = new MolcaMarkdownOptions { ActionScheme = "molca://" };
            foreach (var block in MolcaMarkdown.Parse(body))
            {
                if (block.Kind == MolcaMarkdownBlockKind.Code || block.Kind == MolcaMarkdownBlockKind.Rule)
                    continue;

                if (block.Kind == MolcaMarkdownBlockKind.Table)
                {
                    if (block.TableRows == null) continue;
                    foreach (var row in block.TableRows)
                        foreach (var cell in row)
                            foreach (var target in TargetsFromInline(cell, options))
                                yield return target;
                    continue;
                }

                foreach (var target in TargetsFromInline(block.RawText, options))
                    yield return target;
            }
        }

        private static IEnumerable<string> TargetsFromInline(string text, MolcaMarkdownOptions options)
        {
            foreach (var span in MolcaMarkdown.ParseInline(text, options))
            {
                switch (span.Kind)
                {
                    case MolcaMarkdownInlineKind.Link:
                    case MolcaMarkdownInlineKind.Url:
                        yield return span.LinkPath;
                        break;
                    case MolcaMarkdownInlineKind.Action:
                        yield return span.ActionUri;
                        break;
                }
            }
        }

        // ── resolution (mirrors MolcaEditorDocLinks / MolcaMarkdown.OpenFile) ────────────────────

        /// <summary>True for an <c>http</c>/<c>https</c> link (never fetched here).</summary>
        public static bool IsWebLink(string target)
            => target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        /// <summary>Splits a <c>molca://kind/payload</c> URI; false when it is not a well-formed molca link.</summary>
        public static bool TryReadMolcaAction(string uri, out string kind, out string payload)
        {
            kind = null;
            payload = null;
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith("molca://", StringComparison.Ordinal)) return false;

            var action = uri.Substring("molca://".Length);
            var slash = action.IndexOf('/');
            if (slash <= 0 || slash + 1 >= action.Length) return false;

            kind = action.Substring(0, slash);
            payload = action.Substring(slash + 1);
            return true;
        }

        private static bool IsMolcaLinkResolvable(string uri, ICollection<string> knownDocIds, out string reason)
        {
            reason = null;
            if (!TryReadMolcaAction(uri, out var kind, out var payload))
            {
                reason = "malformed molca:// link";
                return false;
            }

            if (string.Equals(kind, "doc", StringComparison.OrdinalIgnoreCase))
            {
                if (knownDocIds.Contains(payload)) return true;
                reason = $"no reference doc with id '{payload}'";
                return false;
            }

            if (string.Equals(kind, "asset", StringComparison.OrdinalIgnoreCase))
            {
                if (AssetResolves(payload)) return true;
                reason = $"asset not found: {payload}";
                return false;
            }

            reason = $"unknown molca:// action '{kind}'";
            return false;
        }

        private static bool AssetResolves(string guidOrPath)
        {
            var path = IsGuid(guidOrPath) ? AssetDatabase.GUIDToAssetPath(guidOrPath) : guidOrPath;
            return !string.IsNullOrEmpty(path) && AssetDatabase.LoadMainAssetAtPath(path) != null;
        }

        private static bool FileResolves(string path, string docDir, string projectRoot)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null) return true;
                if (File.Exists(path)) return true;
                if (!string.IsNullOrEmpty(docDir) && File.Exists(Path.GetFullPath(Path.Combine(docDir, path)))) return true;
                if (!string.IsNullOrEmpty(projectRoot) && File.Exists(Path.Combine(projectRoot, path))) return true;
            }
            catch (Exception)
            {
                // A malformed path is treated as unresolved and reported below.
            }
            return false;
        }

        private static bool IsGuid(string value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (var c in value)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static int FindLine(string[] lines, string needle)
        {
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return i + 1;
            return 0;
        }

        private static string NormalizePath(string absolute)
        {
            var normalized = absolute.Replace('\\', '/');
            var idx = normalized.IndexOf("/Packages/", StringComparison.Ordinal);
            return idx >= 0 ? normalized.Substring(idx + 1) : normalized;
        }
    }
}
