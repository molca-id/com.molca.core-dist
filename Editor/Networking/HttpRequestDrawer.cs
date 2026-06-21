using UnityEngine;
using UnityEditor;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;
using Molca.Settings;
using System;
using System.Collections.Generic;
using UnityEditorInternal;

namespace Molca.Editor
{
    [CustomEditor(typeof(HttpRequestAsset))]
    public class HttpRequestDrawer : UnityEditor.Editor
    {
        private HttpRequestAsset _asset;
        private SerializedProperty _requestProperty;
        private Vector2 _jsonScrollPosition;
        private Vector2 _responseScrollPosition;
        private HttpResponse _lastResponse;
        private bool _isSending = false;
        private int _selectedTab = 0;
        private readonly string[] _tabs = { "Request", "Headers", "Body", "Settings", "Response" };
        private string[] _validationErrors = new string[0];
        private bool _showResponse = true;
        private bool _showPreviewUrl = true;

        private ReorderableList _headerList;
        private ReorderableList _paramList;
        private ReorderableList _formFieldList;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _tabStyle;
        private GUIStyle _activeTabStyle;
        private GUIStyle _successStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _foldoutStyle;

        // Icons
        private GUIContent _addIcon;
        private GUIContent _removeIcon;
        private GUIContent _duplicateIcon;

        private void OnEnable()
        {
            _asset = (HttpRequestAsset)target;
            _requestProperty = serializedObject.FindProperty("request");
            InitializeStyles();
            InitializeIcons();
            SetupReorderableLists();
            Validate();
        }

        private void InitializeStyles()
        {
            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(0, 0, 10, 5)
            };

            _sectionStyle = new GUIStyle(EditorStyles.helpBox)
            {
                margin = new RectOffset(0, 0, 5, 5),
                padding = new RectOffset(10, 10, 10, 10)
            };

            _tabStyle = new GUIStyle(EditorStyles.toolbarButton)
            {
                fontSize = 11,
                padding = new RectOffset(10, 10, 5, 5)
            };

            _activeTabStyle = new GUIStyle(_tabStyle)
            {
                normal = { textColor = Color.white, background = EditorGUIUtility.Load("builtin skins/darkskin/images/pre button on.png") as Texture2D },
                fontStyle = FontStyle.Bold
            };

