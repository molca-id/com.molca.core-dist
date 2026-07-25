using System;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// A held grant from the <see cref="MolcaExecutionCoordinator"/>. Dispose it (ideally via
    /// <c>using</c>) to release the lock so queued runs can proceed. Releasing twice is a no-op.
    /// </summary>
    public sealed class MolcaResourceLease : IDisposable
    {
        private readonly MolcaExecutionCoordinator _coordinator;
        private bool _released;

        /// <summary>The lock this lease holds.</summary>
        public MolcaLeaseMode Mode { get; }

        internal MolcaResourceLease(MolcaExecutionCoordinator coordinator, MolcaLeaseMode mode)
        {
            _coordinator = coordinator;
            Mode = mode;
        }

        /// <summary>Releases the lease back to the coordinator. Idempotent.</summary>
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (Mode != MolcaLeaseMode.None)
                _coordinator.Release(Mode);
        }
    }
}
