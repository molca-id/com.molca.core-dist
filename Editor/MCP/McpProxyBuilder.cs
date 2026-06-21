using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Builds the TypeScript MCP proxy from the editor by shelling out to
    /// <c>npm install &amp;&amp; npm run build</c>, so a developer never has to leave Unity to produce
    /// <c>dist/index.js</c>. The proxy source ships inside the package under <c>Tools~/molca-mcp</c>
    /// (a <c>~</c> folder Unity does not import but UPM still distributes). Because an installed package
    /// lives in a read-only <c>Library/PackageCache</c>, the source is first copied to a writable
    /// project-local folder (<c>&lt;project&gt;/molca-mcp</c>) and built there.
    /// </summary>
    /// <remarks>
    /// Mirrors the Content Package deploy process pattern: the process callbacks run on a thread-pool
    /// thread and only buffer text; an <see cref="EditorApplication.update"/> poll drains the buffer and
    /// finalizes status on the main thread (the Sprint 12.9 hazard).
    /// </remarks>
    public static class McpProxyBuilder
    {
        // Source files copied from the package into the writable build folder (node_modules/dist excluded).
        private static readonly string[] SourceFiles =
            { "package.json", "package-lock.json", "tsconfig.json", "README.md" };

        private static Process _process;
        private static readonly object Lock = new object();
        private static readonly StringBuilder Pending = new StringBuilder();
        private static readonly StringBuilder Log = new StringBuilder();

        /// <summary>True while a build is in progress.</summary>
        public static bool IsBuilding { get; private set; }

        /// <summary>Last status line (e.g. "Building…", "Build complete.").</summary>
        public static string Status { get; private set; } = string.Empty;

        /// <summary>True if the most recent completed build succeeded.</summary>
        public static bool LastBuildOk { get; private set; }

        /// <summary>Accumulated build log for display.</summary>
        public static string LogText { get { lock (Lock) return Log.ToString(); } }

        /// <summary>Raised on the main thread whenever status/log changes, so UIs can repaint.</summary>
        public static event Action Changed;

        /// <summary>
        /// The proxy source shipped inside the package (<c>&lt;package&gt;/Tools~/molca-mcp</c>), resolved
        /// from the assembly's package. Empty if the package cannot be located.
        /// </summary>
        public static string PackageSourceDirectory
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(McpProxyBuilder).Assembly);
                return info == null ? string.Empty : Path.Combine(info.resolvedPath, "Tools~", "molca-mcp");
            }
        }

        /// <summary>
        /// Writable build location (<c>&lt;project&gt;/molca-mcp</c>). The package source is copied here
        /// before building, since installed packages are read-only. This is the path <c>.mcp.json</c>
        /// should point at (<c>molca-mcp/dist/index.js</c>).
        /// </summary>
        public static string ProxyDirectory
        {
            get
            {
                // Application.dataPath ends in "/Assets"; the project root is its parent.
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
                return Path.Combine(projectRoot, "molca-mcp");
            }
        }

        /// <summary>True if <c>dist/index.js</c> exists in the writable build folder.</summary>
        public static bool IsBuilt => File.Exists(Path.Combine(ProxyDirectory, "dist", "index.js"));

        /// <summary>
        /// Starts <c>npm install &amp;&amp; npm run build</c> in the proxy directory. No-op if a build is
        /// already running. Output streams to the log and the Unity console; completion shows a dialog.
        /// </summary>
        public static void Build()
        {
            if (IsBuilding)
                return;

            var source = PackageSourceDirectory;
            if (string.IsNullOrEmpty(source) || !File.Exists(Path.Combine(source, "package.json")))
            {
                EditorUtility.DisplayDialog("Build MCP Proxy",
                    "Could not locate the proxy source in the package (Tools~/molca-mcp).\n\n"
                    + $"Looked in: {source}", "OK");
                return;
            }

            var dir = ProxyDirectory;
            try
            {
                CopySource(source, dir);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Build MCP Proxy",
                    $"Failed to copy the proxy source to the build folder:\n{dir}\n\n{ex.Message}", "OK");
                return;
            }

            lock (Lock) Log.Clear();
            Status = "Starting…";
            LastBuildOk = false;
            IsBuilding = true;

            // Run through the platform shell so PATH resolution and "&&" chaining work, and npm's
            // platform launcher (npm.cmd on Windows) is found.
            string fileName, arguments;
            const string chained = "npm install --no-fund --no-audit && npm run build";
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                fileName = "cmd.exe";
                arguments = $"/c \"{chained}\"";
            }
            else
            {
                fileName = "/bin/bash";
                arguments = $"-lc \"{chained}\"";
            }

            AppendLog($"$ {chained}");
            AppendLog($"(cwd: {dir})");
            AppendLog(string.Empty);

            try
            {
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = dir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                // Off the main thread — buffer only; the poll drains and repaints.
                _process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (Lock) Pending.AppendLine(e.Data);
                };
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (Lock) Pending.AppendLine("[err] " + e.Data);
                };

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                Status = "Building…";
                EditorApplication.update += Poll;
            }
            catch (Exception ex)
            {
                IsBuilding = false;
                Status = "Failed to start npm.";
                AppendLog($"[err] {ex.Message}");
                _process = null;
                EditorUtility.DisplayDialog("Build MCP Proxy",
                    "Could not start npm. Is Node.js installed and on PATH?\n\n" + ex.Message, "OK");
            }

            Changed?.Invoke();
        }

        private static void Poll()
        {
            var changed = false;

            lock (Lock)
            {
                if (Pending.Length > 0)
                {
                    Log.Append(Pending);
                    Pending.Clear();
                    changed = true;
                }
            }

            if (_process == null || _process.HasExited)
            {
                EditorApplication.update -= Poll;

                if (_process != null && IsBuilding)
                {
                    LastBuildOk = _process.ExitCode == 0;
                    Status = LastBuildOk ? "Build complete." : $"Build failed (exit {_process.ExitCode}).";
                    Debug.Log($"[Molca MCP] {Status}");
                    EditorUtility.DisplayDialog("Build MCP Proxy",
                        LastBuildOk
                            ? "Proxy built successfully (dist/index.js)."
                            : $"Build failed (exit {_process.ExitCode}). See the log for details.",
                        "OK");
                }

                _process?.Dispose();
                _process = null;
                IsBuilding = false;
                changed = true;
            }

            if (changed) Changed?.Invoke();
        }

        /// <summary>
        /// Copies the proxy source (top-level files + <c>src/</c>) from the read-only package into the
        /// writable build folder, overwriting source files but preserving any existing
        /// <c>node_modules</c>/<c>dist</c> so re-builds are incremental.
        /// </summary>
        private static void CopySource(string source, string target)
        {
            Directory.CreateDirectory(target);

            foreach (var file in SourceFiles)
            {
                var src = Path.Combine(source, file);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(target, file), overwrite: true);
            }

            // Mirror src/ fresh so renamed/removed files don't linger.
            var srcDir = Path.Combine(source, "src");
            var dstDir = Path.Combine(target, "src");
            if (Directory.Exists(dstDir))
                Directory.Delete(dstDir, recursive: true);
            if (Directory.Exists(srcDir))
            {
                Directory.CreateDirectory(dstDir);
                foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(srcDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var dst = Path.Combine(dstDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? dstDir);
                    File.Copy(f, dst, overwrite: true);
                }
            }
        }

        private static void AppendLog(string line)
        {
            lock (Lock) Log.AppendLine(line);
        }
    }
}
