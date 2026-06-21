using UnityEngine;
using UnityEditor;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using System;
using System.Linq;
using System.Reflection; // Required for GetCustomAttribute
using System.Collections.Generic; // Required for List
using Molca.Editor.Utils;
using Molca.Editor.UI;
using Object = UnityEngine.Object; // Alias for UnityEngine.Object

namespace Molca.Editor
{
    [CustomEditor(typeof(Step), true)]
    [CanEditMultipleObjects]
    public class StepEditor : UnityEditor.Editor
    {
        private Step _step;
        private SerializedProperty _auxiliariesProperty;

        private static System.Type[] _cachedAuxiliaryTypes;
        private static bool _useTypeCache = true;

        private static GUIStyle _boldFoldoutStyle;
        private static GUIStyle BoldFoldoutStyle
        {
            get
            {
                if (_boldFoldoutStyle == null)
                {
                    _boldFoldoutStyle = new GUIStyle(EditorStyles.foldout)
                    {
                        fontStyle = FontStyle.Bold
                    };
                }
                return _boldFoldoutStyle;
            }
        }

        private void OnEnable()
        {
            _step = (Step)target;
            _auxiliariesProperty = serializedObject.FindProperty("auxiliaries");
            
            // Clear cache on enable to ensure we have fresh type list
            _cachedAuxiliaryTypes = null;
        }

        /// <summary>Tick the inspector in play mode so the live "Elapsed" timer updates.</summary>
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            foreach (var t in targets)
            {
                if (t is Step st)
                    st.EnsureAuxiliaryOwnerReferences();
            }
            
            if (targets.Length > 1)
            {
                DrawMultiEditInspector();
            }
            else
            {
                DrawSingleEditInspector();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSingleEditInspector()
        {
            DrawPropertiesExcluding(serializedObject, "auxiliaries", "m_Script");

            if (Application.isPlaying)
            {
                DrawRuntimeInfo();
            }

            DrawAuxiliariesSection();
        }

        private void DrawAuxiliariesSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Auxiliaries", EditorStyles.boldLabel);

            if (_auxiliariesProperty.arraySize > 0)
            {
                for (int i = 0; i < _auxiliariesProperty.arraySize; i++)
                {
                    DrawAuxiliaryItem(i);
                }
            }
            
            if (GUILayout.Button("Add Auxiliary"))
            {
                ShowAddAuxiliaryMenu();
            }
        }
        
