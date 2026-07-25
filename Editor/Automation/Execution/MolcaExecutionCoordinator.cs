using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>Whether a lease holds no lock, the shared read lock, or the exclusive write lock.</summary>
    public enum MolcaLeaseMode
    {
        /// <summary>No Unity-state lock held (e.g. external-network-only command).</summary>
        None,

        /// <summary>Shared read lock — overlaps other readers, excluded by a writer.</summary>
        Read,

        /// <summary>Exclusive write lock — excludes all readers and other writers.</summary>
        Write
    }

    /// <summary>
    /// Serializes mutating command runs while letting read-only runs overlap (§13, §14). Realized as a
    /// fair async reader/writer gate over Unity project state: any declared mutating resource claim takes
    /// the exclusive write lock (so builds, target switches, AssetDatabase writes, scene edits, Undo, Play
    /// Mode transitions, and manifest changes never overlap each other or a read), and
    /// <see cref="MolcaResourceClaim.ProjectRead"/> takes the shared read lock. Claims that touch neither
    /// Unity state (external network) take no lock and run fully concurrently.
    /// </summary>
    /// <remarks>
    /// The Editor's async model is cooperative single-threaded — continuations resume on the main thread —
    /// so the gate needs no OS locks; it only has to order <c>await</c> resumes. A single lock domain plus
    /// FIFO queueing makes it deadlock-free (no lock ordering to get wrong) and starvation-free (a queued
    /// writer blocks readers behind it). Main thread only. Persisting run state across domain reloads is a
    /// Phase 3 concern; this coordinator is in-memory.
    /// </remarks>
    public sealed class MolcaExecutionCoordinator
    {
        private sealed class Waiter
        {
            public readonly bool IsWriter;
            public readonly AwaitableCompletionSource Source = new AwaitableCompletionSource();
            public State Status = State.Waiting;
            public enum State { Waiting, Granted, Cancelled }

            public Waiter(bool isWriter) => IsWriter = isWriter;

            public void Grant()
            {
                if (Status != State.Waiting) return;
                Status = State.Granted;
                Source.TrySetResult();
            }

            public void Cancel()
            {
                if (Status != State.Waiting) return;
                Status = State.Cancelled;
                Source.TrySetCanceled();
            }
        }

        private readonly Queue<Waiter> _queue = new Queue<Waiter>();
        private int _activeReaders;
        private bool _activeWriter;

        /// <summary>Number of read leases currently held.</summary>
        public int ActiveReaders => _activeReaders;

        /// <summary>Whether a write lease is currently held.</summary>
        public bool HasActiveWriter => _activeWriter;

        /// <summary>
        /// Acquires the lease appropriate for <paramref name="claims"/>, waiting (in FIFO order) until it
        /// can be granted. Dispose the returned lease to release. Throws
        /// <see cref="System.OperationCanceledException"/> if cancelled while waiting.
        /// </summary>
        /// <param name="claims">The command's declared resource claims.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>An acquired <see cref="MolcaResourceLease"/>.</returns>
        public async Awaitable<MolcaResourceLease> AcquireAsync(
            IReadOnlyList<MolcaResourceClaim> claims, CancellationToken cancellationToken)
        {
            var mode = Classify(claims);
            if (mode == MolcaLeaseMode.None)
                return new MolcaResourceLease(this, MolcaLeaseMode.None);

            cancellationToken.ThrowIfCancellationRequested();

            var waiter = new Waiter(mode == MolcaLeaseMode.Write);
            _queue.Enqueue(waiter);
            Pump();

            using (cancellationToken.Register(waiter.Cancel))
                await waiter.Source.Awaitable; // completes on grant; throws OperationCanceledException on cancel

            return new MolcaResourceLease(this, mode);
        }

        /// <summary>
        /// Attempts to acquire immediately without queueing; returns null if the lease cannot be granted
        /// right now (the caller may report a structured <c>blocked</c> result instead of waiting).
        /// </summary>
        /// <param name="claims">The command's declared resource claims.</param>
        /// <returns>An acquired lease, or null if contended.</returns>
        public MolcaResourceLease TryAcquire(IReadOnlyList<MolcaResourceClaim> claims)
        {
            var mode = Classify(claims);
            if (mode == MolcaLeaseMode.None)
                return new MolcaResourceLease(this, MolcaLeaseMode.None);
            if (_queue.Count > 0 || !CanGrant(mode == MolcaLeaseMode.Write))
                return null;

            if (mode == MolcaLeaseMode.Write) _activeWriter = true; else _activeReaders++;
            return new MolcaResourceLease(this, mode);
        }

        /// <summary>Releases a granted lease and dispatches any waiters that can now proceed.</summary>
        /// <param name="mode">The lease mode being released.</param>
        internal void Release(MolcaLeaseMode mode)
        {
            switch (mode)
            {
                case MolcaLeaseMode.Read: if (_activeReaders > 0) _activeReaders--; break;
                case MolcaLeaseMode.Write: _activeWriter = false; break;
            }
            Pump();
        }

        private void Pump()
        {
            while (_queue.Count > 0)
            {
                var w = _queue.Peek();
                if (w.Status == Waiter.State.Cancelled) { _queue.Dequeue(); continue; }

                if (w.IsWriter)
                {
                    if (_activeReaders == 0 && !_activeWriter)
                    {
                        _queue.Dequeue();
                        _activeWriter = true;
                        w.Grant();
                    }
                    // A waiting writer blocks everyone behind it (prevents writer starvation).
                    break;
                }

                if (_activeWriter) break; // readers wait behind an active writer
                _queue.Dequeue();
                _activeReaders++;
                w.Grant();
            }
        }

        private bool CanGrant(bool isWriter) =>
            isWriter ? (_activeReaders == 0 && !_activeWriter) : !_activeWriter;

        /// <summary>Classifies a claim set into the lock it requires.</summary>
        /// <param name="claims">The claims to classify.</param>
        /// <returns>Write if any mutating claim is present; Read if only <see cref="MolcaResourceClaim.ProjectRead"/>; None otherwise.</returns>
        public static MolcaLeaseMode Classify(IReadOnlyList<MolcaResourceClaim> claims)
        {
            if (claims == null || claims.Count == 0) return MolcaLeaseMode.None;
            if (claims.Any(MolcaCommandDefinition.IsMutatingClaim)) return MolcaLeaseMode.Write;
            if (claims.Contains(MolcaResourceClaim.ProjectRead)) return MolcaLeaseMode.Read;
            return MolcaLeaseMode.None;
        }
    }
}
