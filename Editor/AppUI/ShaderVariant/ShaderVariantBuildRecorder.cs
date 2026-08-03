using System.Collections.Generic;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Molca.App.Editor.ShaderVariant
{
    /// <summary>
    /// Hooks into the player build pipeline via <see cref="IPreprocessShaders"/> to record every
    /// shader variant that Unity actually compiles (i.e., was not stripped).
    /// </summary>
    /// <remarks>
    /// The record is written to <c>Library/ShaderVariantRecord.json</c> after each build and is
    /// consumed by the "Prune Against Last Build" pass in <c>ShaderVariantSplitTool</c>.
    ///
    /// <para>
    /// <b>How it works:</b> Unity calls <see cref="OnProcessShader"/> once per shader pass during a
    /// player build, passing the list of <see cref="ShaderCompilerData"/> entries that remain after
    /// stripping. Each entry's <see cref="ShaderCompilerData.shaderKeywordSet"/> gives the full set
    /// of keywords active for that variant. We record (shaderName, passType, sortedKeywords) tuples.
    /// </para>
    ///
    /// <para>
    /// <b>Ordering:</b> <see cref="callbackOrder"/> is set to <c>int.MaxValue</c> so this recorder
    /// runs after all user-defined strippers — we record what actually survives, not what was
    /// submitted before custom stripping.
    /// </para>
    /// </remarks>
    public class ShaderVariantBuildRecorder : IPreprocessShaders
    {
        // Run last so we see variants after all custom strippers have had a chance to remove entries.
        public int callbackOrder => int.MaxValue;

        // Accumulates across all OnProcessShader calls for the current build.
        private static readonly List<CompiledVariantEntry> _recorded = new();
        private static bool _recording;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> shaderCompilerData)
        {
            if (!_recording)
            {
                _recorded.Clear();
                _recording = true;
                RegisterBuildCompletionCallback();
            }

            int passType = (int)snippet.passType;
            foreach (var data in shaderCompilerData)
            {
                var keywords = data.shaderKeywordSet.GetShaderKeywords();
                var sortedKeywords = string.Join(" ",
                    keywords.Select(k => k.name)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .OrderBy(n => n, System.StringComparer.Ordinal));

                _recorded.Add(new CompiledVariantEntry
                {
                    shaderName = shader.name,
                    passType   = passType,
                    keywords   = sortedKeywords
                });
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────
        // Build completion — flush record to disk
        // ──────────────────────────────────────────────────────────────────────────────

        private static bool _callbackRegistered;

        private static void RegisterBuildCompletionCallback()
        {
            if (_callbackRegistered) return;
            _callbackRegistered = true;
            UnityEditor.BuildPlayerWindow.RegisterGetBuildPlayerOptionsHandler(options => options);
            // Use EditorApplication.update to detect when _recording becomes stale (build ends).
            // We hook BuildPlayerHandler via IPostprocessBuildWithReport instead.
        }

        // IPostprocessBuildWithReport can't be on the same class as IPreprocessShaders due to
        // separate callback ordering; delegate flush to a companion class.
        internal static void FlushRecord()
        {
            if (!_recording) return;
            _recording = false;
            _callbackRegistered = false;

            var data = new ShaderVariantRecordData { compiledVariants = new List<CompiledVariantEntry>(_recorded) };
            ShaderVariantRecord.SaveRecord(data);
            _recorded.Clear();

            Debug.Log($"[ShaderVariantBuildRecorder] Recorded {data.compiledVariants.Count} compiled variants " +
                      $"→ {ShaderVariantRecord.RecordPath}");
        }
    }

    /// <summary>
    /// Flushes the <see cref="ShaderVariantBuildRecorder"/> accumulated data to disk after
    /// a player build completes (success or failure).
    /// </summary>
    public class ShaderVariantBuildRecorderFlusher : UnityEditor.Build.IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            ShaderVariantBuildRecorder.FlushRecord();
        }
    }
}
