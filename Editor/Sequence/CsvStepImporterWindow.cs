using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Molca.ReferenceSystem;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using Molca.Localization;
using Molca.Editor.UI;

namespace Molca.Editor
{
    /// <summary>
    /// Extensibility API for Step Importer (CSV, TSV, Excel). Use from [InitializeOnLoad] or similar
    /// to add extra column mappings and setup logic for custom StepInfo-derived types.
    /// </summary>
    public static class CsvStepImporterExtensibility
    {
        /// <summary>Extra column mappings added by extensions (e.g. for custom StepInfo fields).</summary>
        public static readonly List<CsvStepImporterWindow.CsvColumnMapping> ExtraColumnMappings = new List<CsvStepImporterWindow.CsvColumnMapping>();

        /// <summary>Type of StepInfo to add when creating a new auxiliary (default: StepInfo). Set to your derived type in a fork.</summary>
        public static System.Type StepInfoType { get; set; } = typeof(StepInfo);

        /// <summary>Callbacks invoked after base StepInfo setup, with (stepInfo, stepData). Use to set extra fields on your derived StepInfo.</summary>
        public static readonly List<System.Action<StepInfo, CsvStepImporterWindow.CsvStepData>> SetupCallbacks = new List<System.Action<StepInfo, CsvStepImporterWindow.CsvStepData>>();

        /// <summary>Callbacks invoked in OnGUI after controller selection. Receives the importer window (use for controller/sequence context).</summary>
        public static readonly List<System.Action<CsvStepImporterWindow>> DrawExtraGUICallbacks = new List<System.Action<CsvStepImporterWindow>>();

        /// <summary>Add an extra column mapping and optionally a setup callback. For a new field, add a mapping and in the callback cast stepInfo to your type and set the field from stepData.ExtraColumnValues["Your Field Name"].</summary>
        public static void RegisterExtraColumnMapping(string fieldName, string description, bool required = false, System.Action<StepInfo, CsvStepImporterWindow.CsvStepData> onSetup = null)
        {
            ExtraColumnMappings.Add(new CsvStepImporterWindow.CsvColumnMapping
            {
                fieldName = fieldName,
                description = description,
                required = required
            });
            if (onSetup != null)
                SetupCallbacks.Add(onSetup);
        }
    }

    /// <summary>
    /// Imports sequence steps from a spreadsheet (CSV/TSV/TXT/XLSX). Creation and updates
    /// route through <see cref="StepEditingService"/> so the whole import is a single undoable
    /// operation. Columns are mapped by header name (auto-matched on load); steps are matched
    /// to existing ones by Ref Id or persisted step id (never GameObject name); a dry-run
    /// preview reports create/update counts before any change is committed.
    /// </summary>
    /// <remarks>
    /// The <see cref="CsvStepImporterExtensibility"/> API (extra columns, custom StepInfo type,
    /// setup callbacks, extra GUI) is preserved for SDK forks.
    /// </remarks>
    public class CsvStepImporterWindow : EditorWindow
    {
        [MenuItem("Molca/Sequence/Step Importer (CSV, TSV, Excel)", priority = 22)]
        private static void OpenCsvStepImporter()
        {
            ShowWindow();
        }

        // Well-known mapping field names.
        private const string FieldStepNumber = "Step Number";
        private const string FieldRefId = "Ref Id";
        private const string FieldStepType = "Step Type";
        private const string FieldDescription = "Description";
        private const string FieldTitle = "Title";

        private string importFilePath = "";
        private List<string[]> tableRows;
        private string[] headers;
        private bool shouldReloadSpreadsheet;
        private int xlsxSheetIndex;
        private List<CsvColumnMapping> columnMappings = new List<CsvColumnMapping>();
        private Vector2 scrollPosition;
        private SequenceController targetController;

        // Dry-run preview state.
        private string _previewSummary;
        private MessageType _previewMessageType = MessageType.Info;

        // Step-type resolution (name -> concrete Step subtype), built lazily.
        private Dictionary<string, Type> _stepTypesByName;

