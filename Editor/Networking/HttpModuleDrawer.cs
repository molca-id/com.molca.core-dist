using UnityEngine;
using UnityEditor;
using Molca.Networking.Http;

namespace Molca.Editor
{
    [CustomEditor(typeof(HttpModule))]
    public class HttpModuleDrawer : UnityEditor.Editor
    {
        private Vector2 _headerScrollPosition;
        private bool _showAdvancedSettings = false;
        private bool _showDefaultHeaders = true;

        // SerializedProperty references so edits go through SerializedObject (prevents baseUrl being cleared on ApplyModifiedProperties)
        private SerializedProperty _baseUrlProp;
        private SerializedProperty _maxConcurrentRequestsProp;
        private SerializedProperty _defaultTimeoutProp;
        private SerializedProperty _enableRequestHistoryProp;
        private SerializedProperty _maxHistorySizeProp;
        private SerializedProperty _followRedirectsProp;
        private SerializedProperty _validateSSLProp;
        private SerializedProperty _enableLoggingProp;
        private SerializedProperty _defaultHeaderKeysProp;
        private SerializedProperty _defaultHeaderValuesProp;
        
        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _successStyle;
        private GUIStyle _warningStyle;
        
        private void OnEnable()
        {
            _baseUrlProp = serializedObject.FindProperty("_baseUrl");
            _maxConcurrentRequestsProp = serializedObject.FindProperty("_maxConcurrentRequests");
            _defaultTimeoutProp = serializedObject.FindProperty("_defaultTimeout");
            _enableRequestHistoryProp = serializedObject.FindProperty("_enableRequestHistory");
            _maxHistorySizeProp = serializedObject.FindProperty("_maxHistorySize");
            _followRedirectsProp = serializedObject.FindProperty("_followRedirects");
            _validateSSLProp = serializedObject.FindProperty("_validateSSL");
            _enableLoggingProp = serializedObject.FindProperty("_enableLogging");
            _defaultHeaderKeysProp = serializedObject.FindProperty("_defaultHeaderKeys");
            _defaultHeaderValuesProp = serializedObject.FindProperty("_defaultHeaderValues");
        }

