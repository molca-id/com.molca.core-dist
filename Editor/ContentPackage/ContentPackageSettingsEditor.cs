using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Molca.ContentPackage;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Services;
using Molca.Editor.UI;
using Molca.Settings;

namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Two-column inspector for <see cref="ContentPackageSettings"/>.
    /// Left: scrollable package list with health indicators.
    /// Right: selected package detail form.
    /// Bottom: collapsible system settings.
    /// </summary>
    [CustomEditor(typeof(ContentPackageSettings))]
    public partial class ContentPackageSettingsEditor : UnityEditor.Editor
    {
        // ── Selection & search ───────────────────────────────────────────────
        private string _selectedPackageId;
        private int    _selectedPackageIndex = -1;   // fallback when packageId is empty mid-edit
        private string _searchFilter       = "";
        private string _searchFilterLower  = "";

        // ── Runtime service (play mode only) ────────────────────────────────
        private PackageService _packageService;
        private Dictionary<string, PackageState> _runtimeStates = new Dictionary<string, PackageState>();
        private PackageCloudStatus _cloudStatus;

        // ── Styles ───────────────────────────────────────────────────────────
        private GUIStyle _titleStyle;
        private GUIStyle _sectionLabelStyle;
        private GUIStyle _cardStyle;
        private GUIStyle _cardSelectedStyle;
        private GUIStyle _successStyle;
        private GUIStyle _warningStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _mutedStyle;
        private bool _stylesInitialized;

        // ── Layout ───────────────────────────────────────────────────────────
        private const int  LeftPanelWidth = 210;
        private const int  ColumnGap      = 8;
        private float      _rightPanelWidth;

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (Application.isPlaying && _packageService == null)
            {
                var sub = RuntimeManager.GetSubsystem<PackageSubsystem>();
                if (sub?.PackageService != null)
                {
                    _packageService = sub.PackageService;
                    _cloudStatus    = _packageService.CloudStatus;
                    _packageService.OnCloudStatusChanged += OnCloudStatusChanged;
                    RefreshRuntimeStates();
                }
            }
            else if (!Application.isPlaying && _packageService != null)
            {
                _packageService.OnCloudStatusChanged -= OnCloudStatusChanged;
                _packageService = null;
                _runtimeStates.Clear();
                _cloudStatus = null;
                Repaint();
            }
        }

        private void RefreshRuntimeStates()
        {
            if (_packageService == null) return;
            _runtimeStates.Clear();
            foreach (var s in _packageService.GetInstalledPackages())
                _runtimeStates[s.packageId] = s;
            // Also pull any non-installed states for packages in the config list.
            var settings = target as ContentPackageSettings;
            foreach (var cfg in settings.packageConfigs)
            {
                if (!_runtimeStates.ContainsKey(cfg.packageId))
                {
                    var state = _packageService.GetPackageState(cfg.packageId);
                    if (state != null) _runtimeStates[cfg.packageId] = state;
                }
            }
            Repaint();
        }

        private void OnCloudStatusChanged(PackageCloudStatus status)
        {
            _cloudStatus = status;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            EnsureStyles();
            serializedObject.Update();

            var settings = target as ContentPackageSettings;

            // Top bar
            EditorGUILayout.Space(4);
            DrawTopBar();
            EditorGUILayout.Space(6);

            // Two-column layout.
            // The right panel must be width-constrained: when this editor is hosted inside the Hub's
            // InspectorElement (an IMGUIContainer), an unbounded BeginVertical grows to its content's
            // intrinsic width and overflows the panel. currentViewWidth is identical across the Layout
            // and Repaint passes, so deriving the width from it keeps both passes in sync.
            const float rightPanelPadding = 14f; // column gap + detail-body inset
            _rightPanelWidth = Mathf.Max(220f,
                EditorGUIUtility.currentViewWidth - LeftPanelWidth - ColumnGap - rightPanelPadding);

            EditorGUILayout.BeginHorizontal();
            DrawLeftPanel(settings);
            GUILayout.Space(6);
            DrawRightPanel(settings);
            EditorGUILayout.EndHorizontal();

            // Collapsible panels at the bottom
            EditorGUILayout.Space(10);
            DrawSettingsPanel();
            EditorGUILayout.Space(4);
            DrawBuildPanel();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawTopBar()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Content Package Manager", _titleStyle);
            GUILayout.FlexibleSpace();

            if (_packageService != null)
            {
                var prevColor = GUI.color;
                GUI.color = MolcaEditorColors.StatusOk;
                EditorGUILayout.LabelField("● Live", EditorStyles.miniLabel, GUILayout.Width(38));
                GUI.color = prevColor;

                DrawCloudStatusBadge();

                if (GUILayout.Button("↻", GUILayout.Width(24), GUILayout.Height(16)))
                    RefreshRuntimeStates();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Cloud status badge (top bar) ─────────────────────────────────────

        private void DrawCloudStatusBadge()
        {
            var (dot, label, color) = GetCloudBadgeInfo();
            var prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField($"{dot} {label}", EditorStyles.miniLabel, GUILayout.Width(90));
            GUI.color = prev;
        }

        private (string dot, string label, Color color) GetCloudBadgeInfo()
        {
            var state = _cloudStatus?.State ?? CloudConnectionState.Unknown;
            return state switch
            {
                CloudConnectionState.Connected     => ("●", "CDN Connected",    MolcaEditorColors.StatusOk),
                CloudConnectionState.Unreachable   => ("●", "CDN Unreachable",  MolcaEditorColors.StatusError),
                CloudConnectionState.NotConfigured => ("●", "CDN Not Set",      MolcaEditorColors.StatusWarn),
                _                                  => ("●", "CDN Unknown",      MolcaEditorColors.StatusIdle),
            };
        }

        // ── Style init ───────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                padding  = new RectOffset(0, 0, 2, 2)
            };
            _sectionLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11
            };
            _cardStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 5, 5),
                margin  = new RectOffset(0, 0, 1, 2)
            };
            _cardSelectedStyle = new GUIStyle(_cardStyle);

            _successStyle = new GUIStyle(EditorStyles.miniLabel);
            _successStyle.normal.textColor = MolcaEditorColors.StatusOk;

            _warningStyle = new GUIStyle(EditorStyles.miniLabel);
            _warningStyle.normal.textColor = MolcaEditorColors.StatusWarn;

            _errorStyle = new GUIStyle(EditorStyles.miniLabel);
            _errorStyle.normal.textColor = MolcaEditorColors.StatusError;

            _mutedStyle = new GUIStyle(EditorStyles.miniLabel);
            _mutedStyle.normal.textColor = MolcaEditorColors.Muted;
        }
    }
}
