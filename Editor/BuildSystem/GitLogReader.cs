using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Molca.Editor
{
    /// <summary>
    /// Utility for reading git log data. Shells out to git via Process.
    /// </summary>
    public static class GitLogReader
    {
        /// <summary>
        /// Returns true if the given commit hash exists in the local repository.
        /// </summary>
        public static bool IsCommitAvailable(string projectRoot, string commitHash)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
                return false;
            return TryRunGit(projectRoot, $"cat-file -e {commitHash}^{{commit}}", out _);
        }

        /// <summary>
        /// Reads the short commit hash and branch name of <paramref name="projectRoot"/>'s HEAD.
        /// </summary>
        /// <param name="projectRoot">Absolute path to the git repository root.</param>
        /// <param name="commit">The short hash, or empty when unavailable.</param>
        /// <param name="branch">The branch name, or empty when unavailable.</param>
        /// <remarks>
        /// Never throws and never reports failure: build provenance is best-effort, and a project that is
        /// not a git repository still builds. Three call sites had their own copy of this pair of git
        /// invocations — the build manifest, the embedded build-info asset, and the build record — which is
        /// three chances for one of them to drift into recording a different commit than the others for
        /// the same build.
        /// </remarks>
        public static void ReadProvenance(string projectRoot, out string commit, out string branch)
        {
            commit = string.Empty;
            branch = string.Empty;
            if (string.IsNullOrEmpty(projectRoot))
                return;

            if (TryRunGit(projectRoot, "rev-parse --short HEAD", out var c))
                commit = c.Trim();
            if (TryRunGit(projectRoot, "rev-parse --abbrev-ref HEAD", out var b))
                branch = b.Trim();
        }

        /// <summary>
        /// Returns true when <paramref name="projectRoot"/> is inside a git work tree.
        /// </summary>
        /// <param name="projectRoot">Absolute path to check.</param>
        /// <remarks>
        /// Distinguishing "not a repository" from "the command failed" matters to callers that refuse to
        /// act rather than proceed — a release that cannot tag should say which of the two it hit.
        /// </remarks>
        public static bool IsGitRepository(string projectRoot) =>
            TryRunGit(projectRoot, "rev-parse --is-inside-work-tree", out var inside) &&
            inside.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true when <paramref name="tagName"/> already exists in the local repository.
        /// </summary>
        /// <param name="projectRoot">Absolute path to the git repository root.</param>
        /// <param name="tagName">The tag to look for, e.g. <c>v1.4.0</c>.</param>
        /// <remarks>
        /// Checked against the full <c>refs/tags/</c> path so a branch of the same name is not mistaken
        /// for a tag.
        /// </remarks>
        public static bool TagExists(string projectRoot, string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return false;
            return TryRunGit(projectRoot, $"rev-parse -q --verify refs/tags/{tagName}", out _);
        }

        /// <summary>
        /// Returns commit subject lines between <paramref name="sinceHash"/> and HEAD.
        /// Falls back to the last 10 commits if <paramref name="sinceHash"/> is null or unavailable.
        /// </summary>
        /// <param name="projectRoot">Absolute path to the git repository root.</param>
        /// <param name="sinceHash">Exclusive lower bound commit hash, or null for recent commits.</param>
        /// <param name="headHash">The resolved HEAD hash at time of call.</param>
        /// <param name="heading">Human-readable section heading for the log.</param>
        public static List<string> GetCommitMessages(string projectRoot, string sinceHash, out string headHash, out string heading)
        {
            headHash = string.Empty;
            heading = string.Empty;
            var commits = new List<string>();

            if (!TryRunGit(projectRoot, "rev-parse HEAD", out var headOutput))
                return commits;

            headHash = headOutput.Trim();
            if (string.IsNullOrEmpty(headHash))
                return commits;

            string logRange;
            if (!string.IsNullOrWhiteSpace(sinceHash))
            {
                logRange = $"{sinceHash}..HEAD";
                heading = "### Commits since last build";
            }
            else
            {
                logRange = "HEAD~10..HEAD";
                heading = "### Recent commits";
            }

            if (!TryRunGit(projectRoot, $"log {logRange} --pretty=format:%s (%h)", out var logOutput))
            {
                if (!string.IsNullOrWhiteSpace(sinceHash) &&
                    TryRunGit(projectRoot, "log -n 10 --pretty=format:%s (%h)", out logOutput))
                    heading = "### Recent commits";
                else
                    return commits;
            }

            foreach (var line in logOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                commits.Add(line.Trim());

            return commits;
        }

        /// <summary>Maximum time a git subprocess may run before it is killed.</summary>
        private const int GitTimeoutMs = 10_000;

        /// <summary>
        /// Runs a git command in the given directory. Returns true on exit code 0.
        /// </summary>
        /// <remarks>
        /// stderr is drained asynchronously (draining only stdout can deadlock when the
        /// stderr pipe buffer fills), the process is killed after <see cref="GitTimeoutMs"/>,
        /// and stderr text is surfaced in the warning log on failure.
        /// </remarks>
        public static bool TryRunGit(string projectRoot, string arguments, out string output)
        {
            output = string.Empty;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    var stderr = new StringBuilder();
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                        {
                            lock (stderr) stderr.AppendLine(e.Data);
                        }
                    };

                    process.Start();
                    process.BeginErrorReadLine();

                    // Safe to read stdout synchronously: stderr drains on its own thread.
                    output = process.StandardOutput.ReadToEnd();

                    if (!process.WaitForExit(GitTimeoutMs))
                    {
                        try { process.Kill(); } catch { /* already exited */ }
                        UnityEngine.Debug.LogWarning(
                            $"git {arguments} timed out after {GitTimeoutMs / 1000}s and was killed.");
                        return false;
                    }

                    // Second parameterless wait flushes pending async stderr events.
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        string errorText;
                        lock (stderr) errorText = stderr.ToString().Trim();
                        UnityEngine.Debug.LogWarning(
                            $"git {arguments} exited with code {process.ExitCode}." +
                            (errorText.Length > 0 ? $"\n{errorText}" : string.Empty));
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"git {arguments} failed to run: {ex.Message}");
                return false;
            }
        }
    }
}
