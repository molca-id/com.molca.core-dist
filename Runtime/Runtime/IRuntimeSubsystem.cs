using Molca;
using System;

public interface IRuntimeSubsystem
{
    int InitializationPriority { get; }
    bool IsActive { get; }

    /// <summary>
    /// Called by <see cref="RuntimeManager"/> during bootstrap. Always invoke
    /// <paramref name="finishCallback"/> when initialization is complete; bootstrap blocks until it is called.
    /// </summary>
    void Initialize(Action<IRuntimeSubsystem> finishCallback);

    /// <summary>
    /// Final teardown. Called by <see cref="RuntimeManager"/> on shutdown, in reverse init order.
    /// Unregister listeners and release resources here.
    /// </summary>
    void Shutdown();

    /// <summary>
    /// Releases resources and unregisters listeners. Called by <see cref="RuntimeSubsystem.Shutdown"/>.
    /// Override instead of overriding <see cref="Shutdown"/> when cleanup is all that is needed.
    /// </summary>
    void Teardown();
}