        private void DrawAuxiliaryItem(int index)
        {
            var auxiliaryProperty = _auxiliariesProperty.GetArrayElementAtIndex(index);
            if (index >= _step.Auxiliaries.Count) return;
            var auxiliaryObject = _step.Auxiliaries[index];

            // If the auxiliary reference is broken, draw a special UI and stop.
            if (auxiliaryObject == null || !_step.IsAuxiliaryTypeValid(auxiliaryObject))
            {
                DrawInvalidAuxiliaryItem(index);
                return;
            }

                                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
            
            string displayName = auxiliaryObject.GetType().Name;
            var menuAttribute = auxiliaryObject.GetType().GetCustomAttribute<AuxiliaryMenuAttribute>();
            if (menuAttribute != null)
            {
                // Use the last part of the path for a cleaner display name
                displayName = menuAttribute.Path.Split('/').Last();
            }

            // Handle right-click context menu for the foldout
            var foldoutRect = EditorGUILayout.GetControlRect();
            auxiliaryProperty.isExpanded = EditorGUI.Foldout(foldoutRect, auxiliaryProperty.isExpanded, displayName, true, BoldFoldoutStyle);

            if (Event.current.type == EventType.ContextClick && foldoutRect.Contains(Event.current.mousePosition))
            {
                ShowAuxiliaryContextMenu(auxiliaryObject, index);
                Event.current.Use();
            }
            
            GUILayout.FlexibleSpace();

            // Inline enabled checkbox - just the toggle
            var enabledProperty = auxiliaryProperty.FindPropertyRelative("enabled");
            if (enabledProperty != null)
            {
                enabledProperty.boolValue = EditorGUILayout.Toggle(enabledProperty.boolValue, GUILayout.Width(20));
            }

            // Edit script button
            if (GUILayout.Button("Edit", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                OpenAuxiliaryScript(auxiliaryObject);
            }

            if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                EditorApplication.delayCall += () => RemoveAuxiliaryAt(index);
            }
            EditorGUILayout.EndHorizontal();
                    
            if (auxiliaryProperty.isExpanded)
            {
                EditorGUI.indentLevel++;
                
                // Check if this auxiliary type wants custom drawing
                bool hasCustomDrawer = auxiliaryObject.GetType().GetCustomAttribute(typeof(Molca.Sequence.Auxiliary.CustomAuxiliaryDrawerAttribute)) != null;

                if (hasCustomDrawer)
                {
                    // Draw the entire auxiliary as one property so custom drawer works
                    // Also respect the disabled state
                    using (new EditorGUI.DisabledScope(!auxiliaryObject.IsEnabled))
                    {
                        EditorGUILayout.PropertyField(auxiliaryProperty, true);
                    }
                }
                else
                {
                    // Draw all properties with disabled state when auxiliary is disabled
                    var iterator = auxiliaryProperty.Copy();
                    var endProperty = iterator.GetEndProperty();
                    iterator.NextVisible(true);
                    while (iterator.NextVisible(false))
                    {
                        if (SerializedProperty.EqualContents(iterator, endProperty)) break;

                        // Skip the enabled field since we already drew it inline
                        if (iterator.name == "enabled") continue;

                        // Disable other properties when auxiliary is disabled
                        using (new EditorGUI.DisabledScope(!auxiliaryObject.IsEnabled))
                        {
                            EditorGUILayout.PropertyField(iterator, true);
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }
        
        private void DrawInvalidAuxiliaryItem(int index)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            var warningStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = MolcaEditorColors.StatusWarn } };
            EditorGUILayout.LabelField(new GUIContent(" Invalid Auxiliary (Script Missing?)", EditorGUIUtility.IconContent("console.warnicon.sml").image), warningStyle);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Assign", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                ShowAssignAuxiliaryTypeMenu(index);
            }
            
            if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                EditorApplication.delayCall += () => RemoveAuxiliaryAt(index);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void ShowAssignAuxiliaryTypeMenu(int index)
        {
            var menu = new GenericMenu();
            var availableTypes = GetAvailableAuxiliaryTypes();
            
            var menuItems = new List<(string path, Type type)>();

            foreach (var type in availableTypes)
            {
                string menuPath;
                var menuAttribute = type.GetCustomAttribute<AuxiliaryMenuAttribute>();
                if (menuAttribute != null && !string.IsNullOrEmpty(menuAttribute.Path))
                {
                    menuPath = menuAttribute.Path;
                }
                else
                {
                    menuPath = type.Name;
                }
                menuItems.Add((menuPath, type));
            }

            // Sort alphabetically for better organization
            menuItems.Sort((a, b) => a.path.CompareTo(b.path));

            foreach (var item in menuItems)
            {
                var content = new GUIContent(item.path);
                // Pass the index and type to the callback
                menu.AddItem(content, false, () => ReassignAuxiliaryType(index, item.type));
            }

            menu.ShowAsContext();
        }

        private void ReassignAuxiliaryType(int index, Type newType)
        {
            var auxiliaryProperty = _auxiliariesProperty.GetArrayElementAtIndex(index);
            
            // Check if the auxiliary is currently invalid (missing/broken)
            bool isCurrentlyInvalid = auxiliaryProperty.managedReferenceValue == null || 
                                      !_step.IsAuxiliaryTypeValid(_step.Auxiliaries[index]);

            if (isCurrentlyInvalid)
            {
                // For invalid auxiliaries, use the direct YAML editing approach
                Debug.Log($"[ReassignAuxiliary] Detected invalid auxiliary. Using direct YAML type fixing...", _step);
                
                if (Utils.AuxiliaryTypeFixerUtility.FixAuxiliaryTypeInYaml(_step, index, newType))
                {
                    // Success! The YAML has been updated
                    string scenePath = _step.gameObject.scene.path;
                    
                    EditorApplication.delayCall += () =>
                    {
                        Utils.AuxiliaryTypeFixerUtility.PromptSceneReload(scenePath);
                    };
                    
                    return;
                }
                else
                {
                    Debug.LogError("[ReassignAuxiliary] Failed to fix type in YAML. Falling back to standard approach.", _step);
                    // Fall through to standard approach
                }
            }

            // Standard approach for valid types or if YAML editing failed
            // 1. Try to cache data from SerializedProperty (works for valid types)
            var cachedData = new Dictionary<string, object>();
            try
            {
                var iterator = auxiliaryProperty.Copy();
                var endProperty = iterator.GetEndProperty();
                
                // Use depth tracking to ensure we capture ALL nested properties
                int startDepth = iterator.depth;
                bool enterChildren = true;
                
                while (iterator.NextVisible(enterChildren))
                {
                    // Stop if we've gone back to or beyond the parent's depth
                    if (iterator.depth <= startDepth)
                    {
                        break;
                    }
                    
                    // Safety check: ensure this property is actually a child of the auxiliary
                    if (!iterator.propertyPath.StartsWith(auxiliaryProperty.propertyPath + "."))
                    {
                        break; // We've gone outside the auxiliary's scope
                    }
                    
                    string relativePath = iterator.propertyPath.Replace(auxiliaryProperty.propertyPath + ".", "");
                    
                    // Skip if somehow invalid
                    if (string.IsNullOrEmpty(relativePath) || relativePath == iterator.propertyPath)
                    {
                        enterChildren = false;
                        continue;
                    }
                    
                    // Cache the value
                    try
                    {
                        object value = SerializedPropertyUtils.GetSerializedPropertyValue(iterator);
                        if (value != null)
                        {
                            cachedData[relativePath] = value;
                            enterChildren = false; // Don't enter children of properties we've already cached
                        }
                        else
                        {
                            enterChildren = true; // Enter children to capture nested data
                        }
                    }
                    catch
                    {
                        enterChildren = true; // Try to enter children if we can't cache this property
                    }
                }
                
                Debug.Log($"[ReassignAuxiliary] Cached {cachedData.Count} properties from SerializedProperty", _step);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not cache all data from invalid auxiliary via SerializedProperty. Error: {e.Message}", _step);
            }
            
            // 2. Reassign the type by creating a new instance
            try
            {
                var newInstance = Activator.CreateInstance(newType);
                auxiliaryProperty.managedReferenceValue = newInstance;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create and assign new auxiliary instance of type {newType.Name}: {e.Message}", _step);
                return; // Stop if we can't create the new instance
            }
            
            // IMPORTANT: Apply and update to sync the serialized object with the new instance
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();
            
            // Get a fresh reference to the auxiliary property after the update
            auxiliaryProperty = _auxiliariesProperty.GetArrayElementAtIndex(index);
            
            // 3. Restore the cached data to the new instance
            int restoredCount = 0;
            int skippedCount = 0;
            
            if (cachedData.Count > 0)
            {
                foreach(var entry in cachedData)
                {
                    var childProperty = auxiliaryProperty.FindPropertyRelative(entry.Key);
                    if (childProperty != null)
                    {
                        try
                        {
                            SerializedPropertyUtils.SetSerializedPropertyValue(childProperty, entry.Value);
                            restoredCount++;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"Failed to restore property '{entry.Key}': {e.Message}", _step);
                            skippedCount++;
                        }
                    }
                    else
                    {
                        // Property doesn't exist in new type - this is expected when types are different
                        skippedCount++;
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
            Repaint();
            
            // Report results
            if (restoredCount > 0)
            {
                Debug.Log($"<color=green>[ReassignAuxiliary]</color> Successfully reassigned to '{newType.Name}' and restored {restoredCount}/{cachedData.Count} fields. ({skippedCount} skipped)", _step);
            }
            else if (cachedData.Count > 0)
            {
                Debug.LogWarning($"[ReassignAuxiliary] Reassigned to '{newType.Name}' but could not restore any of the {cachedData.Count} fields - types may be incompatible.", _step);
            }
            else
            {
                Debug.Log($"[ReassignAuxiliary] Reassigned to '{newType.Name}'. No data was available to restore.", _step);
            }
        }
        
        private void ShowAddAuxiliaryMenu()
        {
            var menu = new GenericMenu();
            var availableTypes = GetAvailableAuxiliaryTypes();
            var existingTypes = _step.Auxiliaries
                .Where(aux => aux != null)
                .Select(aux => aux.GetType())
                .ToHashSet();

            // Create a list of items to be sorted alphabetically
            var menuItems = new List<(string path, Type type, bool allowMultiple)>();

            foreach (var type in availableTypes)
            {
                string menuPath;
                bool allowMultiple = false;
                var menuAttribute = type.GetCustomAttribute<AuxiliaryMenuAttribute>();
                
                // If the attribute exists, use its path and allowMultiple setting. Otherwise, use the class name.
                if (menuAttribute != null)
                {
                    menuPath = menuAttribute.Path;
                    allowMultiple = menuAttribute.AllowMultiple;
                }
                else
                {
                    menuPath = type.Name;
                    allowMultiple = false; // Default to false for types without the attribute
                }
                menuItems.Add((menuPath, type, allowMultiple));
            }

            // Sort the menu items alphabetically by path for better organization
            menuItems.Sort((a, b) => a.path.CompareTo(b.path));

            // Add the sorted items to the menu
            foreach (var item in menuItems)
            {
                var content = new GUIContent(item.path);
                
                // Check if this type already exists and if multiple instances are not allowed
                if (existingTypes.Contains(item.type) && !item.allowMultiple)
                {
                    menu.AddDisabledItem(content);
                }
                else
                {
                    // Pass the type to the callback
                    menu.AddItem(content, false, () => AddAuxiliary(item.type));
                }
            }

            menu.ShowAsContext();
        }
        
        private Type[] GetAvailableAuxiliaryTypes()
        {
            if (_cachedAuxiliaryTypes != null)
            {
                return _cachedAuxiliaryTypes;
            }
            
            var baseType = typeof(StepAuxiliary);
            
            // Method 1: Use Unity's TypeCache (recommended - works across all assemblies)
            if (_useTypeCache)
            {
                try
                {
                    // TypeCache.GetTypesDerivedFrom automatically discovers types from all loaded assemblies
                    _cachedAuxiliaryTypes = UnityEditor.TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                        .Where(type => !type.IsAbstract && !IsTestAssembly(type.Assembly))
                        .ToArray();
                    
                    Debug.Log($"[StepEditor] Discovered {_cachedAuxiliaryTypes.Length} auxiliary types using TypeCache");
                    return _cachedAuxiliaryTypes;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StepEditor] TypeCache failed, falling back to reflection: {e.Message}");
                    _useTypeCache = false; // Fall back to reflection
                }
            }
            
            // Method 2: Fallback using AppDomain reflection with error handling
            var auxiliaryTypes = new List<Type>();
            
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (IsTestAssembly(assembly)) continue;
                    var types = assembly.GetTypes()
                        .Where(type => type.IsSubclassOf(baseType) && !type.IsAbstract);
                    auxiliaryTypes.AddRange(types);
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Some types in the assembly couldn't be loaded, but we can still get the ones that did
                    var loadedTypes = ex.Types.Where(t => t != null && t.IsSubclassOf(baseType) && !t.IsAbstract);
                    auxiliaryTypes.AddRange(loadedTypes);
                    Debug.LogWarning($"[StepEditor] Partial load from assembly {assembly.GetName().Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[StepEditor] Could not load types from assembly {assembly.GetName().Name}: {ex.Message}");
                }
            }
            
            _cachedAuxiliaryTypes = auxiliaryTypes.ToArray();
            Debug.Log($"[StepEditor] Discovered {_cachedAuxiliaryTypes.Length} auxiliary types using reflection");

            return _cachedAuxiliaryTypes;
        }

