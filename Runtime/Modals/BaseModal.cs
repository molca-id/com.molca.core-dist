using UnityEngine;

namespace Molca.Modals
{
    public abstract class BaseModal : MonoBehaviour
    {
        /// <summary>Makes this modal visible. Override to add open animations.</summary>
        public virtual void Open(bool showNoButton = true)
        {
            gameObject.SetActive(true);
        }

        /// <summary>
        /// Notifies <see cref="ModalManager"/> to untrack this modal, then destroys the GameObject.
        /// Override to add close animations; always call <c>base.Close()</c> at the end.
        /// </summary>
        public virtual void Close()
        {
            RuntimeManager.GetSubsystem<ModalManager>()?.CloseModal(this);
            Destroy(gameObject);
        }

        /// <summary>Controls visibility of the cancel/no button. Override in derived classes as needed.</summary>
        public virtual void SetNoButtonVisible(bool visible) { }
    }
}
