using UnityEngine;
using TMPro;
using Molca.Attributes;
using Molca.ReferenceSystem;

namespace Molca.Localization
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-localization.png")]
    [CreateAssetMenu(fileName = "New Text Style", menuName = "Molca/Localization/Text Style", order = 40)]
    public class LocalizedTextStyleInfo : ScriptableObject, IReferenceable
    {
        [SerializeField, ReadOnly] private string styleId;
        
        [Header("Font Settings")]
        [SerializeField] private TMP_FontAsset font;
        [SerializeField] private FontStyles fontStyle;
        
        [Header("Size Settings")]
        [SerializeField] private float minSize = 12f;
        [SerializeField] private float maxSize = 72f;
        [SerializeField] private float preferredSize = 24f;

        public string StyleId => styleId;
        public TMP_FontAsset Font => font;
        public FontStyles Style => fontStyle;
        public float MinSize => minSize;
        public float MaxSize => maxSize;
        public float PreferredSize => preferredSize;

        // IReferenceable implementation
        public string RefId { get => styleId; set => styleId = value; }
        public string RefType => "LocalizedTextStyle";
        public string DisplayName => string.IsNullOrEmpty(styleId) ? name : styleId;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if(string.IsNullOrEmpty(styleId))
            {
                styleId = ReferenceGenerator.GenerateUniqueId(RefType);
            }

            foreach (var lt in FindObjectsByType<LocalizedText>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                lt.ApplyStyle();
        }
#endif
    }
}