        private void InitializeStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14,
                    margin = new RectOffset(0, 0, 10, 5)
                };
            }

            if (_sectionStyle == null)
            {
                _sectionStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    margin = new RectOffset(0, 0, 5, 5),
                    padding = new RectOffset(10, 10, 10, 10)
                };
            }

            if (_successStyle == null)
            {
                _successStyle = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = new Color(0.2f, 0.7f, 0.2f) },
                    fontSize = 12,
                    fontStyle = FontStyle.Bold
                };
            }

            if (_warningStyle == null)
            {
                _warningStyle = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = new Color(0.8f, 0.6f, 0.2f) },
                    fontSize = 12,
                    fontStyle = FontStyle.Bold
                };
            }
        }
        
        public override void OnInspectorGUI()
        {
            InitializeStyles();

            serializedObject.Update();

            DrawBasicSettings();
            DrawDefaultHeaders();
            DrawAdvancedSettings();
            DrawStatus();

            serializedObject.ApplyModifiedProperties();
        }
        
        private void DrawBasicSettings()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            EditorGUILayout.LabelField("Basic Configuration", EditorStyles.boldLabel);

            // Use SerializedProperty so edits are applied via ApplyModifiedProperties (prevents baseUrl being cleared)
            EditorGUILayout.PropertyField(_baseUrlProp, new GUIContent("Base URL"));
            EditorGUILayout.PropertyField(_maxConcurrentRequestsProp, new GUIContent("Max Concurrent"));
            _maxConcurrentRequestsProp.intValue = Mathf.Clamp(_maxConcurrentRequestsProp.intValue, 1, 20);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_defaultTimeoutProp, new GUIContent("Default Timeout"));
            _defaultTimeoutProp.intValue = Mathf.Clamp(_defaultTimeoutProp.intValue, 1, 300);
            EditorGUILayout.LabelField("seconds", GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
        
        private void DrawDefaultHeaders()
        {
            // Header authoring writes to the SerializeField arrays via SerializedProperty,
            // not via runtime SetDefaultHeader/RemoveDefaultHeader on HttpModule. The
            // module's runtime API mutates the paired HttpState which only exists during
            // play, so it cannot be used to author defaults.

            EditorGUILayout.BeginVertical(_sectionStyle);

            EditorGUILayout.BeginHorizontal();
            _showDefaultHeaders = EditorGUILayout.Foldout(_showDefaultHeaders, "Default Headers", true);
            if (GUILayout.Button("Add Header", GUILayout.Width(80)))
            {
                AddDefaultHeader();
            }
            EditorGUILayout.EndHorizontal();

            if (_showDefaultHeaders)
            {
                EnsureHeaderArraysSized();
                int count = _defaultHeaderKeysProp.arraySize;

                if (count == 0)
                {
                    EditorGUILayout.HelpBox("No default headers configured. Click 'Add Header' to add one.", MessageType.Info);
                }
                else
                {
                    _headerScrollPosition = EditorGUILayout.BeginScrollView(_headerScrollPosition, GUILayout.Height(150));

                    int removeIndex = -1;
                    for (int i = 0; i < count; i++)
                    {
                        var keyProp = _defaultHeaderKeysProp.GetArrayElementAtIndex(i);
                        var valueProp = _defaultHeaderValuesProp.GetArrayElementAtIndex(i);

                        EditorGUILayout.BeginHorizontal();
                        keyProp.stringValue = EditorGUILayout.TextField(keyProp.stringValue, GUILayout.Width(150));
                        valueProp.stringValue = EditorGUILayout.TextField(valueProp.stringValue);

                        if (GUILayout.Button("X", GUILayout.Width(20)))
                        {
                            removeIndex = i;
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.EndScrollView();

                    if (removeIndex >= 0)
                    {
                        _defaultHeaderKeysProp.DeleteArrayElementAtIndex(removeIndex);
                        _defaultHeaderValuesProp.DeleteArrayElementAtIndex(removeIndex);
                    }

                    if (GUILayout.Button("Clear All Headers", GUILayout.Width(120)))
                    {
                        if (EditorUtility.DisplayDialog("Clear Headers",
                            "Are you sure you want to clear all default headers?", "Yes", "No"))
                        {
                            _defaultHeaderKeysProp.ClearArray();
                            _defaultHeaderValuesProp.ClearArray();
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void EnsureHeaderArraysSized()
        {
            // Defensive: keep keys and values arrays the same length even if the asset is malformed.
            if (_defaultHeaderKeysProp.arraySize != _defaultHeaderValuesProp.arraySize)
            {
                int target = Mathf.Min(_defaultHeaderKeysProp.arraySize, _defaultHeaderValuesProp.arraySize);
                _defaultHeaderKeysProp.arraySize = target;
                _defaultHeaderValuesProp.arraySize = target;
            }
        }
        
        private void DrawAdvancedSettings()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            
            _showAdvancedSettings = EditorGUILayout.Foldout(_showAdvancedSettings, "Advanced Settings", true);
            
            if (_showAdvancedSettings)
            {
                EditorGUILayout.PropertyField(_enableRequestHistoryProp, new GUIContent("Enable Request History"));
                if (_enableRequestHistoryProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_maxHistorySizeProp, new GUIContent("Max History Size"));
                    _maxHistorySizeProp.intValue = Mathf.Clamp(_maxHistorySizeProp.intValue, 10, 1000);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(_followRedirectsProp, new GUIContent("Follow Redirects"));
                EditorGUILayout.PropertyField(_validateSSLProp, new GUIContent("Validate SSL"));
                EditorGUILayout.PropertyField(_enableLoggingProp, new GUIContent("Enable Logging"));
            }
            
            EditorGUILayout.EndVertical();
        }
        
        private void DrawStatus()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            
            // HasService is the actual liveness signal; the old `BaseUrl != null`
            // check was always true (the shim returns "" when uninitialized).
            bool httpClientInitialized = Application.isPlaying && RuntimeManager.HasService<IHttpClient>();
            GUIStyle statusStyle = httpClientInitialized ? _successStyle : _warningStyle;
            string statusText = httpClientInitialized ? "HttpClient Initialized" : "HttpClient Not Initialized";
            EditorGUILayout.LabelField(statusText, statusStyle);

            if (httpClientInitialized)
            {
                var http = RuntimeManager.GetService<IHttpClient>();
                EditorGUILayout.LabelField($"Base URL: {http.BaseUrl}");
                EditorGUILayout.LabelField($"Max Concurrent: {http.MaxConcurrentRequests}");
            }

            EditorGUILayout.EndVertical();
        }
        
        private void AddDefaultHeader()
        {
            // Append a new "Content-Type: application/json" entry through SerializedProperty
            // so the change is treated as a normal asset edit (undo/redo, dirty tracking).
            _defaultHeaderKeysProp.arraySize++;
            _defaultHeaderValuesProp.arraySize++;
            int last = _defaultHeaderKeysProp.arraySize - 1;
            _defaultHeaderKeysProp.GetArrayElementAtIndex(last).stringValue = "Content-Type";
            _defaultHeaderValuesProp.GetArrayElementAtIndex(last).stringValue = "application/json";
        }
    }
} 