using System.Collections.Generic;

namespace Molca.Logging
{
    /// <summary>
    /// A bounded in-memory ring of recent log entries.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/Sinks/</c>.
    /// <b>Registration:</b> created and registered by <see cref="MolcaLogPipeline.Install"/>; always present.
    /// <para/>
    /// Two jobs. It is the <b>pre-bootstrap buffer</b> — capture starts at subsystem registration but the
    /// file sink cannot exist until <see cref="LogManager"/> has a writable directory, so everything logged
    /// in between lands here and is drained forward. It is also the <b>recent-history window</b> an
    /// on-screen console or the Hub reads, which is why it keeps running rather than being discarded once
    /// the drain is done.
    /// <para/>
    /// A ring rather than a growing list: the whole point is that a project which never installs
    /// <see cref="LogManager"/>, or one that logs heavily for hours, cannot turn this into a leak. Oldest
    /// entries are overwritten, and <see cref="DroppedCount"/> says how many.
    /// </remarks>
    public sealed class MemoryLogSink : ILogSink
    {
        private readonly MolcaLogEntry[] _entries;
        private readonly object _gate = new object();
        private int _next;
        private int _count;
        private int _dropped;

        /// <inheritdoc/>
        public string Name => "memory";

        /// <inheritdoc/>
        /// <remarks>
        /// Settable, and <see cref="MolcaLogLevel.Verbose"/> by default. The buffer is the last resort for
        /// "what happened just before this went wrong", so it stays maximally permissive unless a project
        /// deliberately narrows it.
        /// </remarks>
        public MolcaLogLevel MinimumLevel { get; set; } = MolcaLogLevel.Verbose;

        /// <summary>How many entries the ring holds.</summary>
        public int Capacity => _entries.Length;

        /// <summary>How many entries are currently retained.</summary>
        public int Count
        {
            get { lock (_gate) return _count; }
        }

        /// <summary>How many entries were overwritten because the ring was full.</summary>
        public int DroppedCount
        {
            get { lock (_gate) return _dropped; }
        }

        /// <summary>Creates a ring.</summary>
        /// <param name="capacity">Maximum retained entries; clamped to at least one.</param>
        public MemoryLogSink(int capacity)
        {
            _entries = new MolcaLogEntry[capacity < 1 ? 1 : capacity];
        }

        /// <inheritdoc/>
        public void Write(in MolcaLogEntry entry)
        {
            lock (_gate)
            {
                if (_count == _entries.Length) _dropped++;
                _entries[_next] = entry;
                _next = (_next + 1) % _entries.Length;
                if (_count < _entries.Length) _count++;
            }
        }

        /// <inheritdoc/>
        /// <remarks>Nothing to do: the ring <i>is</i> the destination.</remarks>
        public void Flush() { }

        /// <summary>
        /// Copies the retained entries out, oldest first.
        /// </summary>
        /// <returns>A fresh array; safe for the caller to keep and iterate.</returns>
        /// <remarks>
        /// A copy, not a view. Entries arrive from any thread, so handing out the backing array would let
        /// a reader see a half-written slot.
        /// </remarks>
        public MolcaLogEntry[] Snapshot()
        {
            lock (_gate)
            {
                var copy = new MolcaLogEntry[_count];
                int start = _count == _entries.Length ? _next : 0;
                for (int i = 0; i < _count; i++)
                {
                    copy[i] = _entries[(start + i) % _entries.Length];
                }
                return copy;
            }
        }

        /// <summary>
        /// Copies the retained entries into <paramref name="destination"/> and empties the ring.
        /// </summary>
        /// <param name="destination">Receives the entries, oldest first.</param>
        /// <returns>How many entries were moved.</returns>
        /// <remarks>
        /// How bootstrap logs reach the file: <see cref="LogManager"/> drains into the file sink the moment
        /// it has one. Draining clears, so a second drain cannot write the same lines again — the previous
        /// implementation's buffer had exactly that duplication hazard on shutdown.
        /// </remarks>
        public int DrainTo(ICollection<MolcaLogEntry> destination)
        {
            if (destination == null) return 0;

            lock (_gate)
            {
                int start = _count == _entries.Length ? _next : 0;
                int moved = _count;

                for (int i = 0; i < moved; i++)
                {
                    destination.Add(_entries[(start + i) % _entries.Length]);
                }

                System.Array.Clear(_entries, 0, _entries.Length);
                _next = 0;
                _count = 0;
                return moved;
            }
        }

        /// <summary>Empties the ring.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                System.Array.Clear(_entries, 0, _entries.Length);
                _next = 0;
                _count = 0;
                _dropped = 0;
            }
        }

        /// <inheritdoc/>
        public void Dispose() => Clear();
    }
}
