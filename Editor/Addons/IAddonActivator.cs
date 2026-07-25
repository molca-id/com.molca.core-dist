using UnityEditor;

namespace Molca.Editor.Addons
{
    /// <summary>Activation seam between verified package acquisition and Unity script discovery.</summary>
    internal interface IAddonActivator
    {
        /// <summary>Activates the package now present beneath the project Packages directory.</summary>
        void Activate();
    }

    /// <summary>Unity 6 activation strategy: refresh assets and let Unity perform its normal domain reload.</summary>
    internal sealed class DomainReloadAddonActivator : IAddonActivator
    {
        // Deferred to a clean editor tick via delayCall rather than refreshing inline. Install/remove run
        // inside an async Awaitable continuation; triggering the script recompile + domain reload from there
        // tears down the in-flight async state machine mid-execution. That is harmless on a first install
        // (a brand-new Packages/<id> dir isn't picked up until Unity re-resolves packages on focus, so the
        // reload is naturally deferred anyway) but wedges on an update, where the package path already exists
        // and Refresh fires the reload immediately from within the continuation — observed as a hung
        // "Importing assets" that only clears on force-quit. delayCall lets the flow fully unwind first, then
        // reloads from a clean editor callback.
        public void Activate() => EditorApplication.delayCall += () => AssetDatabase.Refresh();
    }
}
