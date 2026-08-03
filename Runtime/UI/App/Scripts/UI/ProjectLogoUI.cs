using UnityEngine;
using UnityEngine.UI;
using Molca; // Add this to access MolcaProjectSettings

namespace Molca.App.UI
{
    [RequireComponent(typeof(Image))]
    public class ProjectLogoUI : MonoBehaviour
    {
        void Start()
        {
            var image = GetComponent<Image>();
            if (MolcaProjectSettings.Instance != null && MolcaProjectSettings.Instance.ProjectLogo != null)
            {
                image.sprite = MolcaProjectSettings.Instance.ProjectLogo;
            }
        }
    }
}