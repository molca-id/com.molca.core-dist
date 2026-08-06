using System;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Hub field rows for settings whose value is <b>per machine</b>: the control shows the effective value,
    /// editing it writes <see cref="MolcaLocalSettings"/> instead of the committed asset, and an overridden
    /// field is marked and offers a reset back to the project default.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Registration: static UI factory.
    /// <para>
    /// These rows deliberately do <b>not</b> bind to a <c>SerializedProperty</c>, which is what the ordinary
    /// bound rows do. A bound control writes the asset — the exact behavior being avoided here, since that is
    /// what turned one developer's choice of model or port into a repository diff. The caller supplies the
    /// effective value and a write action that goes through the settings property, so the machine/project split
    /// stays decided in one place: <see cref="MolcaLocalSettings.Keys"/>.
    /// </para>
    /// <para>
    /// Reset clears the override rather than writing the default value back. The overlay is sparse, so an
    /// absent entry keeps tracking the project default as that default evolves, while an entry that happens to
    /// equal today's default would silently pin the old value.
    /// </para>
    /// </remarks>
    public static class MolcaLocalOverrideRow
    {
        /// <summary>A machine-local toggle row.</summary>
        /// <param name="label">Field label.</param>
        /// <param name="key">The overlay key from <see cref="MolcaLocalSettings.Keys"/>.</param>
        /// <param name="projectDefault">The committed project default, shown on the reset affordance.</param>
        /// <param name="effective">The value in force now (override if set, else the project default).</param>
        /// <param name="write">Writes the new value — wire this to the settings property setter.</param>
        /// <param name="afterChange">Optional side effect to run after a change or a reset (e.g. restart).</param>
        public static VisualElement Bool(string label, string key, bool projectDefault, bool effective,
                                         Action<bool> write, Action afterChange = null)
        {
            var control = new Toggle { value = effective };
            return Compose(label, key, control, projectDefault ? "on" : "off",
                onEdit: () => write(control.value),
                onReset: () => control.SetValueWithoutNotify(projectDefault),
                subscribe: refresh => control.RegisterValueChangedCallback(_ => refresh()),
                afterChange: afterChange);
        }

        /// <summary>A machine-local integer row. Commits on blur or Enter, not on every keystroke.</summary>
        /// <param name="label">Field label.</param>
        /// <param name="key">The overlay key from <see cref="MolcaLocalSettings.Keys"/>.</param>
        /// <param name="projectDefault">The committed project default, shown on the reset affordance.</param>
        /// <param name="effective">The value in force now (override if set, else the project default).</param>
        /// <param name="write">Writes the new value — wire this to the settings property setter.</param>
        /// <param name="afterChange">Optional side effect to run after a change or a reset (e.g. restart).</param>
        public static VisualElement Int(string label, string key, int projectDefault, int effective,
                                        Action<int> write, Action afterChange = null)
        {
            // isDelayed: each keystroke would otherwise be a separate override write, and — for the MCP port —
            // a separate listener restart on a half-typed number.
            var control = new IntegerField { value = effective, isDelayed = true };
            return Compose(label, key, control, projectDefault.ToString(),
                onEdit: () => write(control.value),
                onReset: () => control.SetValueWithoutNotify(projectDefault),
                subscribe: refresh => control.RegisterValueChangedCallback(_ => refresh()),
                afterChange: afterChange);
        }

        /// <summary>A machine-local text row. Commits on blur or Enter, not on every keystroke.</summary>
        /// <param name="label">Field label.</param>
        /// <param name="key">The overlay key from <see cref="MolcaLocalSettings.Keys"/>.</param>
        /// <param name="projectDefault">The committed project default, shown on the reset affordance.</param>
        /// <param name="effective">The value in force now (override if set, else the project default).</param>
        /// <param name="write">Writes the new value — wire this to the settings property setter.</param>
        /// <param name="afterChange">Optional side effect to run after a change or a reset (e.g. rebuild).</param>
        public static VisualElement String(string label, string key, string projectDefault, string effective,
                                           Action<string> write, Action afterChange = null)
        {
            var control = new TextField { value = effective ?? string.Empty, isDelayed = true };
            return Compose(label, key, control,
                string.IsNullOrEmpty(projectDefault) ? "empty" : projectDefault,
                onEdit: () => write(control.value),
                onReset: () => control.SetValueWithoutNotify(projectDefault ?? string.Empty),
                subscribe: refresh => control.RegisterValueChangedCallback(_ => refresh()),
                afterChange: afterChange);
        }

        /// <summary>A machine-local enum row.</summary>
        /// <typeparam name="T">The enum type.</typeparam>
        /// <param name="label">Field label.</param>
        /// <param name="key">The overlay key from <see cref="MolcaLocalSettings.Keys"/>.</param>
        /// <param name="projectDefault">The committed project default, shown on the reset affordance.</param>
        /// <param name="effective">The value in force now (override if set, else the project default).</param>
        /// <param name="write">Writes the new value — wire this to the settings property setter.</param>
        /// <param name="afterChange">Optional side effect to run after a change or a reset (e.g. rebuild).</param>
        public static VisualElement Enum<T>(string label, string key, T projectDefault, T effective,
                                            Action<T> write, Action afterChange = null)
            where T : struct, System.Enum
        {
            var control = new EnumField(effective);
            return Compose(label, key, control, projectDefault.ToString(),
                onEdit: () => write((T)control.value),
                onReset: () => control.SetValueWithoutNotify(projectDefault),
                subscribe: refresh => control.RegisterValueChangedCallback(_ => refresh()),
                afterChange: afterChange);
        }

        /// <summary>
        /// Wraps a control in the shared label + control + reset layout and keeps the override marker in sync.
        /// </summary>
        /// <param name="label">Field label.</param>
        /// <param name="key">The overlay key.</param>
        /// <param name="control">The value control, already seeded with the effective value.</param>
        /// <param name="defaultText">Human-readable project default, for the reset tooltip.</param>
        /// <param name="onEdit">Pushes the control's current value through the settings property.</param>
        /// <param name="onReset">Puts the project default back into the control without re-notifying.</param>
        /// <param name="subscribe">Hooks the control's change event to the supplied refresh action.</param>
        /// <param name="afterChange">Caller side effect, run after both edits and resets.</param>
        private static VisualElement Compose(string label, string key, VisualElement control, string defaultText,
                                            Action onEdit, Action onReset, Action<Action> subscribe,
                                            Action afterChange)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");

            var fieldLabel = new Label(label);
            fieldLabel.AddToClassList("molca-hub-field-label");
            row.Add(fieldLabel);

            control.AddToClassList("molca-hub-field-control");
            row.Add(control);

            var reset = new Button { text = "↺" };
            reset.AddToClassList("molca-hub-mini-button");
            reset.tooltip = $"Local override — click to restore the project default ({defaultText}).";
            row.Add(reset);

            void SyncMarker()
            {
                var overridden = MolcaLocalSettings.Instance.Has(key);
                fieldLabel.style.unityFontStyleAndWeight =
                    overridden ? UnityEngine.FontStyle.Bold : UnityEngine.FontStyle.Normal;
                // Kept in the layout but invisible, so rows don't shift as overrides come and go.
                reset.style.visibility = overridden ? Visibility.Visible : Visibility.Hidden;
            }

            subscribe(() =>
            {
                onEdit();
                SyncMarker();
                afterChange?.Invoke();
            });

            reset.clicked += () =>
            {
                // Clears the entry rather than writing the default — see the type remarks.
                MolcaLocalSettings.Instance.Clear(key);
                onReset();
                SyncMarker();
                afterChange?.Invoke();
            };

            SyncMarker();
            return row;
        }
    }
}