        private static readonly Dictionary<Assembly, bool> _testAssemblyCache = new();

        /// <summary>
        /// True if <paramref name="assembly"/> is a test assembly (one that references the NUnit
        /// framework). Auxiliaries defined for unit tests must never surface in the authoring menu.
        /// </summary>
        private static bool IsTestAssembly(Assembly assembly)
        {
            if (assembly == null) return false;
            if (_testAssemblyCache.TryGetValue(assembly, out var cached)) return cached;

            bool isTest = false;
            try
            {
                isTest = assembly.GetReferencedAssemblies()
                    .Any(name => name.Name.StartsWith("nunit.framework", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Dynamic or partially-loaded assemblies may throw; treat as non-test.
            }

            _testAssemblyCache[assembly] = isTest;
            return isTest;
        }
        
        private void AddAuxiliary(Type auxiliaryType)
        {
            if (auxiliaryType == null || !auxiliaryType.IsSubclassOf(typeof(StepAuxiliary))) return;

            Undo.RecordObject(_step, $"Add Auxiliary {auxiliaryType.Name}");
            var auxiliary = Activator.CreateInstance(auxiliaryType) as StepAuxiliary;
            if (auxiliary != null)
            {
                auxiliary.BindOwnerFromStep(_step);

                _step.AddAuxiliary(auxiliary);

                EditorUtility.SetDirty(_step);
            }
        }
        
        private void RemoveAuxiliaryAt(int index)
        {
            if (index < 0 || index >= _auxiliariesProperty.arraySize) return;

            Undo.RecordObject(_step, "Remove Auxiliary");
            var auxiliaryToRemove = _step.Auxiliaries[index];
            auxiliaryToRemove?.OnRemoved();
            _step.RemoveAuxiliaryAt(index);
            EditorUtility.SetDirty(_step);
        }

        private void DrawMultiEditInspector()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Multi-Edit Mode", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Auxiliary editing for multiple steps is available in the Sequence Visualizer (Molca > Utilities > Sequence Visualizer) — select the same steps there to batch-edit auxiliaries.", MessageType.Info);
            if (GUILayout.Button("Open Sequence Visualizer"))
            {
                SequenceVisualizerWindow.ShowWindow();
            }
        }

