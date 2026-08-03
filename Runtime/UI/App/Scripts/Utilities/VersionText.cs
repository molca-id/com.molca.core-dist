using TMPro;
using UnityEngine;

namespace Molca.App.Utilities
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class VersionText : MonoBehaviour
    {
        [Header("Format Settings")]
        [Tooltip("Format string. Available placeholders:\n{version} - Application version\n{projectName} - Project name\n{companyName} - Company name\n{platform} - Current platform\n{buildGuid} - Build GUID\n{unityVersion} - Unity version")]
        [TextArea(2, 4)]
        [SerializeField] private string formatString = "version. {version}";
        
        [Header("Display Options")]
        [SerializeField] private bool includeProjectName = false;
        [SerializeField] private bool includeCompanyName = false;
        [SerializeField] private bool includePlatform = false;
        [SerializeField] private bool includeUnityVersion = false;
        [SerializeField] private bool includeBuildGuid = false;
        
        [Header("Custom Values")]
        [SerializeField] private string customProjectName = "";
        [SerializeField] private string customCompanyName = "";

        private TextMeshProUGUI textComponent;

        void Start()
        {
            textComponent = GetComponent<TextMeshProUGUI>();
            UpdateText();
        }

        private void UpdateText()
        {
            string formattedText = formatString;
            
            // Replace version
            formattedText = formattedText.Replace("{version}", Application.version);
            
            // Replace project name
            string projectName = !string.IsNullOrEmpty(customProjectName) 
                ? customProjectName 
                : Application.productName;
            formattedText = formattedText.Replace("{projectName}", projectName);
            
            // Replace company name
            string companyName = !string.IsNullOrEmpty(customCompanyName) 
                ? customCompanyName 
                : Application.companyName;
            formattedText = formattedText.Replace("{companyName}", companyName);
            
            // Replace platform
            formattedText = formattedText.Replace("{platform}", Application.platform.ToString());
            
            // Replace Unity version
            formattedText = formattedText.Replace("{unityVersion}", Application.unityVersion);
            
            // Replace build GUID
            formattedText = formattedText.Replace("{buildGuid}", Application.buildGUID);
            
            // Apply display options if they're enabled
            if (includeProjectName && !formatString.Contains("{projectName}"))
            {
                formattedText = $"{projectName} - {formattedText}";
            }
            
            if (includeCompanyName && !formatString.Contains("{companyName}"))
            {
                formattedText = $"{companyName} - {formattedText}";
            }
            
            if (includePlatform && !formatString.Contains("{platform}"))
            {
                formattedText = $"{formattedText} ({Application.platform})";
            }
            
            if (includeUnityVersion && !formatString.Contains("{unityVersion}"))
            {
                formattedText = $"{formattedText} [Unity {Application.unityVersion}]";
            }
            
            if (includeBuildGuid && !formatString.Contains("{buildGuid}"))
            {
                formattedText = $"{formattedText}\nBuild: {Application.buildGUID}";
            }
            
            textComponent.SetText(formattedText);
        }
        
        /// <summary>
        /// Manually update the text with a custom format string
        /// </summary>
        public void SetCustomFormat(string format)
        {
            formatString = format;
            UpdateText();
        }
        
        /// <summary>
        /// Force update the text
        /// </summary>
        public void ForceUpdate()
        {
            UpdateText();
        }
    }
}
