#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// A one- or two-field modal prompt for the Themes workspace's structural actions.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Hub/</c>.
    /// <b>Shape:</b> a small <see cref="EditorWindow"/> shown modally.
    /// <para/>
    /// Deliberately dumb: it collects strings and hands them back. Every rule about what those strings may
    /// be — whether a token ID is well formed, whether a rename target already exists, whether the pair is
    /// already mapped — belongs to the transaction planner, which reports it in a preview the author reads
    /// before anything is applied. Validating here would duplicate those rules into a second place that
    /// could disagree with the first, and would refuse input the planner might have accepted.
    /// <para/>
    /// The one thing it does enforce is non-empty, because an empty field is a slip rather than an
    /// intention and the resulting plan error would be less clear than the disabled button.
    /// </remarks>
    internal sealed class ColorThemeTokenPrompt : EditorWindow
    {
        private string _title = "";
        private string _firstLabel = "";
        private string _secondLabel;
        private string _first = "";
        private string _second = "";
        private Action<string, string> _onAccept;

        /// <summary>Shows the prompt.</summary>
        /// <param name="title">Window title and confirm-button context.</param>
        /// <param name="firstLabel">Label for the first field.</param>
        /// <param name="secondLabel">Label for the second field, or <c>null</c> for a single field.</param>
        /// <param name="onAccept">Invoked with the entered values when the author confirms.</param>
        internal static void Show(string title, string firstLabel, string secondLabel,
            Action<string, string> onAccept)
        {
            var window = CreateInstance<ColorThemeTokenPrompt>();
            window.titleContent = new GUIContent(title);
            window._title = title;
            window._firstLabel = firstLabel;
            window._secondLabel = secondLabel;
            window._onAccept = onAccept;
            window.minSize = new Vector2(420, secondLabel == null ? 96 : 120);
            window.ShowModalUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(_title, EditorStyles.boldLabel);

            _first = EditorGUILayout.TextField(_firstLabel, _first);
            if (_secondLabel != null) _second = EditorGUILayout.TextField(_secondLabel, _second);

            bool complete = !string.IsNullOrWhiteSpace(_first)
                            && (_secondLabel == null || !string.IsNullOrWhiteSpace(_second));

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Cancel", GUILayout.Width(80))) Close();

                using (new EditorGUI.DisabledScope(!complete))
                {
                    if (GUILayout.Button("Continue", GUILayout.Width(90)))
                    {
                        // Captured and the window closed before invoking: the callback opens its own
                        // preview dialog, and running that while this modal is still up stacks two modals.
                        var accept = _onAccept;
                        string first = _first.Trim();
                        string second = _second?.Trim();
                        Close();
                        accept?.Invoke(first, second);
                    }
                }
            }

            EditorGUILayout.HelpBox(
                "Nothing is written yet. The next step previews the exact changes and asks again.",
                MessageType.Info);
        }
    }
}
#endif
