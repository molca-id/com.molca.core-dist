using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Molca.App.Editor.ShaderVariant
{
    /// <summary>
    /// A single compiled variant entry, as recorded by <see cref="ShaderVariantBuildRecorder"/>
    /// and written to <see cref="ShaderVariantRecord.RecordPath"/>.
    /// </summary>
    [Serializable]
    public class CompiledVariantEntry
    {
        /// <summary>Value of <c>Shader.name</c> for the shader being compiled.</summary>
        public string shaderName;

        /// <summary>Integer value of <c>UnityEngine.Rendering.PassType</c>.</summary>
        public int passType;

        /// <summary>Space-separated, sorted keyword string. Empty string = no keywords.</summary>
        public string keywords;
    }

    /// <summary>
    /// Maps a generated <c>.shadervariants</c> asset (by filename) back to its original
    /// <see cref="CompiledVariantEntry"/> data so the prune pass can cross-reference it.
    /// Written alongside the manifest by <c>ShaderVariantSplitTool</c>.
    /// </summary>
    [Serializable]
    public class VariantIndexEntry
    {
        /// <summary>Asset filename without extension (e.g. <c>variant_00042</c>).</summary>
        public string assetName;

        /// <summary>Shader name matching <see cref="CompiledVariantEntry.shaderName"/>.</summary>
        public string shaderName;

        /// <summary>Integer pass type matching <see cref="CompiledVariantEntry.passType"/>.</summary>
        public int passType;

        /// <summary>Sorted keyword string matching <see cref="CompiledVariantEntry.keywords"/>.</summary>
        public string keywords;
    }

    /// <summary>
    /// Root container for the build recorder output and the split-tool index.
    /// </summary>
    [Serializable]
    public class ShaderVariantRecordData
    {
        public List<CompiledVariantEntry> compiledVariants = new();
    }

    [Serializable]
    public class ShaderVariantIndexData
    {
        public List<VariantIndexEntry> entries = new();
    }

    /// <summary>
    /// Paths and helpers for the record/index files written outside the Assets folder.
    /// </summary>
    public static class ShaderVariantRecord
    {
        /// <summary>Path where <see cref="ShaderVariantBuildRecorder"/> writes compiled variant data.</summary>
        public static string RecordPath => Path.Combine(Application.dataPath, "..", "Library", "ShaderVariantRecord.json");

        /// <summary>
        /// Loads the record written by the last player build.
        /// Returns null and logs a warning if no record exists yet.
        /// </summary>
        public static ShaderVariantRecordData LoadRecord()
        {
            if (!File.Exists(RecordPath))
            {
                Debug.LogWarning("[ShaderVariantRecord] No build record found at " +
                                 $"'{RecordPath}'. Run a player build first to record compiled variants.");
                return null;
            }
            return JsonUtility.FromJson<ShaderVariantRecordData>(File.ReadAllText(RecordPath));
        }

        /// <summary>Saves <paramref name="data"/> to <see cref="RecordPath"/>.</summary>
        public static void SaveRecord(ShaderVariantRecordData data)
        {
            File.WriteAllText(RecordPath, JsonUtility.ToJson(data, prettyPrint: true));
        }

        /// <summary>
        /// Returns the index file path for a given output folder.
        /// Stored inside the output folder so it stays with the generated assets.
        /// </summary>
        public static string IndexPath(string outputFolder) =>
            Path.Combine(outputFolder, "ShaderVariantIndex.json");

        /// <summary>Loads the index written by <c>ShaderVariantSplitTool</c>, or null if missing.</summary>
        public static ShaderVariantIndexData LoadIndex(string outputFolder)
        {
            var path = IndexPath(outputFolder);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[ShaderVariantRecord] No index found at '{path}'. " +
                                 "Re-run the split tool to regenerate it.");
                return null;
            }
            return JsonUtility.FromJson<ShaderVariantIndexData>(File.ReadAllText(path));
        }

        /// <summary>Saves the index to <paramref name="outputFolder"/>.</summary>
        public static void SaveIndex(string outputFolder, ShaderVariantIndexData data)
        {
            File.WriteAllText(IndexPath(outputFolder), JsonUtility.ToJson(data, prettyPrint: true));
        }
    }
}
