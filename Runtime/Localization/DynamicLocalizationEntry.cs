using System;

namespace Molca.Localization
{
    [Serializable]
    public class DynamicLocalizationEntry
    {
        public string languageCode;
        public string text;

        public DynamicLocalizationEntry(string languageCode, string text)
        {
            this.languageCode = languageCode;
            this.text = text;
        }
    }
} 