        private void DrawRuntimeInfo()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);
            
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Current Status:", GUILayout.Width(120));
                var statusColor = GetStatusColor(_step.CurrentStatus);
                var originalColor = GUI.color;
                GUI.color = statusColor;
                EditorGUILayout.LabelField(_step.CurrentStatus.ToString());
                GUI.color = originalColor;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Toggle("Internally Completed", _step.IsInternallyCompleted);
                EditorGUILayout.Toggle("Fully Completed", _step.IsCompleted);

                DrawTimingInfo(_step);
            }

            // Blocked-on-what hint: an active, not-yet-completed step whose CanComplete() gate
            // is closed. Drawn outside the disabled scope so the help box reads at full contrast.
            if (_step.CurrentStatus == StepStatus.Active && !_step.IsInternallyCompleted && !_step.CanCompleteNow())
            {
                string reason = _step.GetCompletionBlockReason();
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(reason)
                        ? "Blocked: this step's completion condition is not met yet."
                        : $"Blocked: {reason}",
                    MessageType.Warning);
            }

            var children = _step.Children;
            if (children != null && children.Any())
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Children", EditorStyles.boldLabel);
                foreach (var child in children)
                {
                    if (child == null) continue;
                    EditorGUILayout.ObjectField(child.name, child, typeof(Step), true);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime Actions", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Complete Step")) _step.ForceComplete();
            if (GUILayout.Button("Reset Step")) _step.ResetStep();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws StartTime / EndTime / elapsed for a step that has been activated this session.
        /// Elapsed is live while the step is active, and frozen to (End - Start) once it ends.
        /// </summary>
        private static void DrawTimingInfo(Step step)
        {
            // StartTime is default(DateTime) until the step first goes Active.
            if (step.StartTime == default) return;

            EditorGUILayout.LabelField("Start Time", step.StartTime.ToString("HH:mm:ss.fff"));

            bool ended = step.CurrentStatus != StepStatus.Active && step.EndTime >= step.StartTime;
            if (ended)
            {
                EditorGUILayout.LabelField("End Time", step.EndTime.ToString("HH:mm:ss.fff"));
            }

            var elapsed = (ended ? step.EndTime : System.DateTime.Now) - step.StartTime;
            EditorGUILayout.LabelField("Elapsed", $"{elapsed.TotalSeconds:F1}s");
        }

        private static Color GetStatusColor(StepStatus status)
        {
            switch (status)
            {
                case StepStatus.Active: return MolcaEditorColors.StatusOk;
                case StepStatus.Completed: return MolcaEditorColors.StatusWarn;
                default: return MolcaEditorColors.StatusIdle;
            }
        }

        private void OpenAuxiliaryScript(StepAuxiliary auxiliary)
        {
            var auxiliaryType = auxiliary.GetType();
            var scriptPath = FindScriptPath(auxiliaryType);

            if (!string.IsNullOrEmpty(scriptPath))
            {
                var scriptAsset = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                if (scriptAsset != null)
                {
                    AssetDatabase.OpenAsset(scriptAsset);
                }
                else
                {
                    Debug.LogWarning($"Could not load script asset at path: {scriptPath}");
                }
            }
            else
            {
                Debug.LogWarning($"Could not find script file for auxiliary type: {auxiliaryType.Name}");
            }
        }

        private string FindScriptPath(System.Type type)
        {
            string typeName = type.Name;
            string namespaceName = type.Namespace;

            // Method 1: Search by exact class name using AssetDatabase
            var guids = AssetDatabase.FindAssets($"{typeName} t:MonoScript");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script != null)
                {
                    var scriptClass = script.GetClass();
                    if (scriptClass == type)
                    {
                        return path;
                    }

                    // Also check if it's a base class or interface implementation
                    if (scriptClass != null && (type.IsAssignableFrom(scriptClass) || scriptClass.IsSubclassOf(type)))
                    {
                        return path;
                    }
                }
            }

            // Method 2: Try common locations based on namespace
            if (!string.IsNullOrEmpty(namespaceName))
            {
                var namespacePath = namespaceName.Replace(".", "/");
                var possiblePaths = new[]
                {
                    $"Assets/{namespacePath}/{typeName}.cs",
                    $"Assets/Scripts/{namespacePath}/{typeName}.cs",
                    $"Assets/_Molca/{namespacePath}/{typeName}.cs",
                    $"Assets/_Molca/_Core/{namespacePath}/{typeName}.cs",
                    $"Assets/_Molca/_Core/Sequence/{typeName}.cs",
                    $"Assets/_Molca/_Core/Sequence/Auxiliary/{typeName}.cs"
                };

                foreach (var possiblePath in possiblePaths)
                {
                    if (AssetDatabase.LoadAssetAtPath<MonoScript>(possiblePath) != null)
                    {
                        return possiblePath;
                    }
                }
            }

            // Method 3: Search in all script files recursively
            var allScriptGuids = AssetDatabase.FindAssets("t:MonoScript");
            foreach (var guid in allScriptGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script != null)
                {
                    var scriptClass = script.GetClass();
                    if (scriptClass != null && scriptClass.Name == typeName)
                    {
                        // Additional check for namespace if available
                        if (string.IsNullOrEmpty(namespaceName) || scriptClass.Namespace == namespaceName)
                        {
                            return path;
                        }
                    }
                }
            }

            Debug.LogWarning($"Could not find script file for type: {type.FullName}");
            return null;
        }

        private void ShowAuxiliaryContextMenu(StepAuxiliary auxiliary, int index)
        {
            var menu = new GenericMenu();

            // Copy/paste share AuxiliaryClipboard with the Sequence Visualizer batch panel.
            menu.AddItem(new GUIContent("Copy Values"), false, () => AuxiliaryClipboard.Copy(auxiliary));

            if (AuxiliaryClipboard.CanPasteInto(auxiliary))
            {
                menu.AddItem(new GUIContent("Paste Values"), false, () => PasteAuxiliaryValues(auxiliary));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Paste Values"));
            }

            menu.ShowAsContext();
        }

        private void PasteAuxiliaryValues(StepAuxiliary targetAuxiliary)
        {
            Undo.RecordObject(_step, $"Paste Auxiliary Values to {targetAuxiliary.GetType().Name}");
            if (AuxiliaryClipboard.Paste(targetAuxiliary))
            {
                EditorUtility.SetDirty(_step);
            }
            else
            {
                Debug.LogError("Cannot paste: clipboard is empty or incompatible with the target auxiliary type.");
            }
        }
    }
}