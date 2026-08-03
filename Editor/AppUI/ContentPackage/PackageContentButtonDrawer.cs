using UnityEngine;
using UnityEditor;

namespace Molca.App.UI.Editor
{
    [CustomEditor(typeof(Molca.App.UI.ContentPackage.PackageContentButton))]
    [CanEditMultipleObjects]
    public class PackageContentButtonDrawer : ColorIDButtonDrawer
    {
        private SerializedProperty _packageProp;
        private SerializedProperty _progressRootProp;
        private SerializedProperty _progressSliderProp;
        private SerializedProperty _progressLabelProp;
        private SerializedProperty _statusLabelProp;

        private SerializedProperty _onPackageReadyProp;
        private SerializedProperty _onDownloadStartedProp;
        private SerializedProperty _onInstallCompletedProp;
        private SerializedProperty _onInstallFailedProp;

        private bool _packageEventsFoldout;

        protected override void OnEnable()
        {
            base.OnEnable();

            _packageProp         = serializedObject.FindProperty("_package");
            _progressRootProp    = serializedObject.FindProperty("_progressRoot");
            _progressSliderProp  = serializedObject.FindProperty("_progressSlider");
            _progressLabelProp   = serializedObject.FindProperty("_progressLabel");
            _statusLabelProp     = serializedObject.FindProperty("_statusLabel");

            _onPackageReadyProp      = serializedObject.FindProperty("onPackageReady");
            _onDownloadStartedProp   = serializedObject.FindProperty("onDownloadStarted");
            _onInstallCompletedProp  = serializedObject.FindProperty("onInstallCompleted");
            _onInstallFailedProp     = serializedObject.FindProperty("onInstallFailed");
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Content Package", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_packageProp, new GUIContent("Package"));

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Progress UI", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_progressRootProp,   new GUIContent("Progress Root"));
            EditorGUILayout.PropertyField(_progressSliderProp, new GUIContent("Progress Slider"));
            EditorGUILayout.PropertyField(_progressLabelProp,  new GUIContent("Progress Label"));
            EditorGUILayout.PropertyField(_statusLabelProp,    new GUIContent("Status Label"));

            EditorGUILayout.Space(2);
            _packageEventsFoldout = EditorGUILayout.Foldout(_packageEventsFoldout, "Package Events");
            if (_packageEventsFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_onPackageReadyProp,     new GUIContent("On Package Ready"));
                EditorGUILayout.PropertyField(_onDownloadStartedProp,  new GUIContent("On Download Started"));
                EditorGUILayout.PropertyField(_onInstallCompletedProp, new GUIContent("On Install Completed"));
                EditorGUILayout.PropertyField(_onInstallFailedProp,    new GUIContent("On Install Failed"));
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
