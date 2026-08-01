using System;

namespace Molca.Logging
{
    /// <summary>
    /// A sink that hands each admitted entry to a delegate.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/Sinks/</c>.
    /// <b>Registration:</b> <see cref="MolcaLogPipeline.AddSink"/>.
    /// <para/>
    /// For the common case — "call me when something is logged" — without writing a class. It is also why
    /// the pipeline exposes no <c>event</c> of its own: an event would be a second extension mechanism
    /// with no threshold and no name, and every consumer would then have to re-implement the filtering
    /// this already does.
    /// <para/>
    /// The callback runs on the logging thread, inherits <see cref="ILogSink"/>'s contract — do not block,
    /// do not log — and a callback that throws is isolated and counted by the pipeline rather than
    /// propagating into the <c>Debug.Log</c> call site.
    /// </remarks>
    public sealed class ActionLogSink : ILogSink
    {
        private readonly Action<MolcaLogEntry> _callback;

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public MolcaLogLevel MinimumLevel { get; set; }

        /// <summary>Creates a delegate sink.</summary>
        /// <param name="name">Short identifier for diagnostics.</param>
        /// <param name="callback">Invoked for each admitted entry.</param>
        /// <param name="minimumLevel">Lowest level to deliver.</param>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <c>null</c>.</exception>
        public ActionLogSink(string name, Action<MolcaLogEntry> callback,
            MolcaLogLevel minimumLevel = MolcaLogLevel.Verbose)
        {
            Name = string.IsNullOrEmpty(name) ? "action" : name;
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            MinimumLevel = minimumLevel;
        }

        /// <inheritdoc/>
        public void Write(in MolcaLogEntry entry) => _callback(entry);

        /// <inheritdoc/>
        /// <remarks>Nothing is buffered; the callback already ran.</remarks>
        public void Flush() { }

        /// <inheritdoc/>
        /// <remarks>
        /// Deliberately does not unregister itself. The pipeline holds the registration, so the owner
        /// calls <see cref="MolcaLogPipeline.RemoveSink"/> — having disposal reach back into the
        /// pipeline's list would mutate it from inside a dispatch.
        /// </remarks>
        public void Dispose() { }
    }
}