            _successStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.2f, 0.7f, 0.2f) },
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };

            _errorStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.8f, 0.2f, 0.2f) },
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };

            _foldoutStyle = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold
            };
        }

        private void InitializeIcons()
        {
            _addIcon = EditorGUIUtility.IconContent("d_Toolbar Plus", "Add");
            _removeIcon = EditorGUIUtility.IconContent("d_Toolbar Minus", "Remove");
            _duplicateIcon = EditorGUIUtility.IconContent("d_TreeEditor.Duplicate", "Duplicate");
        }

        private void SetupReorderableLists()
        {
            var headersProperty = _requestProperty.FindPropertyRelative("headers");
            _headerList = new ReorderableList(serializedObject, headersProperty, true, true, true, true)
            {
                drawHeaderCallback = (Rect rect) =>
                {
                    EditorGUI.LabelField(new Rect(rect.x, rect.y, rect.width - 60, rect.height), "Headers", EditorStyles.boldLabel);
                    if (GUI.Button(new Rect(rect.x + rect.width - 70, rect.y, 70, rect.height), "Add New", EditorStyles.miniButton))
                    {
                        var index = headersProperty.arraySize;
                        headersProperty.arraySize++;
                        var element = headersProperty.GetArrayElementAtIndex(index);
                        element.FindPropertyRelative("key").stringValue = "";
                        element.FindPropertyRelative("value").stringValue = "";
                        element.FindPropertyRelative("isEnabled").boolValue = true;
                    }
                },
                drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {
                    if (index < 0 || index >= headersProperty.arraySize)
                        return;
                        
                    var element = headersProperty.GetArrayElementAtIndex(index);
                    if (element == null)
                        return;
                        
                    rect.y += 2;
                    var keyRect = new Rect(rect.x + 20, rect.y, rect.width * 0.4f - 20, EditorGUIUtility.singleLineHeight);
                    var valueRect = new Rect(rect.x + rect.width * 0.4f + 5, rect.y, rect.width * 0.6f - 55, EditorGUIUtility.singleLineHeight);
                    var enabledRect = new Rect(rect.x, rect.y, 20, EditorGUIUtility.singleLineHeight);
                    var duplicateRect = new Rect(rect.x + rect.width - 40, rect.y, 25, EditorGUIUtility.singleLineHeight);
                    var removeRect = new Rect(rect.x + rect.width - 15, rect.y, 25, EditorGUIUtility.singleLineHeight);

                    var enabledProp = element.FindPropertyRelative("isEnabled");
                    var keyProp = element.FindPropertyRelative("key");
                    var valueProp = element.FindPropertyRelative("value");

                    if (enabledProp != null) EditorGUI.PropertyField(enabledRect, enabledProp, GUIContent.none);
                    if (keyProp != null) EditorGUI.PropertyField(keyRect, keyProp, GUIContent.none);
                    if (valueProp != null) EditorGUI.PropertyField(valueRect, valueProp, GUIContent.none);

                    if (GUI.Button(duplicateRect, _duplicateIcon, EditorStyles.iconButton))
                    {
                        headersProperty.InsertArrayElementAtIndex(index);
                    }
                    if (GUI.Button(removeRect, _removeIcon, EditorStyles.iconButton))
                    {
                        headersProperty.DeleteArrayElementAtIndex(index);
                    }
                }
            };

            var paramsProperty = _requestProperty.FindPropertyRelative("queryParams");
            _paramList = new ReorderableList(serializedObject, paramsProperty, true, true, true, true)
            {
                drawHeaderCallback = (Rect rect) =>
                {
                    EditorGUI.LabelField(new Rect(rect.x, rect.y, rect.width - 60, rect.height), "Query Parameters", EditorStyles.boldLabel);
                },
                drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {
                    if (index < 0 || index >= paramsProperty.arraySize)
                        return;
                        
                    var element = paramsProperty.GetArrayElementAtIndex(index);
                    if (element == null)
                        return;
                        
                    rect.y += 2;
                    var keyRect = new Rect(rect.x + 20, rect.y, rect.width * 0.4f - 20, EditorGUIUtility.singleLineHeight);
                    var valueRect = new Rect(rect.x + rect.width * 0.4f + 5, rect.y, rect.width * 0.6f - 55, EditorGUIUtility.singleLineHeight);
                    var enabledRect = new Rect(rect.x, rect.y, 20, EditorGUIUtility.singleLineHeight);
                    var duplicateRect = new Rect(rect.x + rect.width - 40, rect.y, 25, EditorGUIUtility.singleLineHeight);
                    var removeRect = new Rect(rect.x + rect.width - 15, rect.y, 25, EditorGUIUtility.singleLineHeight);

                    var enabledProp = element.FindPropertyRelative("isEnabled");
                    var keyProp = element.FindPropertyRelative("key");
                    var valueProp = element.FindPropertyRelative("value");

                    if (enabledProp != null) EditorGUI.PropertyField(enabledRect, enabledProp, GUIContent.none);
                    if (keyProp != null) EditorGUI.PropertyField(keyRect, keyProp, GUIContent.none);
                    if (valueProp != null) EditorGUI.PropertyField(valueRect, valueProp, GUIContent.none);

                    if (GUI.Button(duplicateRect, _duplicateIcon, EditorStyles.iconButton))
                    {
                        paramsProperty.InsertArrayElementAtIndex(index);
                    }
                    if (GUI.Button(removeRect, _removeIcon, EditorStyles.iconButton))
                    {
                        paramsProperty.DeleteArrayElementAtIndex(index);
                    }
                }
            };

            var formsProperty = _requestProperty.FindPropertyRelative("formFields");
            _formFieldList = new ReorderableList(serializedObject, formsProperty, true, true, true, true)
            {
                drawHeaderCallback = (Rect rect) =>
                {
                    EditorGUI.LabelField(new Rect(rect.x, rect.y, rect.width - 60, rect.height), "Form Data", EditorStyles.boldLabel);
                    if (GUI.Button(new Rect(rect.x + rect.width - 70, rect.y, 70, rect.height), "Add New", EditorStyles.miniButton))
                    {
                        var index = formsProperty.arraySize;
                        formsProperty.arraySize++;
                        var element = formsProperty.GetArrayElementAtIndex(index);
                        element.FindPropertyRelative("key").stringValue = "";
                        element.FindPropertyRelative("value").stringValue = "";
                        element.FindPropertyRelative("isEnabled").boolValue = true;
                    }
                },
                drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {
                    if (index < 0 || index >= formsProperty.arraySize)
                        return;
                        
                    var element = formsProperty.GetArrayElementAtIndex(index);
                    if (element == null)
                        return;
                        
                    rect.y += 2;
                    var keyRect = new Rect(rect.x + 20, rect.y, rect.width * 0.4f - 20, EditorGUIUtility.singleLineHeight);
                    var valueRect = new Rect(rect.x + rect.width * 0.4f + 5, rect.y, rect.width * 0.6f - 55, EditorGUIUtility.singleLineHeight);
                    var enabledRect = new Rect(rect.x, rect.y, 20, EditorGUIUtility.singleLineHeight);
                    var duplicateRect = new Rect(rect.x + rect.width - 40, rect.y, 25, EditorGUIUtility.singleLineHeight);
                    var removeRect = new Rect(rect.x + rect.width - 15, rect.y, 25, EditorGUIUtility.singleLineHeight);

                    var enabledProp = element.FindPropertyRelative("isEnabled");
                    var keyProp = element.FindPropertyRelative("key");
                    var valueProp = element.FindPropertyRelative("value");

                    if (enabledProp != null) EditorGUI.PropertyField(enabledRect, enabledProp, GUIContent.none);
                    if (keyProp != null) EditorGUI.PropertyField(keyRect, keyProp, GUIContent.none);
                    if (valueProp != null) EditorGUI.PropertyField(valueRect, valueProp, GUIContent.none);

                    if (GUI.Button(duplicateRect, _duplicateIcon, EditorStyles.iconButton))
                    {
                        formsProperty.InsertArrayElementAtIndex(index);
                    }
                    if (GUI.Button(removeRect, _removeIcon, EditorStyles.iconButton))
                    {
                        formsProperty.DeleteArrayElementAtIndex(index);
                    }
                }
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            DrawHeaderTitle();
            DrawTabs();
            DrawTabContent();
            DrawValidationErrors();
            DrawSendButton();

            if (EditorGUI.EndChangeCheck())
            {
                Validate();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void Validate()
        {
            _asset.Validate(out _validationErrors);
        }

        private void DrawValidationErrors()
        {
            if (_validationErrors.Length > 0)
            {
                EditorGUILayout.Space();
                foreach (var error in _validationErrors)
                {
                    EditorGUILayout.HelpBox(error, MessageType.Error);
                }
            }
        }


        private void DrawHeaderTitle()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("HTTP Request", _headerStyle);
            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("name"), GUIContent.none);
            EditorGUILayout.Space(5);
        }

        private void DrawTabs()
        {
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < _tabs.Length; i++)
            {
                GUIStyle style = i == _selectedTab ? _activeTabStyle : _tabStyle;
                if (GUILayout.Button(_tabs[i], style))
                {
                    _selectedTab = i;
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(5);
        }

        private void DrawTabContent()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            switch (_selectedTab)
            {
                case 0: DrawRequestTab(); break;
                case 1: DrawHeadersTab(); break;
                case 2: DrawBodyTab(); break;
                case 3: DrawSettingsTab(); break;
                case 4: DrawResponseTab(); break;
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawRequestTab()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("method"), GUIContent.none, GUILayout.Width(80));
            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("url"), GUIContent.none);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            _paramList.DoLayoutList();

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("useFullUrl"));

            if (!_asset.request.useFullUrl)
            {
                var httpModule = GlobalSettings.GetModule<HttpModule>();
                string baseUrl = httpModule?.BaseUrl ?? "Not Set";
                EditorGUILayout.LabelField("Base URL: ", baseUrl, EditorStyles.miniLabel);
            }

            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("expectedResponseType"));

            EditorGUILayout.Space();
            _showPreviewUrl = EditorGUILayout.Foldout(_showPreviewUrl, "Preview URL", true, _foldoutStyle);
            if (_showPreviewUrl)
            {
                string previewUrl = "Invalid URL";
                try
                {
                    previewUrl = _asset.GetFullUrl();
                }
                catch (Exception e)
                {
                    previewUrl = $"Error: {e.Message}";
                }
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.SelectableLabel(previewUrl, EditorStyles.helpBox, GUILayout.Height(EditorGUIUtility.singleLineHeight * 2));
                if (GUILayout.Button("Copy", GUILayout.Width(50)))
                {
                    EditorGUIUtility.systemCopyBuffer = previewUrl;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawHeadersTab()
        {
            _headerList.DoLayoutList();

            if (GUILayout.Button("Add Common Headers", GUILayout.Height(25)))
            {
                AddCommonHeaders(_requestProperty.FindPropertyRelative("headers"));
            }
        }

        private void DrawBodyTab()
        {
            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("bodyType"));
            
            if (_asset.request.bodyType == BodyType.Form)
            {
                _formFieldList.DoLayoutList();
            }
            else if (_asset.request.bodyType == BodyType.Json)
            {
                DrawJsonBody();
            }
        }

        private void DrawJsonBody()
        {
            EditorGUILayout.LabelField("JSON Body", EditorStyles.boldLabel);
            var jsonBodyProperty = _requestProperty.FindPropertyRelative("jsonBody");

            _jsonScrollPosition = EditorGUILayout.BeginScrollView(_jsonScrollPosition, GUILayout.Height(200));
            jsonBodyProperty.stringValue = EditorGUILayout.TextArea(jsonBodyProperty.stringValue, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Format JSON", GUILayout.Width(100)))
            {
                FormatJsonBody(jsonBodyProperty);
            }
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                jsonBodyProperty.stringValue = "";
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettingsTab()
        {
            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("timeout"));
            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("followRedirects"));
            EditorGUILayout.PropertyField(_requestProperty.FindPropertyRelative("validateSSL"));
        }

        private void DrawResponseTab()
        {
            if (_lastResponse == null)
            {
                EditorGUILayout.HelpBox("Send a request to see the response here.", MessageType.Info);
                return;
            }

            _showResponse = EditorGUILayout.Foldout(_showResponse, "Response Details", true, _foldoutStyle);
            if (!_showResponse) return;

            // Status and Time
            EditorGUILayout.BeginHorizontal();
            GUIStyle statusStyle = _lastResponse.isSuccess ? _successStyle : _errorStyle;
            EditorGUILayout.LabelField("Status:", EditorStyles.boldLabel, GUILayout.Width(50));
            EditorGUILayout.LabelField($"{_lastResponse.statusCode} {_lastResponse.statusMessage}", statusStyle);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Time:", EditorStyles.boldLabel, GUILayout.Width(40));
            EditorGUILayout.LabelField($"{_lastResponse.responseTime:F2}s", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();
            
            // Size and Type
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Size:", EditorStyles.boldLabel, GUILayout.Width(50));
            EditorGUILayout.LabelField($"{_lastResponse.contentLength} bytes");
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(_lastResponse.contentType))
            {
                EditorGUILayout.LabelField("Type:", EditorStyles.boldLabel, GUILayout.Width(40));
                EditorGUILayout.LabelField(_lastResponse.contentType, GUILayout.Width(150));
            }
            EditorGUILayout.EndHorizontal();


            EditorGUILayout.Space();

            // Headers
            if (_lastResponse.headers != null && _lastResponse.headers.Count > 0)
            {
                EditorGUILayout.LabelField("Response Headers", EditorStyles.boldLabel);
                _responseScrollPosition = EditorGUILayout.BeginScrollView(_responseScrollPosition, GUILayout.Height(100), GUILayout.ExpandWidth(true));
                foreach (var header in _lastResponse.headers)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(header.Key, GUILayout.Width(150));
                    EditorGUILayout.SelectableLabel(header.Value, EditorStyles.helpBox, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.Space();
            }

            // Body
            EditorGUILayout.LabelField("Response Body", EditorStyles.boldLabel);
            string responseText = _lastResponse.GetContentAsString();

            if (!string.IsNullOrEmpty(responseText))
            {
                 if (_lastResponse.IsJson)
                {
                    try
                    {
                        var formatted = Newtonsoft.Json.JsonConvert.SerializeObject(
                            Newtonsoft.Json.JsonConvert.DeserializeObject(responseText), 
                            Newtonsoft.Json.Formatting.Indented);
                        responseText = formatted;
                    }
                    catch { }
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Copy Body", GUILayout.Width(80)))
                {
                    EditorGUIUtility.systemCopyBuffer = responseText;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.SelectableLabel(responseText, EditorStyles.helpBox, GUILayout.Height(200), GUILayout.ExpandHeight(true));
            }
            else
            {
                EditorGUILayout.HelpBox("No text content in response.", MessageType.Info);
            }
        }


        private void DrawSendButton()
        {
            EditorGUILayout.Space();
            EditorGUI.BeginDisabledGroup(_isSending || _validationErrors.Length > 0);

            var buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = 14;
            buttonStyle.fontStyle = FontStyle.Bold;
            buttonStyle.normal.textColor = Color.white;

            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = _isSending ? Color.gray : (_validationErrors.Length > 0 ? Color.red : new Color(0.81f, 1f, 0.2f));
            
            if (GUILayout.Button(_isSending ? "Sending..." : "Send Request", buttonStyle, GUILayout.Height(35)))
            {
                SendRequest();
            }

            GUI.backgroundColor = originalColor;
            EditorGUI.EndDisabledGroup();
        }

        private async void SendRequest()
        {
            if (_isSending) return;

            Validate();
            if (_validationErrors.Length > 0)
            {
                EditorUtility.DisplayDialog("Validation Error",
                    $"Request validation failed:\n{string.Join("\n", _validationErrors)}", "OK");
                return;
            }

            _isSending = true;
            _selectedTab = 4; // Switch to response tab
            Repaint();

            try
            {
                if (!Application.isPlaying)
                {
                    _lastResponse = await EditorHttpClient.SendAsync(_asset.request);
                }
                else
                {
                    _lastResponse = await _asset.SendAsync();
                }
            }
            catch (Exception e)
            {
                _lastResponse = new HttpResponse
                {
                    isSuccess = false,
                    errorMessage = e.Message,
                    statusCode = 0,
                    statusMessage = "Error"
                };
            }
            finally
            {
                _isSending = false;
                Repaint();
            }
        }

        private void AddCommonHeaders(SerializedProperty headersProperty)
        {
            var existingHeaders = new HashSet<string>();
            for(int i = 0; i < headersProperty.arraySize; i++)
            {
                existingHeaders.Add(headersProperty.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue);
            }

            var commonHeaders = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "Accept", "application/json" },
                { "Authorization", "Bearer " },
                { "User-Agent", "Unity-HttpRequestAsset/1.0" },
                { "Cache-Control", "no-cache" }
            };

            foreach(var header in commonHeaders)
            {
                if (!existingHeaders.Contains(header.Key))
                {
                    var index = headersProperty.arraySize;
                    headersProperty.arraySize++;
                    var element = headersProperty.GetArrayElementAtIndex(index);
                    element.FindPropertyRelative("key").stringValue = header.Key;
                    element.FindPropertyRelative("value").stringValue = header.Value;
                    element.FindPropertyRelative("isEnabled").boolValue = true;
                }
            }
        }

        private void FormatJsonBody(SerializedProperty jsonBodyProperty)
        {
            try
            {
                if (!string.IsNullOrEmpty(jsonBodyProperty.stringValue))
                {
                    var jsonObject = Newtonsoft.Json.JsonConvert.DeserializeObject(jsonBodyProperty.stringValue);
                    jsonBodyProperty.stringValue = Newtonsoft.Json.JsonConvert.SerializeObject(jsonObject, Newtonsoft.Json.Formatting.Indented);
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("JSON Format Error", $"Invalid JSON: {e.Message}", "OK");
            }
        }
    }
}