        /// <summary>Currently selected controller (exposed for extensions e.g. scenario role mapping).</summary>
        public SequenceController TargetController => targetController;

        private int startRow = 1; // 0 = include top row, 1 = skip header (0-indexed)
        private int endRow = -1; // -1 means all rows

        private const string PrefsKeyColumnMappings = "Molca.CsvStepImporter.ColumnMappings";

        [System.Serializable]
        public class CsvColumnMapping
        {
            public string fieldName;
            public string description;
            public int columnIndex = -1;
            public string previewValue = "";
            public bool required = false;
        }

        private void OnEnable()
        {
            titleContent = Molca.Editor.Icons.MolcaEditorIcons.WindowTitle("Step Importer", "sequence");
            minSize = new Vector2(800, 600);

            InitializeColumnMappings();
            LoadColumnMappings();
        }

        private void InitializeColumnMappings()
        {
            columnMappings.Clear();

            columnMappings.Add(new CsvColumnMapping {
                fieldName = FieldStepNumber,
                description = "Step identifier (e.g., 1, 1.1, 2.1). Integer values also persist as the step id for re-import matching.",
                required = true
            });

            // Optional stable identity + type columns.
            columnMappings.Add(new CsvColumnMapping {
                fieldName = FieldRefId,
                description = "Optional. Stable Ref Id used to match existing steps for update. Blank rows get a generated Ref Id."
            });
            columnMappings.Add(new CsvColumnMapping {
                fieldName = FieldStepType,
                description = "Optional. Concrete Step type name (e.g., BranchingStep). Defaults to Step."
            });

            // Merge extra column mappings from extensions (e.g. for custom StepInfo-derived types)
            foreach (var extra in CsvStepImporterExtensibility.ExtraColumnMappings)
            {
                columnMappings.Add(new CsvColumnMapping
                {
                    fieldName = extra.fieldName,
                    description = extra.description,
                    required = extra.required
                });
            }

            // Add language-specific title mappings
            var localizationModule = Molca.GlobalSettings.GetModule<Molca.Localization.LocalizationModule>();
            if (localizationModule != null && localizationModule.LanguageCode != null && localizationModule.LanguageCode.Length > 0)
            {
                foreach (var languageCode in localizationModule.LanguageCode)
                {
                    columnMappings.Add(new CsvColumnMapping {
                        fieldName = $"Title ({languageCode})",
                        description = $"Step title in {languageCode.ToUpper()}",
                        required = languageCode == "en" // Make English required as fallback
                    });
                }
            }
            else
            {
                columnMappings.Add(new CsvColumnMapping {
                    fieldName = FieldTitle,
                    description = "Step title",
                    required = true
                });
            }

            columnMappings.Add(new CsvColumnMapping {
                fieldName = FieldDescription,
                description = "Step description"
            });
        }

        [System.Serializable]
        private class SerializedColumnMappings
        {
            public List<SerializedMappingEntry> entries = new List<SerializedMappingEntry>();
        }

        [System.Serializable]
        private class SerializedMappingEntry
        {
            public string fieldName;
            public int columnIndex;
        }

        private void SaveColumnMappings()
        {
            var data = new SerializedColumnMappings();
            foreach (var m in columnMappings)
            {
                data.entries.Add(new SerializedMappingEntry { fieldName = m.fieldName, columnIndex = m.columnIndex });
            }
            MolcaEditorPrefs.SetString(PrefsKeyColumnMappings, JsonUtility.ToJson(data));
        }

        private void LoadColumnMappings()
        {
            string json = MolcaEditorPrefs.GetString(PrefsKeyColumnMappings, null);
            if (string.IsNullOrEmpty(json)) return;
            var data = JsonUtility.FromJson<SerializedColumnMappings>(json);
            if (data?.entries == null) return;
            var byName = data.entries.ToDictionary(e => e.fieldName, e => e.columnIndex);
            foreach (var m in columnMappings)
            {
                if (byName.TryGetValue(m.fieldName, out int index))
                    m.columnIndex = index;
            }
        }

