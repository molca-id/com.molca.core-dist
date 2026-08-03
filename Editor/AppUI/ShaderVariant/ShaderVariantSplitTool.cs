using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Molca.App.Preload;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Molca.App.Editor.ShaderVariant
{
    /// <summary>
    /// Editor window that generates one-variant-per-file <see cref="ShaderVariantCollection"/>
    /// assets and populates a <see cref="ShaderVariantManifest"/> for use with
    /// <see cref="ShaderWarmupCheck"/>.
    /// </summary>
    /// <remarks>
    /// Open via <b>Molca / Shader Variant Split Tool</b>.
    /// <para>
    /// Writes raw YAML directly rather than using the C# API because
    /// <c>ShaderVariantCollection.ShaderVariant()</c> throws <c>ArgumentException</c> for many
    /// valid URP <c>multi_compile</c> combinations on freshly-created in-memory collections in
    /// Unity 6000.x. Disk-loaded collections accept the same combinations without error.
    /// </para>
    /// <para>
    /// After generating, a <c>ShaderVariantIndex.json</c> is written to the output folder mapping
    /// each <c>variant_NNNNN</c> asset back to its (shaderName, passType, keywords). This index
    /// is required by the <b>Prune Against Last Build</b> pass.
    /// </para>
    /// </remarks>
    public class ShaderVariantSplitTool : EditorWindow
    {
        private const int WarningThreshold = 500;

        [SerializeField] private List<ShaderEntry> _entries = new();
        [SerializeField] private ShaderVariantManifest _manifest;
        [SerializeField] private string _outputFolder = "Assets/ShaderVariants";

        private SerializedObject _serializedSelf;
        private SerializedProperty _entriesProp;
        private Vector2 _scroll;
        private bool _showPruneSection;

        [MenuItem("Molca/SDK/Shader Variant Split Tool", priority = 90)]
        public static void Open() => GetWindow<ShaderVariantSplitTool>("Shader Variant Split Tool");

        private void OnEnable()
        {
            _serializedSelf = new SerializedObject(this);
            _entriesProp = _serializedSelf.FindProperty(nameof(_entries));
        }

        private void OnGUI()
        {
            _serializedSelf.Update();

            EditorGUILayout.LabelField("Shader Variant Split Tool", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            _manifest = (ShaderVariantManifest)EditorGUILayout.ObjectField(
                "Manifest", _manifest, typeof(ShaderVariantManifest), false);
            _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shader Entries", EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawEntries();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            DrawActions();
            EditorGUILayout.Space();
            DrawPruneSection();

            _serializedSelf.ApplyModifiedProperties();
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // UI drawing
        // ──────────────────────────────────────────────────────────────────────────────────

        private void DrawEntries()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"Entry {i}", EditorStyles.boldLabel);
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    _entries.RemoveAt(i--);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    continue;
                }
                EditorGUILayout.EndHorizontal();

                entry.shader = (Shader)EditorGUILayout.ObjectField("Shader", entry.shader, typeof(Shader), false);
                entry.materialScanFolder = EditorGUILayout.TextField("Material Scan Folder", entry.materialScanFolder);
                entry.includeForwardLit   = EditorGUILayout.Toggle("Forward Lit (Pass 13)",   entry.includeForwardLit);
                entry.includeShadowCaster = EditorGUILayout.Toggle("Shadow Caster (Pass 8)", entry.includeShadowCaster);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Material Keywords (shader_feature)");
                DrawStringList(entry.materialKeywords);

                if (GUILayout.Button("Scan Materials"))
                    ScanMaterials(entry);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Pipeline Axes (multi_compile)");
                DrawAxes(entry.pipelineAxes);
                if (GUILayout.Button("+ Add Axis"))
                    entry.pipelineAxes.Add(new KeywordAxis { label = "New Axis", values = new List<string> { "" }, enabled = new List<bool> { true } });

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            if (GUILayout.Button("+ Add Shader Entry"))
                _entries.Add(new ShaderEntry());
        }

        private void DrawStringList(List<string> list)
        {
            EditorGUI.indentLevel++;
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                list[i] = EditorGUILayout.TextField(list[i]);
                if (GUILayout.Button("-", GUILayout.Width(20)))
                    list.RemoveAt(i--);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ Add Keyword", GUILayout.Width(120)))
                list.Add(string.Empty);
            EditorGUI.indentLevel--;
        }

        private void DrawAxes(List<KeywordAxis> axes)
        {
            EditorGUI.indentLevel++;
            for (int a = 0; a < axes.Count; a++)
            {
                var axis = axes[a];
                EditorGUILayout.BeginHorizontal();
                axis.label = EditorGUILayout.TextField("Axis", axis.label);
                if (GUILayout.Button("-", GUILayout.Width(20)))
                {
                    axes.RemoveAt(a--);
                    EditorGUILayout.EndHorizontal();
                    continue;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel++;
                for (int v = 0; v < axis.values.Count; v++)
                {
                    while (axis.enabled.Count <= v) axis.enabled.Add(true);

                    EditorGUILayout.BeginHorizontal();
                    axis.enabled[v] = EditorGUILayout.Toggle(axis.enabled[v], GUILayout.Width(18));
                    axis.values[v]  = EditorGUILayout.TextField(axis.values[v]);
                    if (GUILayout.Button("-", GUILayout.Width(20)))
                    {
                        axis.values.RemoveAt(v);
                        axis.enabled.RemoveAt(v--);
                    }
                    EditorGUILayout.EndHorizontal();
                }
                if (GUILayout.Button("+ Value", GUILayout.Width(80)))
                {
                    axis.values.Add(string.Empty);
                    axis.enabled.Add(true);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;
        }

        private void DrawActions()
        {
            if (_manifest == null)
                EditorGUILayout.HelpBox("Assign a ShaderVariantManifest asset before generating.", MessageType.Warning);

            EditorGUI.BeginDisabledGroup(_manifest == null || _entries.Count == 0);
            if (GUILayout.Button("Generate & Populate Manifest"))
                GenerateVariants();
            EditorGUI.EndDisabledGroup();
        }

        private void DrawPruneSection()
        {
            _showPruneSection = EditorGUILayout.Foldout(_showPruneSection, "Prune Against Last Build", true, EditorStyles.foldoutHeader);
            if (!_showPruneSection) return;

            EditorGUILayout.HelpBox(
                "Removes variants from the manifest that were stripped during the last player build.\n\n" +
                "Prerequisites:\n" +
                "1. Run a player build (ShaderVariantBuildRecorder hooks in automatically).\n" +
                "2. Click Prune — unused .shadervariants files are deleted and the manifest is rebuilt.",
                MessageType.Info);

            bool recordExists = File.Exists(ShaderVariantRecord.RecordPath);
            bool indexExists  = File.Exists(ShaderVariantRecord.IndexPath(_outputFolder));

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Build record:", recordExists ? "✓ Found" : "✗ Missing");
            EditorGUILayout.LabelField("Variant index:", indexExists ? "✓ Found" : "✗ Missing");
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginDisabledGroup(_manifest == null || !recordExists || !indexExists);
            if (GUILayout.Button("Prune Unused Variants"))
                PruneVariants();
            EditorGUI.EndDisabledGroup();
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Scan materials
        // ──────────────────────────────────────────────────────────────────────────────────

        private void ScanMaterials(ShaderEntry entry)
        {
            if (entry.shader == null)
            {
                Debug.LogWarning("[ShaderVariantSplitTool] Shader is null — assign a shader first.");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Material", new[] { entry.materialScanFolder });
            var found = new HashSet<string>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat  = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader != entry.shader) continue;

                foreach (var kw in mat.shaderKeywords)
                    if (!string.IsNullOrEmpty(kw))
                        found.Add(kw);
            }

            // Merge with existing; preserve manual additions.
            foreach (var kw in found)
                if (!entry.materialKeywords.Contains(kw))
                    entry.materialKeywords.Add(kw);

            Debug.Log($"[ShaderVariantSplitTool] Scanned {guids.Length} materials under '{entry.materialScanFolder}', " +
                      $"found {found.Count} shader_feature keywords.");
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Generate variants
        // ──────────────────────────────────────────────────────────────────────────────────

        private void GenerateVariants()
        {
            var allVariants = new List<(Shader shader, VariantSpec spec)>();
            foreach (var entry in _entries)
            {
                if (entry.shader == null) continue;
                var specs = VariantCombinator.BuildMatrix(entry);
                foreach (var spec in specs)
                    allVariants.Add((entry.shader, spec));
            }

            if (allVariants.Count == 0)
            {
                EditorUtility.DisplayDialog("No Variants", "No variants were generated. Check shader entries.", "OK");
                return;
            }

            if (allVariants.Count > WarningThreshold)
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Large Variant Count",
                    $"This will generate {allVariants.Count} .shadervariants files in '{_outputFolder}'.\n\nProceed?",
                    "Generate", "Cancel");
                if (!proceed) return;
            }

            if (!_outputFolder.StartsWith("Assets"))
            {
                Debug.LogError($"[ShaderVariantSplitTool] Output folder must be under Assets/. Got: {_outputFolder}");
                return;
            }
            Directory.CreateDirectory(_outputFolder);

            var indexEntries = new List<VariantIndexEntry>(allVariants.Count);

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < allVariants.Count; i++)
                {
                    var (shader, spec) = allVariants[i];
                    var assetName = $"variant_{i:D5}";
                    var filePath  = Path.Combine(_outputFolder, assetName + ".shadervariants");
                    File.WriteAllText(filePath, BuildYaml(assetName, shader, spec), Encoding.UTF8);

                    indexEntries.Add(new VariantIndexEntry
                    {
                        assetName  = assetName,
                        shaderName = shader.name,
                        passType   = (int)spec.PassType,
                        keywords   = spec.Keywords
                    });
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // Write index before Refresh so it's available immediately after.
            ShaderVariantRecord.SaveIndex(_outputFolder, new ShaderVariantIndexData { entries = indexEntries });

            AssetDatabase.Refresh();

            var collections = new List<ShaderVariantCollection>(allVariants.Count);
            for (int i = 0; i < allVariants.Count; i++)
            {
                var assetPath = Path.Combine(_outputFolder, $"variant_{i:D5}.shadervariants").Replace('\\', '/');
                var collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(assetPath);
                if (collection != null)
                    collections.Add(collection);
                else
                    Debug.LogWarning($"[ShaderVariantSplitTool] Failed to load imported asset at '{assetPath}'.");
            }

            _manifest.collections = collections;
            EditorUtility.SetDirty(_manifest);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ShaderVariantSplitTool] Generated {collections.Count} variant assets. " +
                      $"Index written. Manifest '{_manifest.name}' updated.");
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Prune variants
        // ──────────────────────────────────────────────────────────────────────────────────

        private void PruneVariants()
        {
            var record = ShaderVariantRecord.LoadRecord();
            var index  = ShaderVariantRecord.LoadIndex(_outputFolder);
            if (record == null || index == null) return;

            // Build a lookup set from the build record: "shaderName|passType|keywords"
            var compiledSet = new HashSet<string>(
                record.compiledVariants.Select(e => $"{e.shaderName}|{e.passType}|{e.keywords}"),
                System.StringComparer.Ordinal);

            var toDelete = new List<VariantIndexEntry>();
            var toKeep   = new List<VariantIndexEntry>();

            foreach (var entry in index.entries)
            {
                var key = $"{entry.shaderName}|{entry.passType}|{entry.keywords}";
                if (compiledSet.Contains(key))
                    toKeep.Add(entry);
                else
                    toDelete.Add(entry);
            }

            if (toDelete.Count == 0)
            {
                EditorUtility.DisplayDialog("Prune Complete", "No unused variants found — manifest is already lean.", "OK");
                return;
            }

            bool confirm = EditorUtility.DisplayDialog(
                "Prune Unused Variants",
                $"Found {toDelete.Count} unused variants out of {index.entries.Count}.\n" +
                $"Delete {toDelete.Count} .shadervariants files and rebuild the manifest?",
                "Prune", "Cancel");
            if (!confirm) return;

            // Delete unused files.
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in toDelete)
                {
                    var assetPath = Path.Combine(_outputFolder, entry.assetName + ".shadervariants").Replace('\\', '/');
                    if (AssetDatabase.DeleteAsset(assetPath))
                        continue;
                    // Fall back to File.Delete for files not tracked by AssetDatabase.
                    var fullPath = Path.GetFullPath(assetPath);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();

            // Rebuild manifest from surviving entries (preserving order).
            var survivors = new List<ShaderVariantCollection>(toKeep.Count);
            foreach (var entry in toKeep)
            {
                var assetPath = Path.Combine(_outputFolder, entry.assetName + ".shadervariants").Replace('\\', '/');
                var collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(assetPath);
                if (collection != null)
                    survivors.Add(collection);
                else
                    Debug.LogWarning($"[ShaderVariantSplitTool] Prune: could not reload '{assetPath}' — skipping.");
            }

            _manifest.collections = survivors;
            EditorUtility.SetDirty(_manifest);

            // Update index to reflect only surviving entries.
            ShaderVariantRecord.SaveIndex(_outputFolder, new ShaderVariantIndexData { entries = toKeep });

            AssetDatabase.SaveAssets();

            Debug.Log($"[ShaderVariantSplitTool] Pruned {toDelete.Count} unused variants. " +
                      $"{survivors.Count} variants remain in manifest '{_manifest.name}'.");
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // YAML writer
        // ──────────────────────────────────────────────────────────────────────────────────

        private static string BuildYaml(string name, Shader shader, VariantSpec spec)
        {
            var shaderPath = AssetDatabase.GetAssetPath(shader);
            var guid       = AssetDatabase.AssetPathToGUID(shaderPath);
            var passType   = (int)spec.PassType;

            return
$@"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!200 &20000000
ShaderVariantCollection:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: {name}
  m_Shaders:
  - first: {{fileID: 4800000, guid: {guid}, type: 3}}
    second:
      variants:
      - keywords: {spec.Keywords}
        passType: {passType}
";
        }
    }
}
