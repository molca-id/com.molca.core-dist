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

        /// <summary>
        /// Untracks this modal when it is destroyed WITHOUT going through
        /// <see cref="Close"/> (scene unload, direct <c>Destroy</c>) so the
        /// <see cref="ModalManager"/> panel doesn't stay wedged open on a
        /// fake-null entry. Subclasses overriding OnDestroy must call
        /// <c>base.OnDestroy()</c>.
        /// </summary>
        protected virtual void OnDestroy()
        {
            RuntimeManager.GetSubsystem<ModalManager>()?.CloseModal(this);
        }
    }
}