        public static void ShowWindow(SequenceController controller = null)
        {
            var window = GetWindow<CsvStepImporterWindow>();
            window.targetController = controller;
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            if (tableRows != null && tableRows.Count > 0)
            {
                EditorGUILayout.LabelField($"({tableRows.Count} rows loaded)", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Controller:", GUILayout.Width(70));
            EditorGUI.BeginChangeCheck();
            targetController = (SequenceController)EditorGUILayout.ObjectField(
                targetController, typeof(SequenceController), true);
            if (EditorGUI.EndChangeCheck())
            {
                _previewSummary = null;
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            if (targetController == null)
            {
                EditorGUILayout.HelpBox("Select a SequenceController to import steps into.", MessageType.Warning);
                return;
            }

            foreach (var drawExtra in CsvStepImporterExtensibility.DrawExtraGUICallbacks)
            {
                drawExtra?.Invoke(this);
            }

            // Spreadsheet file (CSV, tab-separated, .txt, .xlsx)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("File:", GUILayout.Width(70));
            EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(importFilePath) ? "No file selected" : importFilePath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("Browse...", GUILayout.Width(70)))
            {
                string directory = string.IsNullOrEmpty(importFilePath) ? "" : Path.GetDirectoryName(importFilePath);
                string path = EditorUtility.OpenFilePanel("Select spreadsheet (CSV, TSV, TXT, XLSX)", directory, "");
                if (!string.IsNullOrEmpty(path))
                {
                    importFilePath = path;
                    xlsxSheetIndex = 0;
                    shouldReloadSpreadsheet = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (shouldReloadSpreadsheet)
            {
                shouldReloadSpreadsheet = false;
                LoadSpreadsheetData();
            }

            var xlsxNames = SpreadsheetTableLoader.LastXlsxSheetNames;
            if (xlsxNames != null && xlsxNames.Count > 1)
            {
                EditorGUI.BeginChangeCheck();
                xlsxSheetIndex = EditorGUILayout.Popup("Excel sheet", xlsxSheetIndex, xlsxNames.ToArray());
                if (EditorGUI.EndChangeCheck())
                    LoadSpreadsheetData();
            }

            if (tableRows != null && tableRows.Count > 0)
            {
                DrawRowRangeControls();
                EditorGUILayout.Space();
                DrawColumnMappings();
            }
            else
            {
                EditorGUILayout.HelpBox("Browse for a CSV, tab-separated (.tsv/.tab), .txt (delimiter auto-detected), or Excel .xlsx file.", MessageType.Info);
            }
        }

        private void DrawRowRangeControls()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Import rows:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            int oldStartRow = startRow;
            startRow = EditorGUILayout.IntField(startRow, GUILayout.Width(50));
            EditorGUILayout.LabelField("to", GUILayout.Width(15));
            endRow = EditorGUILayout.IntField(endRow, GUILayout.Width(50));
            if (endRow < 0) endRow = -1;

            if (EditorGUI.EndChangeCheck())
            {
                if (oldStartRow != startRow) UpdateAllPreviews();
                _previewSummary = null;
            }

            int validatedStartRow = Mathf.Max(0, startRow);
            int validatedEndRow = (endRow == -1) ? (tableRows.Count - 1) : Mathf.Min(endRow, tableRows.Count - 1);
            int rowsToImport = Mathf.Max(0, validatedEndRow - validatedStartRow + 1);
            EditorGUILayout.LabelField($"({rowsToImport} rows)", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawColumnMappings()
        {
            EditorGUILayout.LabelField("Column Mappings", EditorStyles.boldLabel);

            if (headers == null || headers.Length == 0) return;

            // Header dropdown options: index 0 = "(none)", then one per header column.
            var options = new string[headers.Length + 1];
            options[0] = "(none)";
            for (int h = 0; h < headers.Length; h++)
            {
                string name = string.IsNullOrEmpty(headers[h]) ? $"Column {h}" : headers[h];
                options[h + 1] = $"{h}: {name}";
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(220));
            for (int i = 0; i < columnMappings.Count; i++)
            {
                var mapping = columnMappings[i];

                EditorGUILayout.BeginHorizontal();
                GUI.color = mapping.required ? MolcaEditorColors.StatusWarn : Color.white;
                EditorGUILayout.LabelField(
                    new GUIContent($"{mapping.fieldName}{(mapping.required ? "*" : "")}", mapping.description),
                    GUILayout.Width(160));
                GUI.color = Color.white;

                EditorGUI.BeginChangeCheck();
                int popupIndex = Mathf.Clamp(mapping.columnIndex + 1, 0, options.Length - 1);
                popupIndex = EditorGUILayout.Popup(popupIndex, options, GUILayout.Width(200));
                if (EditorGUI.EndChangeCheck())
                {
                    mapping.columnIndex = popupIndex - 1;
                    UpdatePreview(mapping);
                    SaveColumnMappings();
                    _previewSummary = null;
                }

                EditorGUILayout.LabelField("→", GUILayout.Width(20));
                EditorGUILayout.LabelField(mapping.previewValue, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-Match Columns"))
            {
                AutoMatchColumns();
            }
            EditorGUILayout.EndHorizontal();

            bool hasRequiredMappings = columnMappings.Where(m => m.required).All(m => m.columnIndex >= 0);
            if (!hasRequiredMappings)
            {
                EditorGUILayout.HelpBox("Map all required columns (*) before importing.", MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!hasRequiredMappings))
            {
                if (GUILayout.Button("Preview (Dry Run)", GUILayout.Height(28)))
                {
                    BuildAndShowPreview();
                }
                if (GUILayout.Button("Import Steps", GUILayout.Height(28)))
                {
                    ImportSteps();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_previewSummary))
            {
                EditorGUILayout.HelpBox(_previewSummary, _previewMessageType);
            }
        }

        #region Spreadsheet loading / preview

        private void LoadSpreadsheetData()
        {
            _previewSummary = null;
            if (string.IsNullOrEmpty(importFilePath) || !File.Exists(importFilePath))
            {
                tableRows = null;
                headers = null;
                return;
            }

            try
            {
                tableRows = SpreadsheetTableLoader.LoadFromPath(importFilePath, xlsxSheetIndex, out var errorMessage);
                if (tableRows == null)
                {
                    headers = null;
                    if (!string.IsNullOrEmpty(errorMessage))
                        Debug.LogError($"Error loading spreadsheet: {errorMessage}");
                    return;
                }

                if (tableRows.Count > 0)
                {
                    headers = tableRows[0];
                    var ext = Path.GetExtension(importFilePath);
                    Debug.Log($"Loaded spreadsheet ({ext}) with {headers.Length} columns, {tableRows.Count} rows.");
                    AutoMatchColumns();
                }
                else
                {
                    Debug.LogWarning("Spreadsheet contains no rows");
                    headers = null;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error loading spreadsheet: {ex.Message}");
                tableRows = null;
                headers = null;
            }

            UpdateAllPreviews();
        }

        /// <summary>
        /// Assigns each unmapped column mapping to a header whose normalized name matches the
        /// field name (exact normalized match, else substring). Already-mapped columns are left alone.
        /// </summary>
        private void AutoMatchColumns()
        {
            if (headers == null) { return; }

            string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

            var normHeaders = headers.Select(Norm).ToArray();
            foreach (var mapping in columnMappings)
            {
                string target = Norm(mapping.fieldName);
                if (string.IsNullOrEmpty(target)) continue;

                int exact = Array.FindIndex(normHeaders, h => h == target);
                int match = exact >= 0
                    ? exact
                    : Array.FindIndex(normHeaders, h => h.Length > 0 && (h.Contains(target) || target.Contains(h)));

                if (match >= 0)
                {
                    mapping.columnIndex = match;
                    UpdatePreview(mapping);
                }
            }
            SaveColumnMappings();
            _previewSummary = null;
        }

        private void UpdateAllPreviews()
        {
            foreach (var mapping in columnMappings) UpdatePreview(mapping);
        }

        private void UpdatePreview(CsvColumnMapping mapping)
        {
            if (tableRows == null || tableRows.Count < 1 || headers == null || mapping.columnIndex < 0 || mapping.columnIndex >= headers.Length)
            {
                mapping.previewValue = "<unmapped>";
                return;
            }

            int previewRowIndex = Mathf.Clamp(Mathf.Max(0, startRow), 0, tableRows.Count - 1);
            var previewRow = tableRows[previewRowIndex];
            if (mapping.columnIndex < previewRow.Length)
            {
                string value = previewRow[mapping.columnIndex];
                mapping.previewValue = string.IsNullOrEmpty(value) ? "<empty>" : value;
            }
            else
            {
                mapping.previewValue = "<out of bounds>";
            }
        }

        private void BuildAndShowPreview()
        {
            var plan = BuildPlan(out var warnings);
            int creates = plan.Count(p => !p.IsUpdate);
            int updates = plan.Count(p => p.IsUpdate);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Dry run: {creates} step(s) to create, {updates} to update.");
            foreach (var w in warnings) sb.AppendLine("• " + w);

            _previewSummary = sb.ToString().TrimEnd();
            _previewMessageType = warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
        }

        #endregion

        #region Plan + commit

        /// <summary>One planned step operation: the parsed row, the matched existing step (if any), and the resolved type.</summary>
        private class PlanItem
        {
            public CsvStepData Data;
            public Step Existing;     // null => create
            public Type StepType;     // resolved concrete Step type for creation
            public bool IsUpdate => Existing != null;
        }

        /// <summary>
        /// Parses the selected row range into a sorted plan, matching existing steps by Ref Id
        /// or persisted step id (never GameObject name). Pure: performs no scene mutation.
        /// </summary>
        private List<PlanItem> BuildPlan(out List<string> warnings)
        {
            warnings = new List<string>();
            var plan = new List<PlanItem>();
            if (targetController == null || tableRows == null) return plan;

            int actualStartRow = Mathf.Max(0, startRow);
            int actualEndRow = (endRow == -1) ? (tableRows.Count - 1) : Mathf.Min(endRow, tableRows.Count - 1);

            var stepDataList = new List<CsvStepData>();
            int skipped = 0;
            for (int i = actualStartRow; i <= actualEndRow; i++)
            {
                var columns = tableRows[i];
                if (columns.Length == 0) continue;

                var stepData = new CsvStepData
                {
                    rowIndex = i,
                    stepNumber = GetColumnValue(columns, FieldStepNumber),
                    refId = GetColumnValue(columns, FieldRefId),
                    stepTypeName = GetColumnValue(columns, FieldStepType),
                    title = GetColumnValue(columns, FieldTitle),
                    titlesByLanguage = GetTitlesForAllLanguages(columns),
                    description = GetColumnValue(columns, FieldDescription),
                    extraColumnValues = GetExtraColumnValues(columns)
                };

                if (string.IsNullOrEmpty(stepData.stepNumber) ||
                    (string.IsNullOrEmpty(stepData.title) && (stepData.titlesByLanguage == null || stepData.titlesByLanguage.Count == 0)))
                {
                    skipped++;
                    continue;
                }
                stepDataList.Add(stepData);
            }
            if (skipped > 0) warnings.Add($"{skipped} row(s) skipped (missing step number or title).");

            // Duplicate step-number detection.
            foreach (var dup in stepDataList.GroupBy(s => s.stepNumber).Where(g => g.Count() > 1))
            {
                warnings.Add($"Step number '{dup.Key}' appears {dup.Count()} times; only the first will be used.");
            }

            // Existing-step match indices (Ref Id + persisted step id) — not GameObject name.
            var existing = targetController.GetComponentsInChildren<Step>(true)
                .Where(s => s.gameObject != targetController.gameObject)
                .ToList();
            var byRefId = new Dictionary<string, Step>();
            foreach (var s in existing)
                if (!string.IsNullOrEmpty(s.RefId)) byRefId[s.RefId] = s;
            var byStepId = new Dictionary<int, Step>();
            foreach (var s in existing)
                if (s.StepId != 0 && !byStepId.ContainsKey(s.StepId)) byStepId[s.StepId] = s;

            var seenNumbers = new HashSet<string>();
            foreach (var data in stepDataList
                         .OrderBy(s => s.stepNumber, new StepNumberComparer()))
            {
                if (!seenNumbers.Add(data.stepNumber)) continue; // skip duplicates after the first

                Step matched = null;
                if (!string.IsNullOrEmpty(data.refId)) byRefId.TryGetValue(data.refId, out matched);
                if (matched == null && int.TryParse(data.stepNumber, out int sid)) byStepId.TryGetValue(sid, out matched);

                plan.Add(new PlanItem
                {
                    Data = data,
                    Existing = matched,
                    StepType = ResolveStepType(data.stepTypeName)
                });
            }

            return plan;
        }

        private void ImportSteps()
        {
            if (targetController == null || targetController.gameObject == null)
            {
                EditorUtility.DisplayDialog("Error", "Please select a valid target SequenceController.", "OK");
                return;
            }

            var plan = BuildPlan(out var warnings);
            if (plan.Count == 0)
            {
                EditorUtility.DisplayDialog("Nothing to import", "No valid rows found in the selected range.", "OK");
                return;
            }

            int creates = plan.Count(p => !p.IsUpdate);
            int updates = plan.Count(p => p.IsUpdate);
            string warnText = warnings.Count > 0 ? "\n\nWarnings:\n• " + string.Join("\n• ", warnings) : "";
            if (!EditorUtility.DisplayDialog("Import Steps?",
                    $"Create {creates} step(s) and update {updates} existing step(s).{warnText}", "Import", "Cancel"))
            {
                return;
            }

            int undoGroup = Undo.GetCurrentGroup();
            int created = 0, updated = 0, failed = 0;

            try
            {
                // Resolve parents within this import: keyed by CSV step number, populated as we go.
                var stepLookup = new Dictionary<string, Step>();

                // Process in hierarchy order so a parent row is handled before its children.
                var ordered = plan.OrderBy(p => GetStepLevel(p.Data.stepNumber))
                                  .ThenBy(p => p.Data.stepNumber, new StepNumberComparer())
                                  .ToList();

                for (int idx = 0; idx < ordered.Count; idx++)
                {
                    var item = ordered[idx];
                    EditorUtility.DisplayProgressBar("Importing steps",
                        $"Step {item.Data.stepNumber} ({idx + 1}/{ordered.Count})", (float)idx / ordered.Count);

                    try
                    {
                        Step parent = ResolveParent(item.Data.stepNumber, stepLookup);
                        Step step = item.Existing;

                        if (step == null)
                        {
                            string goName = $"Step_{item.Data.stepNumber.Replace('.', '_')}";
                            step = StepEditingService.AddStep(targetController, item.StepType, parent, goName);
                            if (step == null) { failed++; continue; }
                            AssignIdentity(step, item.Data);
                            created++;
                        }
                        else
                        {
                            updated++;
                        }

                        ApplyStepInfo(step, item.Data);
                        stepLookup[item.Data.stepNumber] = step;
                    }
                    catch (System.Exception e)
                    {
                        failed++;
                        Debug.LogError($"Failed to import step '{item.Data.stepNumber}': {e.Message}");
                    }
                }

                Undo.SetCurrentGroupName("Import Steps from Spreadsheet");
                Undo.CollapseUndoOperations(undoGroup);

                EditorUtility.SetDirty(targetController.gameObject);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string msg = $"Created {created} step(s), updated {updated}." + (failed > 0 ? $" {failed} failed (see Console)." : "");
            Debug.Log($"[Step Importer] {msg}");
            EditorUtility.DisplayDialog(failed > 0 ? "Import completed with errors" : "Import complete", msg, "OK");
            _previewSummary = null;
        }

        /// <summary>Assigns Ref Id (from the CSV or generated) and the persisted step id (when the number is an integer) via SerializedObject.</summary>
        private static void AssignIdentity(Step step, CsvStepData data)
        {
            var so = new SerializedObject(step);
            so.FindProperty("refId").stringValue = !string.IsNullOrEmpty(data.refId)
                ? data.refId
                : ReferenceGenerator.GenerateUniqueId(step.RefType);
            if (int.TryParse(data.stepNumber, out int stepId))
                so.FindProperty("stepId").intValue = stepId;
            so.ApplyModifiedProperties();
        }

        private Step ResolveParent(string stepNumber, Dictionary<string, Step> stepLookup)
        {
            var parentNumber = GetParentStepNumber(stepNumber);
            if (parentNumber != null && stepLookup.TryGetValue(parentNumber, out var parent))
                return parent;
            return null; // root level (under the controller)
        }

        /// <summary>Resolves a concrete Step type by simple name (case-insensitive); defaults to <see cref="Step"/>.</summary>
        private Type ResolveStepType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return typeof(Step);

            if (_stepTypesByName == null)
            {
                _stepTypesByName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in UnityEditor.TypeCache.GetTypesDerivedFrom<Step>().Where(t => !t.IsAbstract))
                    _stepTypesByName[t.Name] = t;
                _stepTypesByName[nameof(Step)] = typeof(Step);
            }

            return _stepTypesByName.TryGetValue(typeName.Trim(), out var type) ? type : typeof(Step);
        }

        #endregion

        #region StepInfo population (reflection-free)

        private void ApplyStepInfo(Step step, CsvStepData stepData)
        {
            Undo.RecordObject(step, "Import Step Info");

            var stepInfoType = CsvStepImporterExtensibility.StepInfoType ?? typeof(StepInfo);
            StepInfo stepInfo;
            if (step.HasAuxiliary<StepInfo>())
            {
                stepInfo = step.GetAuxiliary<StepInfo>();
            }
            else
            {
                stepInfo = (StepInfo)System.Activator.CreateInstance(stepInfoType);
                step.AddAuxiliary(stepInfo);
                stepInfo.BindOwnerFromStep(step);
            }

            try
            {
                SetupStepInfo(stepInfo, stepData);
                foreach (var callback in CsvStepImporterExtensibility.SetupCallbacks)
                    callback(stepInfo, stepData);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error setting up StepInfo for step {stepData.stepNumber}: {e.Message}");
            }

            EditorUtility.SetDirty(step);
        }

        private void SetupStepInfo(StepInfo stepInfo, CsvStepData stepData)
        {
            if (stepInfo == null) return;

            // Editor-internal init replaces the former reflection into title/description/translations.
            stepInfo.EnsureLocalizationInitialized();

            var languages = GetSupportedLanguages();
            foreach (var lang in languages)
            {
                stepInfo.Title.EnsureLanguage(lang);
                stepInfo.Description.EnsureLanguage(lang);
            }

            stepInfo.Title.disabled = false;
            stepInfo.Title.useLocalizedString = false;
            if (stepData.titlesByLanguage != null && stepData.titlesByLanguage.Count > 0)
            {
                foreach (var kvp in stepData.titlesByLanguage)
                    stepInfo.Title.SetTextForLanguage(kvp.Value, kvp.Key);
            }
            else
            {
                foreach (var lang in languages)
                    stepInfo.Title.SetTextForLanguage(stepData.title ?? "", lang);
            }

            stepInfo.Description.disabled = false;
            stepInfo.Description.useLocalizedString = false;
            stepInfo.Description.SetTextForLanguage(stepData.description ?? "", "en");
        }

        private static string[] GetSupportedLanguages()
        {
            var localizationModule = Molca.GlobalSettings.GetModule<Molca.Localization.LocalizationModule>();
            if (localizationModule != null && localizationModule.LanguageCode != null && localizationModule.LanguageCode.Length > 0)
                return localizationModule.LanguageCode;
            return new[] { "en" };
        }

        #endregion

        #region Column parsing helpers

        private string GetColumnValue(string[] columns, string fieldName)
        {
            var mapping = columnMappings.FirstOrDefault(m => m.fieldName == fieldName);
            if (mapping != null && mapping.columnIndex >= 0 && mapping.columnIndex < columns.Length)
                return columns[mapping.columnIndex];
            return "";
        }

        private Dictionary<string, string> GetTitlesForAllLanguages(string[] columns)
        {
            var titles = new Dictionary<string, string>();
            var localizationModule = Molca.GlobalSettings.GetModule<Molca.Localization.LocalizationModule>();

            if (localizationModule != null && localizationModule.LanguageCode != null && localizationModule.LanguageCode.Length > 0)
            {
                foreach (var languageCode in localizationModule.LanguageCode)
                {
                    var title = GetColumnValue(columns, $"Title ({languageCode})");
                    if (!string.IsNullOrEmpty(title)) titles[languageCode] = title;
                }
            }

            if (titles.Count == 0)
            {
                var fallbackTitle = GetColumnValue(columns, FieldTitle);
                if (!string.IsNullOrEmpty(fallbackTitle) && localizationModule != null && localizationModule.LanguageCode != null)
                {
                    foreach (var languageCode in localizationModule.LanguageCode)
                        titles[languageCode] = fallbackTitle;
                }
            }

            return titles;
        }

        private Dictionary<string, string> GetExtraColumnValues(string[] columns)
        {
            var result = new Dictionary<string, string>();
            var extraNames = new HashSet<string>(CsvStepImporterExtensibility.ExtraColumnMappings.Select(m => m.fieldName));
            foreach (var mapping in columnMappings)
            {
                if (!extraNames.Contains(mapping.fieldName) || mapping.columnIndex < 0) continue;
                result[mapping.fieldName] = mapping.columnIndex < columns.Length ? columns[mapping.columnIndex] ?? "" : "";
            }
            return result;
        }

        #endregion

        #region Hierarchy helpers

        private static string[] ParseStepHierarchy(string stepNumber) => stepNumber.Split('.');

        private static int GetStepLevel(string stepNumber) => ParseStepHierarchy(stepNumber).Length - 1;

        private static string GetParentStepNumber(string stepNumber)
        {
            var parts = ParseStepHierarchy(stepNumber);
            if (parts.Length <= 1) return null;
            return string.Join(".", parts.Take(parts.Length - 1));
        }

        #endregion

        /// <summary>Step data parsed from one spreadsheet row. ExtraColumnValues contains values for columns registered via CsvStepImporterExtensibility.</summary>
        public class CsvStepData
        {
            public int rowIndex;
            public string stepNumber;
            public string refId;           // optional stable identity from the sheet
            public string stepTypeName;    // optional concrete Step type name
            public string title; // Legacy single language support
            public Dictionary<string, string> titlesByLanguage; // Multi-language support
            public string description;
            /// <summary>Values for extra columns (field name -> value). Populated from CsvStepImporterExtensibility.ExtraColumnMappings.</summary>
            public Dictionary<string, string> extraColumnValues;
        }

        // Custom comparer for step numbers to handle hierarchical sorting (e.g. 1 < 1.1 < 2).
        private class StepNumberComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                if (x == y) return 0;
                if (string.IsNullOrEmpty(x)) return -1;
                if (string.IsNullOrEmpty(y)) return 1;

                var xParts = x.Split('.');
                var yParts = y.Split('.');
                int minLength = Mathf.Min(xParts.Length, yParts.Length);

                for (int i = 0; i < minLength; i++)
                {
                    if (int.TryParse(xParts[i], out int xNum) && int.TryParse(yParts[i], out int yNum))
                    {
                        int numCompare = xNum.CompareTo(yNum);
                        if (numCompare != 0) return numCompare;
                    }
                    else
                    {
                        int strCompare = string.Compare(xParts[i], yParts[i]);
                        if (strCompare != 0) return strCompare;
                    }
                }
                return xParts.Length.CompareTo(yParts.Length);
            }
        }
    }
}
