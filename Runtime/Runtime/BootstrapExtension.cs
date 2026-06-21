using UnityEngine;

namespace Molca
{
    /// <summary>
    /// Base class for bootstrap-time extension points.
    /// Add concrete <see cref="BootstrapExtension"/> assets to
    /// <see cref="MolcaProjectSettings"/> to participate in the bootstrap pipeline
    /// before any <see cref="RuntimeSubsystem"/> initializes.
    /// </summary>
    /// <remarks>
    /// SDK layers (e.g., SDK VR, SDK DT) use this to inject layer-specific bootstrap
    /// configuration without subclassing <see cref="MolcaProjectSettings"/> and without
    /// triggering an uncontrolled second Addressables load before
    /// <see cref="RuntimeManager"/> is alive.
    /// <para>
    /// Lifecycle: <see cref="OnBootstrap"/> is invoked by <see cref="RuntimeManager"/>
    /// after the RuntimeManager prefab is instantiated and before
    /// <see cref="GlobalSettings.Initialize"/> runs. Extensions are processed in the
    /// order they appear in the <see cref="MolcaProjectSettings.BootstrapExtensions"/> list.
    /// </para>
    /// <para>
    /// The hook is asynchronous — subclasses can await Addressables loads, network
    /// calls, or other long-running work. The bootstrap pipeline awaits each
    /// extension before advancing.
    /// </para>
    /// </remarks>
    public abstract class BootstrapExtension : ScriptableObject
    {
        /// <summary>
        /// Called once during application bootstrap, before subsystems are initialized.
        /// </summary>
        /// <param name="projectSettings">The loaded project settings instance, for context.</param>
        /// <returns>An <see cref="Awaitable"/> that completes when the extension is done.</returns>
        public abstract Awaitable OnBootstrap(MolcaProjectSettings projectSettings);
    }
}
