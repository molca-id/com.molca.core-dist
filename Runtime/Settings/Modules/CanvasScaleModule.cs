using System;
using UnityEngine;

namespace Molca.Settings
{
    /// <summary>
    /// User-configurable UI scale, expressed as a normalized value in [0, 1] that the
    /// module lerps between authored <see cref="minCanvasScale"/> and
    /// <see cref="maxCanvasScale"/> bounds.
    /// </summary>
    /// <remarks>
    /// Mutable runtime state (the chosen normalized scale) lives on the paired
    /// <see cref="CanvasScaleState"/>. The SerializeFields on this asset are authored
    /// defaults and are never written to at runtime.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Canvas Scale Setting", menuName = "Molca/Settings/Canvas Scale", order = 10)]
    public class CanvasScaleModule : SettingModule
    {
        public float minCanvasScale;
        public float maxCanvasScale;
        public Action<float> onUiScaleChanged;

        private CanvasScaleState TypedState => (CanvasScaleState)State;

        // Edit-time access (before GlobalSettings has initialized) returns 0 — the
        // authored normalized default — rather than throwing. Setters are no-ops.
        private float CurrentNormalized => State != null ? TypedState.UIScaleNormalized : 0f;

        /// <summary>The current UI scale, lerped between <see cref="minCanvasScale"/> and <see cref="maxCanvasScale"/>.</summary>
        public float UIScale => Mathf.Lerp(minCanvasScale, maxCanvasScale, CurrentNormalized);

        /// <summary>The current normalized UI scale in [0, 1]. Setter persists and notifies <see cref="onUiScaleChanged"/>.</summary>
        public float UIScaleNormalized
        {
            get => CurrentNormalized;
            set
            {
                if (State == null)
                {
                    Debug.LogError("[CanvasScaleModule] Cannot set UIScaleNormalized before GlobalSettings has initialized.", this);
                    return;
                }
                TypedState.UIScaleNormalized = value;
                SaveSettings();
                onUiScaleChanged?.Invoke(UIScale);
            }
        }

        public override SettingState CreateState() => new CanvasScaleState();

        public override void SaveSettings()
        {
            if (State != null) TypedState.Save(this);
        }

        public override void LoadSettings()
        {
            if (State == null) return;
            TypedState.Load(this);
            onUiScaleChanged?.Invoke(UIScale);
        }
    }

    /// <summary>Mutable runtime state for <see cref="CanvasScaleModule"/>.</summary>
    public class CanvasScaleState : SettingState
    {
        // Persistence key kept as "_uiScale" for backward compatibility with existing
        // PlayerPrefs entries written by the previous SerializeField-mutating implementation.
        private const string Key = "_uiScale";

        /// <summary>Normalized UI scale in [0, 1].</summary>
        public float UIScaleNormalized;

        public override void Load(SettingModule owner) => UIScaleNormalized = owner.LoadFloat(Key, 0f);
        public override void Save(SettingModule owner) => owner.SaveFloat(Key, UIScaleNormalized);
    }
}
