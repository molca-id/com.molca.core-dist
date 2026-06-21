using System;
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
        /// Runs <c>graphify &lt;arguments&gt;</c> in the project root and returns its captured output.
        /// <paramref name="arguments"/> must already be shell-quoted by the caller (use <see cref="Quote"/>).
        /// </summary>
        public static async Awaitable<GraphifyResult> RunAsync(
            string arguments, CancellationToken cancellationToken, int timeoutMs = 180_000)
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
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

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
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested || DateTime.UtcNow > deadline)
                    {
                        try { process.Kill(); } catch { /* already gone */ }
                        result.StdErr = cancellationToken.IsCancellationRequested
                            ? "graphify call cancelled."
                            : $"graphify call timed out after {timeoutMs} ms.";
                        await Awaitable.MainThreadAsync();
                        return result;
                    }
                    Thread.Sleep(50);
                }

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

        /// <summary>Shell-quotes an argument value (wraps in double quotes, escaping inner quotes).</summary>
        public static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
