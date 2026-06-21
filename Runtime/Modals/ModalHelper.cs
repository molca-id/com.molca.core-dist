using UnityEngine;

namespace Molca.Modals
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-utils.png")]
    [CreateAssetMenu(fileName = "ModalHelper", menuName = "Molca/UI/Modal Helper", order = 70)]
    public class ModalHelper : ScriptableObject
    {
        /// <summary>Shows a modal registered under <paramref name="modalName"/>.</summary>
        public void ShowModal(string modalName)
        {
            RuntimeManager.GetSubsystem<ModalManager>()?.ShowModal(modalName);
        }

        /// <summary>Shows a specific modal prefab instance.</summary>
        public void ShowModal(BaseModal modal)
        {
            RuntimeManager.GetSubsystem<ModalManager>()?.ShowModal(modal);
        }
    }
}
