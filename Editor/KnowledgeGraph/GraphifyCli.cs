using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.KnowledgeGraph
{
    /// <summary>The outcome of a graphify CLI invocation.</summary>
    public struct GraphifyResult
    {
        /// <summary>Process exit code; -1 if the process could not be started or timed out.</summary>
        public int ExitCode;
        /// <summary>Captured standard output.</summary>
        public string StdOut;
        /// <summary>Captured standard error.</summary>
        public string StdErr;
        /// <summary>True if the CLI could not be launched at all (graphify not installed / not on PATH).</summary>
        public bool NotFound;

        /// <summary>True when the process ran and returned success.</summary>
        public readonly bool Ok => !NotFound && ExitCode == 0;
    }

    /// <summary>
    /// Thin editor-side wrapper around the external <c>graphify</c> CLI. Molca does not reimplement the
    /// knowledge-graph engine — it feeds graphify a Unity-aware corpus (see the facts exporter) and drives
    /// build / query / path / explain through this helper, surfacing the results to the Molca Assistant and
    /// IDE MCP clients as read-only tools.
    /// </summary>
    /// <remarks>
    /// Runs the CLI through the platform shell (so PATH resolution and a uv/pipx-installed launcher are
    /// found, mirroring <c>McpProxyBuilder</c>). Invocation is marshalled onto a background thread and the
    /// result back onto the main thread, so a slow (LLM-bound) query never blocks the editor or the bridge
    /// listener.
    /// </remarks>
    public static class GraphifyCli
    {
        /// <summary>Default timeout for knowledge-graph builds, in minutes.</summary>
        public const int DefaultBuildTimeoutMinutes = 30;

        /// <summary>Minimum accepted knowledge-graph build timeout, in minutes.</summary>
        public const int MinBuildTimeoutMinutes = 5;

        /// <summary>Maximum accepted knowledge-graph build timeout, in minutes.</summary>
        public const int MaxBuildTimeoutMinutes = 180;

        /// <summary>Default timeout for knowledge-graph builds, in milliseconds.</summary>
        public const int DefaultBuildTimeoutMs = DefaultBuildTimeoutMinutes * 60 * 1000;

        /// <summary>Project root (parent of <c>Assets/</c>) — the working directory for graphify.</summary>
        public static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;

        /// <summary>The graphify output directory (<c>&lt;root&gt;/graphify-out</c>).</summary>
        public static string GraphOutDir => Path.Combine(ProjectRoot, "graphify-out");

        /// <summary>The built graph (<c>&lt;root&gt;/graphify-out/graph.json</c>).</summary>
        public static string GraphJsonPath => Path.Combine(GraphOutDir, "graph.json");

        /// <summary>Where the Unity facts exporter writes its corpus (<c>&lt;root&gt;/graphify-corpus</c>).</summary>
        public static string CorpusDir => Path.Combine(ProjectRoot, "graphify-corpus");

        /// <summary>True if a built graph exists on disk.</summary>
        public static bool GraphExists => File.Exists(GraphJsonPath);

        /// <summary>
        /// Builds the graphify argument string for a (re)build. graphify's default build indexes a single
        /// root and honours <c>.gitignore</c>, so the project root is the right scope: it sweeps in
        /// <c>Assets/</c> (project + SDK content), the embedded <c>Packages/com.molca.core/</c> framework
        /// source — the classes "how does X work" questions are actually about — the generated
        /// <c>graphify-corpus/</c> facts, and the markdown docs, while <c>.gitignore</c> excludes
        /// <c>Library/</c>, the UPM package cache, and other build junk. <c>--out</c> pins the output under
        /// the root; <c>--update</c> makes non-full rebuilds incremental. Must be called on the main thread.
        /// </summary>
        /// <remarks>
        /// A consuming project that installs Core via UPM gets the package source under
        /// <c>Library/PackageCache</c> (gitignored, so not indexed by the root sweep). That's acceptable —
        /// the <c>graphify-corpus/</c> type graph still names every Core extension point; index the package
        /// cache explicitly only if deep Core-internals Q&amp;A is needed there.
        /// </remarks>
        public static string BuildIndexArgs(bool full)
        {
            var cmd = Quote(ProjectRoot) + " --out " + Quote(ProjectRoot);
            if (!full && GraphExists) cmd += " --update";
            return cmd;
        }

        /// <summary>
        /// Converts an optional build timeout in minutes to milliseconds, clamped to Molca's supported
        /// graphify build range.
        /// </summary>
        /// <param name="timeoutMinutes">Optional caller-provided timeout in minutes.</param>
        /// <returns>A timeout in milliseconds suitable for <see cref="RunAsync"/>.</returns>
        public static int ResolveBuildTimeoutMs(int? timeoutMinutes)
        {
            if (!timeoutMinutes.HasValue || timeoutMinutes.Value <= 0)
                return DefaultBuildTimeoutMs;

            var minutes = timeoutMinutes.Value;
            if (minutes < MinBuildTimeoutMinutes) minutes = MinBuildTimeoutMinutes;
            if (minutes > MaxBuildTimeoutMinutes) minutes = MaxBuildTimeoutMinutes;
            return minutes * 60 * 1000;
        }

        /// <summary>Formats a raw graphify output line for compact UI progress display.</summary>
        /// <param name="line">The raw stdout/stderr line from graphify.</param>
        /// <returns>A trimmed line capped to a UI-friendly length, or an empty string for blank input.</returns>
        public static string FormatProgressLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;
            line = line.Trim();
            const int maxLength = 180;
            return line.Length <= maxLength ? line : line.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Runs <c>graphify &lt;arguments&gt;</c> in the project root and returns its captured output.
        /// <paramref name="arguments"/> must already be shell-quoted by the caller (use <see cref="Quote"/>).
        /// </summary>
        public static async Awaitable<GraphifyResult> RunAsync(
            string arguments, CancellationToken cancellationToken, int timeoutMs = 180_000,
            Action<string> onProgressLine = null)
        {
            var root = ProjectRoot;

            // Hop off the main thread: the process is started and awaited here so the editor stays
            // responsive; we marshal back before returning.
            await Awaitable.BackgroundThreadAsync();

            var result = new GraphifyResult { ExitCode = -1 };
            Process process = null;
            try
            {
                string fileName, shellArgs;
                var command = "graphify " + arguments;
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    fileName = "cmd.exe";
                    shellArgs = $"/c \"{command}\"";
                }
                else
                {
                    fileName = "/bin/bash";
                    shellArgs = $"-lc \"{command.Replace("\"", "\\\"")}\"";
                }

                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = shellArgs,
                        WorkingDirectory = root,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var progressLines = new ConcurrentQueue<string>();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    stdout.AppendLine(e.Data);
                    progressLines.Enqueue(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    stderr.AppendLine(e.Data);
                    progressLines.Enqueue(e.Data);
                };

                try
                {
                    process.Start();
                }
                catch
                {
                    // Shell launched but graphify itself missing usually still starts the shell; a true
                    // launch failure (no shell) lands here.
                    result.NotFound = true;
                    await Awaitable.MainThreadAsync();
                    return result;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Cooperative wait so cancellation/timeout can abort a hung build.
                var startedAt = DateTime.UtcNow;
                var deadline = startedAt.AddMilliseconds(timeoutMs);
                var nextHeartbeat = startedAt.AddSeconds(15);
                while (!process.HasExited)
                {
                    await FlushProgressAsync(progressLines, onProgressLine);

                    if (cancellationToken.IsCancellationRequested || DateTime.UtcNow > deadline)
                    {
                        try { process.Kill(); } catch { /* already gone */ }
                        result.StdErr = cancellationToken.IsCancellationRequested
                            ? "graphify call cancelled."
                            : $"graphify call timed out after {timeoutMs} ms.";
                        await ReportProgressAsync(result.StdErr, onProgressLine);
                        await Awaitable.MainThreadAsync();
                        return result;
                    }

                    var now = DateTime.UtcNow;
                    if (now >= nextHeartbeat)
                    {
                        var elapsed = (int)Math.Max(1, (now - startedAt).TotalMinutes);
                        var limit = Math.Max(1, timeoutMs / 60000);
                        await ReportProgressAsync($"Still building graph... {elapsed}m elapsed (timeout {limit}m).", onProgressLine);
                        nextHeartbeat = now.AddSeconds(15);
                    }

                    Thread.Sleep(250);
                }

                await FlushProgressAsync(progressLines, onProgressLine);

                result.ExitCode = process.ExitCode;
                result.StdOut = stdout.ToString().TrimEnd();
                result.StdErr = stderr.ToString().TrimEnd();

                // A shell "command not found" exit (127 on bash, 1 on cmd with a clear message) means the
                // CLI isn't installed — flag it so callers can give an actionable error.
                if (!string.IsNullOrEmpty(result.StdErr) &&
                    (result.StdErr.Contains("not recognized") || result.StdErr.Contains("not found") ||
                     result.StdErr.Contains("command not found")))
                {
                    result.NotFound = true;
                }
            }
            catch (Exception ex)
            {
                result.StdErr = ex.Message;
            }
            finally
            {
                process?.Dispose();
            }

            await Awaitable.MainThreadAsync();
            return result;
        }

        private static async Awaitable FlushProgressAsync(ConcurrentQueue<string> progressLines, Action<string> onProgressLine)
        {
            if (onProgressLine == null || progressLines == null || progressLines.IsEmpty)
                return;

            var lines = new List<string>(12);
            while (lines.Count < 12 && progressLines.TryDequeue(out var line))
            {
                line = FormatProgressLine(line);
                if (!string.IsNullOrEmpty(line)) lines.Add(line);
            }

            if (lines.Count == 0) return;
            await Awaitable.MainThreadAsync();
            try
            {
                foreach (var line in lines)
                    onProgressLine(line);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Molca KG] Progress callback failed: {ex.Message}");
            }
            await Awaitable.BackgroundThreadAsync();
        }

        private static async Awaitable ReportProgressAsync(string line, Action<string> onProgressLine)
        {
            if (onProgressLine == null) return;
            line = FormatProgressLine(line);
            if (string.IsNullOrEmpty(line)) return;

            await Awaitable.MainThreadAsync();
            try { onProgressLine(line); }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[Molca KG] Progress callback failed: {ex.Message}"); }
            await Awaitable.BackgroundThreadAsync();
        }

        /// <summary>Shell-quotes an argument value (wraps in double quotes, escaping inner quotes).</summary>
        public static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
