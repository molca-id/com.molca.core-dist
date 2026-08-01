using UnityEngine;

namespace Molca.Logging
{
    /// <summary>
    /// Severity threshold for a log sink, in ascending order.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/</c>.
    /// <para/>
    /// This type exists because <see cref="LogType"/> is unusable as a threshold. Its ordinals are
    /// <c>Error=0, Assert=1, Warning=2, Log=3, Exception=4</c> — neither ascending nor descending in
    /// severity — so a serialized field of that type defaults to <c>0</c>, which reads as "Error" and
    /// means "suppress almost everything". The shipped Runtime Manager prefab was authored under the
    /// natural assumption that <c>0</c> meant verbose, and every warning in the framework was silently
    /// discarded as a result.
    /// <para/>
    /// Here <c>0</c> is <see cref="Verbose"/>: the default serialized value of a new field is the most
    /// permissive one. A logger that fails open loses log volume; a logger that fails closed loses the
    /// diagnostics you need to find out why. Only the first is recoverable.
    /// </remarks>
    public enum MolcaLogLevel
    {
        /// <summary>
        /// Admit everything.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Info"/> even though no <see cref="LogType"/> currently maps below
        /// Info. It reserves the bottom of the range so adding a trace channel later does not have to
        /// renumber the enum — which would silently change the meaning of every serialized asset.
        /// </remarks>
        Verbose = 0,

        /// <summary>Admit informational messages and above (<see cref="LogType.Log"/>).</summary>
        Info = 1,

        /// <summary>Admit warnings and above.</summary>
        Warning = 2,

        /// <summary>Admit only errors, assertions and exceptions.</summary>
        Error = 3,

        /// <summary>Admit nothing. Disables a sink without unregistering it.</summary>
        None = 4
    }

    /// <summary>Maps Unity log types onto <see cref="MolcaLogLevel"/>.</summary>
    public static class MolcaLogLevels
    {
        /// <summary>
        /// The level a Unity log type is filtered at.
        /// </summary>
        /// <param name="logType">The Unity log type.</param>
        /// <returns>The corresponding level; never <see cref="MolcaLogLevel.None"/>.</returns>
        /// <remarks>
        /// <see cref="LogType.Assert"/> and <see cref="LogType.Exception"/> both map to
        /// <see cref="MolcaLogLevel.Error"/>: all three are failures, and a threshold that admitted an
        /// exception but not an assertion would be a distinction no author asked for. This also removes
        /// the old inconsistency where an exception reaching the handler through
        /// <c>LogException</c> bypassed the filter entirely while the same exception arriving through
        /// <c>LogFormat</c> was filtered.
        /// </remarks>
        public static MolcaLogLevel FromUnity(LogType logType)
        {
            switch (logType)
            {
                case LogType.Log: return MolcaLogLevel.Info;
                case LogType.Warning: return MolcaLogLevel.Warning;
                case LogType.Assert:
                case LogType.Error:
                case LogType.Exception: return MolcaLogLevel.Error;
                // A LogType outside the documented set is treated as a failure rather than as noise:
                // silently downgrading something unrecognised is how messages disappear.
                default: return MolcaLogLevel.Error;
            }
        }

        /// <summary>Whether an entry at <paramref name="level"/> passes a <paramref name="threshold"/>.</summary>
        /// <param name="level">The entry's level.</param>
        /// <param name="threshold">The sink's minimum level.</param>
        /// <returns><c>false</c> when the threshold is <see cref="MolcaLogLevel.None"/> or above the level.</returns>
        public static bool Passes(MolcaLogLevel level, MolcaLogLevel threshold) =>
            threshold != MolcaLogLevel.None && level >= threshold;

        /// <summary>Whether a Unity log type passes a threshold.</summary>
        /// <param name="logType">The Unity log type.</param>
        /// <param name="threshold">The sink's minimum level.</param>
        public static bool Passes(LogType logType, MolcaLogLevel threshold) =>
            Passes(FromUnity(logType), threshold);
    }
}
