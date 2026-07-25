using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The undo work a reversible command registers (via
    /// <see cref="MolcaCommandContext.RegisterCompensation"/>) so its effect can be rolled back — either
    /// automatically when the run fails, or later on an explicit revert (§13, Phase 4). Runs on the Unity
    /// main thread; honors its cancellation token.
    /// </summary>
    /// <param name="cancellationToken">Bounds the rollback; independent of the (possibly cancelled) run token.</param>
    /// <returns>An awaitable yielding the rollback outcome.</returns>
    public delegate Awaitable<MolcaRevertOutcome> MolcaCompensation(CancellationToken cancellationToken);

    /// <summary>
    /// The result of running a command's compensation: whether the effect was reversed, the evidence for
    /// that, and a failure reason when it was not. A failed rollback is surfaced as an Error so a caller
    /// never assumes clean state after an incomplete revert (§13).
    /// </summary>
    public sealed class MolcaRevertOutcome
    {
        /// <summary>Whether the rollback completed and the effect is reversed.</summary>
        public bool Succeeded { get; }

        /// <summary>Human-readable evidence lines supporting the outcome. Never null.</summary>
        public IReadOnlyList<string> Evidence { get; }

        /// <summary>Why the rollback did not complete, or null when it succeeded.</summary>
        public string FailureMessage { get; }

        private MolcaRevertOutcome(bool succeeded, IReadOnlyList<string> evidence, string failureMessage)
        {
            Succeeded = succeeded;
            Evidence = evidence ?? Array.Empty<string>();
            FailureMessage = failureMessage;
        }

        /// <summary>A successful rollback with optional evidence.</summary>
        /// <param name="evidence">Evidence lines describing what was reversed.</param>
        /// <returns>A succeeded outcome.</returns>
        public static MolcaRevertOutcome Ok(params string[] evidence) => new MolcaRevertOutcome(true, evidence, null);

        /// <summary>A failed rollback carrying the reason.</summary>
        /// <param name="message">Why the rollback did not complete.</param>
        /// <returns>A failed outcome.</returns>
        public static MolcaRevertOutcome Failed(string message) =>
            new MolcaRevertOutcome(false, Array.Empty<string>(), string.IsNullOrEmpty(message) ? "unknown error" : message);

        /// <summary>Serializes this outcome to its JSON object form.</summary>
        /// <returns>A <see cref="JObject"/> with succeeded/evidence and an optional failure message.</returns>
        public JObject ToJson()
        {
            var o = new JObject
            {
                ["succeeded"] = Succeeded,
                ["evidence"] = new JArray(Evidence)
            };
            if (!string.IsNullOrEmpty(FailureMessage)) o["failureMessage"] = FailureMessage;
            return o;
        }
    }
}
