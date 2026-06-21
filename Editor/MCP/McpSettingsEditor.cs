using System.IO;
using Molca.Editor.KnowledgeGraph;
using Molca.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Inspector for <see cref="McpSettings"/>: the provider list plus a per-provider status panel
    /// (status dot, display name, message) mirroring the Notification Providers panel, and a summary
    /// of any registry collisions.
    /// </summary>
    [CustomEditor(typeof(McpSettings))]
    public class McpSettingsEditor : UnityEditor.Editor
    {
        private GUIStyle _boxStyle;
        private GUIContent _helpIcon;
        private GUIContent _playBadge;
        private GUIContent _undoBadge;
        private GUIStyle _badgeStyle;
        private GUIStyle _legendLabelStyle;

        /// <summary>Number of action tools shown per allowlist page.</summary>
        private const int AllowlistPageSize = 12;

        /// <summary>Zero-based index of the currently displayed allowlist page.</summary>
        private int _allowlistPage;

        private void OnEnable() => GraphifyBuild.Changed += Repaint;
        private void OnDisable() => GraphifyBuild.Changed -= Repaint;

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            _boxStyle ??= new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(15, 15, 15, 15),
                margin = new RectOffset(5, 5, 5, 5)
            };
            _helpIcon ??= EditorGUIUtility.IconContent("_Help", "|Show this tool's description");
            _playBadge ??= EditorGUIUtility.IconContent("PlayButton", "|Play mode only");
            _undoBadge ??= EditorGUIUtility.IconContent("UndoHistory", "|Undoable — this action can be reverted");
            _badgeStyle ??= new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0)
            };
            _legendLabelStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };

            serializedObject.Update();

            EditorGUILayout.BeginVertical(_boxStyle);

            var providersProperty = serializedObject.FindProperty("providers");
            EditorGUILayout.PropertyField(providersProperty, new GUIContent("Tool Providers"), true);

            EditorGUILayout.Space(10);

            var settings = (McpSettings)target;
            if (providersProperty.arraySize > 0)
            {
                EditorGUILayout.LabelField("Provider Status", EditorStyles.miniBoldLabel);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                for (int i = 0; i < providersProperty.arraySize; i++)
                {
                    var provider = providersProperty.GetArrayElementAtIndex(i).objectReferenceValue as McpToolProvider;
                    if (provider == null)
                        continue;

                    DrawProviderStatusRow(provider);
                }

                EditorGUILayout.EndVertical();

                // Surface tool-name / namespace collisions from a fresh registry build.
                var registry = settings.BuildRegistry();
                if (registry.HasErrors)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox(
                        "Registry issues:\n - " + string.Join("\n - ", registry.Errors),
                        MessageType.Error);
                }
                else
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField($"{registry.Tools.Count} tool(s) registered.",
                        EditorStyles.centeredGreyMiniLabel);
                }

                DrawActionAllowlist(registry);
            }

            DrawUndoStack();

            DrawKnowledgeGraph();

            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Project knowledge-graph (graphify) section (Sprint 23.4): shows whether a graph is built and
        /// when, and offers non-blocking Build / Full Rebuild buttons that refresh the Unity facts corpus
        /// and run graphify. The graph powers the read-only <c>molca_kg_query</c>/<c>_path</c>/<c>_explain</c>
        /// tools the assistant uses to answer project-wide questions.
        /// </summary>
        private static void DrawKnowledgeGraph()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Knowledge Graph (graphify)", EditorStyles.miniBoldLabel);

            var built = GraphifyCli.GraphExists;
            EditorGUILayout.BeginHorizontal();
            var dotStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = built ? MolcaEditorColors.StatusOk : MolcaEditorColors.StatusIdle }
            };
            GUILayout.Label(built ? "●" : "○", dotStyle, GUILayout.Width(20));
            EditorGUILayout.LabelField(
                built
                    ? $"Built {File.GetLastWriteTime(GraphifyCli.GraphJsonPath):yyyy-MM-dd HH:mm}"
                    : "Not built yet.",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField($"Corpus: {GraphifyCli.CorpusDir}", EditorStyles.wordWrappedMiniLabel);

            if (GraphifyBuild.IsBuilding)
            {
                EditorGUILayout.HelpBox(GraphifyBuild.Status, MessageType.Info);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(built ? "Update Graph" : "Build Graph"))
                    GraphifyBuild.Run(full: false);
                using (new EditorGUI.DisabledScope(!built))
                {
                    if (GUILayout.Button("Full Rebuild", GUILayout.Width(110)))
                        GraphifyBuild.Run(full: true);
                }
                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(GraphifyBuild.Status))
                    EditorGUILayout.LabelField(GraphifyBuild.Status, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.HelpBox(
                "Builds a graphify knowledge graph of the project (code + assets + docs) so the assistant "
                + "can answer project-wide questions via molca_kg_query. Requires the graphify CLI on PATH; "
                + "indexing incurs LLM cost.",
                MessageType.None);
        }

        /// <summary>Shows the action undo stack and a one-click revert of the most recent action (Sprint 17).</summary>
        private static void DrawUndoStack()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Action Undo", EditorStyles.miniBoldLabel);

            var entries = McpUndoStack.Entries;
            EditorGUILayout.BeginHorizontal();
            if (entries.Count == 0)
            {
                EditorGUILayout.LabelField("No revertible actions recorded.", EditorStyles.miniLabel);
            }
            else
            {
                var last = entries[entries.Count - 1];
                EditorGUILayout.LabelField($"{entries.Count} revertible — last: {last.Description}",
                    EditorStyles.wordWrappedMiniLabel);
            }
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(entries.Count == 0))
            {
                if (GUILayout.Button("Revert last action", GUILayout.Width(130)))
                {
                    var msg = McpUndoStack.UndoLast();
                    EditorUtility.DisplayDialog("Revert MCP Action", msg, "OK");
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Lists the registry's Action (mutating) tools with an allowlist toggle each (Sprint 17.1).
        /// Only allowlisted action tools may run — and even then only after a confirmation step.
        /// </summary>
        private void DrawActionAllowlist(McpToolRegistry registry)
        {
            var actionTools = new System.Collections.Generic.List<McpToolDefinition>();
            foreach (var t in registry.Tools)
                if (t.Kind == McpToolKind.Action) actionTools.Add(t);
            if (actionTools.Count == 0)
                return;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Action Tools (mutating)", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Action tools mutate the project. Only ticked tools may run, and each run still requires "
                + "an explicit confirmation (MCP elicitation in IDE clients, a dialog in the chat).",
                MessageType.Warning);

            var listProp = serializedObject.FindProperty("actionToolAllowlist");

            // One compact header row: count · legend · bulk toggles.
            // Fixed row height + middle-aligned styles keep icons and text on a common baseline.
            var rowH = EditorGUIUtility.singleLineHeight;
            EditorGUILayout.BeginHorizontal(GUILayout.Height(rowH));
            GUILayout.Label($"{CountAllowed(listProp, actionTools)} / {actionTools.Count} allowed",
                _legendLabelStyle);
            GUILayout.Space(12);
            GUILayout.Label(_playBadge, _badgeStyle, GUILayout.Width(16), GUILayout.Height(rowH));
            GUILayout.Label("play only", _legendLabelStyle, GUILayout.Width(54));
            GUILayout.Label(_undoBadge, _badgeStyle, GUILayout.Width(16), GUILayout.Height(rowH));
            GUILayout.Label("undoable", _legendLabelStyle, GUILayout.Width(56));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(44)))
                SetAll(listProp, actionTools, true);
            if (GUILayout.Button("None", EditorStyles.miniButtonRight, GUILayout.Width(44)))
                SetAll(listProp, actionTools, false);
            EditorGUILayout.EndHorizontal();

            // Clamp the page index — the tool count can shrink between repaints (provider toggled off).
            var pageCount = Mathf.Max(1, Mathf.CeilToInt(actionTools.Count / (float)AllowlistPageSize));
            _allowlistPage = Mathf.Clamp(_allowlistPage, 0, pageCount - 1);
            var start = _allowlistPage * AllowlistPageSize;
            var end = Mathf.Min(start + AllowlistPageSize, actionTools.Count);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            // Two tools per row to halve the vertical footprint of a growing allowlist.
            // Each row reserves a real full-width layout rect, then splits it into two columns
            // with explicit GUI rects — this cannot overflow the panel like nested ExpandWidth groups can.
            var rowHeight = EditorGUIUtility.singleLineHeight + 2f;
            for (int i = start; i < end; i += 2)
            {
                var row = EditorGUILayout.GetControlRect(false, rowHeight);
                var half = row.width * 0.5f;
                DrawAllowlistCell(listProp, actionTools[i], new Rect(row.x, row.y, half - 4f, row.height));
                if (i + 1 < end)
                    DrawAllowlistCell(listProp, actionTools[i + 1], new Rect(row.x + half, row.y, half - 4f, row.height));
            }
            EditorGUILayout.EndVertical();

            // Page navigation — only shown once the list spills past a single page.
            if (pageCount > 1)
            {
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(_allowlistPage <= 0))
                    if (GUILayout.Button("◄ Prev", EditorStyles.miniButtonLeft, GUILayout.Width(60)))
                        _allowlistPage--;
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    $"Page {_allowlistPage + 1} / {pageCount}   (tools {start + 1}–{end} of {actionTools.Count})",
                    _legendLabelStyle);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_allowlistPage >= pageCount - 1))
                    if (GUILayout.Button("Next ►", EditorStyles.miniButtonRight, GUILayout.Width(60)))
                        _allowlistPage++;
                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Draws one allowlist toggle + capability badges + help button for <paramref name="tool"/>
        /// inside the explicit <paramref name="cell"/> rect. Name only on the row; the full description
        /// lives in the hover tooltip and behind the help button.
        /// </summary>
        private void DrawAllowlistCell(SerializedProperty listProp, McpToolDefinition tool, Rect cell)
        {
            var index = IndexInStringList(listProp, tool.Name);
            var allowed = index >= 0;

            // Right-pinned cluster, packed right-to-left with no reserved gaps: [help][undo?][play?].
            // Only badges that apply take space, so the icons stay tight against the help button.
            const float badge = 16f, help = 16f;
            var x = cell.xMax;

            x -= help;
            var helpRect = new Rect(x, cell.y, help, cell.height);

            if (tool.Reversibility != McpToolReversibility.Irreversible)
            {
                x -= badge;
                GUI.Label(new Rect(x, cell.y, badge, cell.height), _undoBadge, _badgeStyle);
            }
            if (tool.Mode == McpToolMode.Play)
            {
                x -= badge;
                GUI.Label(new Rect(x, cell.y, badge, cell.height), _playBadge, _badgeStyle);
            }

            var toggleRect = new Rect(cell.x, cell.y, x - cell.x - 2f, cell.height);
            var label = new GUIContent(tool.Name, tool.Description);
            var now = EditorGUI.ToggleLeft(toggleRect, label, allowed);

            if (GUI.Button(helpRect, _helpIcon, EditorStyles.label))
                EditorUtility.DisplayDialog(tool.Name, tool.Description, "OK");

            if (now == allowed) return;

            if (now)
            {
                listProp.arraySize++;
                listProp.GetArrayElementAtIndex(listProp.arraySize - 1).stringValue = tool.Name;
            }
            else
            {
                listProp.DeleteArrayElementAtIndex(index);
            }
        }

        /// <summary>Counts how many of <paramref name="tools"/> are present in the allowlist property.</summary>
        private static int CountAllowed(SerializedProperty listProp, System.Collections.Generic.List<McpToolDefinition> tools)
        {
            var count = 0;
            foreach (var t in tools)
                if (IndexInStringList(listProp, t.Name) >= 0) count++;
            return count;
        }

        /// <summary>Adds every action tool to the allowlist (<paramref name="allow"/> true) or clears them all.</summary>
        private static void SetAll(SerializedProperty listProp, System.Collections.Generic.List<McpToolDefinition> tools, bool allow)
        {
            foreach (var t in tools)
            {
                var index = IndexInStringList(listProp, t.Name);
                if (allow && index < 0)
                {
                    listProp.arraySize++;
                    listProp.GetArrayElementAtIndex(listProp.arraySize - 1).stringValue = t.Name;
                }
                else if (!allow && index >= 0)
                {
                    listProp.DeleteArrayElementAtIndex(index);
                }
            }
        }

        private static int IndexInStringList(SerializedProperty listProp, string value)
        {
            for (int i = 0; i < listProp.arraySize; i++)
                if (listProp.GetArrayElementAtIndex(i).stringValue == value)
                    return i;
            return -1;
        }

        private static void DrawProviderStatusRow(McpToolProvider provider)
        {
            var status = provider.GetStatus();
            Color color;
            string icon;
            switch (status)
            {
                case McpProviderStatus.Configured:
                    color = MolcaEditorColors.StatusOk;
                    icon = "●";
                    break;
                case McpProviderStatus.Misconfigured:
                    color = MolcaEditorColors.StatusWarn;
                    icon = "●";
                    break;
                default: // Disabled
                    color = MolcaEditorColors.StatusIdle;
                    icon = "○";
                    break;
            }

            EditorGUILayout.BeginHorizontal();

            var iconStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 16,
                normal = { textColor = color },
                alignment = TextAnchor.MiddleCenter
            };
            GUILayout.Label(icon, iconStyle, GUILayout.Width(20));

            EditorGUILayout.LabelField($"{provider.DisplayName}  ({provider.Namespace})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(provider.GetStatusMessage(), EditorStyles.miniLabel, GUILayout.Width(200));

            EditorGUILayout.EndHorizontal();
        }
    }
}
