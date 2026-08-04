using System;
using Molca.Editor.UI.Components;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// The catalog's modal prompts: <see cref="MolcaValuePrompt"/> plus the id rule this workspace uses.
    /// </summary>
    /// <remarks>
    /// The window itself moved to <see cref="MolcaValuePrompt"/> when the Content workspace needed the
    /// same one. What stays here is the only part that is about a catalog: an id has to satisfy
    /// <see cref="NetworkIds"/>, and that rule belongs beside the catalog rather than in a shared
    /// component that would then know about one domain's identifiers.
    /// </remarks>
    internal static class NetworkHubPrompt
    {
        /// <summary>
        /// Asks for a valid catalog identifier, blocking until the user accepts or cancels.
        /// </summary>
        /// <param name="title">Window title.</param>
        /// <param name="explanation">What this ID is used for and what changing it costs.</param>
        /// <param name="initialValue">The value to start from.</param>
        /// <returns>The accepted identifier, or <c>null</c> when cancelled.</returns>
        internal static string ForId(string title, string explanation, string initialValue) =>
            ForValue(title, explanation, "Identifier", initialValue, "Rename",
                candidate => NetworkIds.IsValid(candidate, out string error) ? null : error);

        /// <summary>
        /// Asks for a value the caller validates, blocking until the user accepts or cancels.
        /// </summary>
        /// <param name="title">Window title.</param>
        /// <param name="explanation">What the value is used for.</param>
        /// <param name="fieldLabel">Label on the input.</param>
        /// <param name="initialValue">The value to start from.</param>
        /// <param name="acceptLabel">Text on the accept button, e.g. <c>Add</c> or <c>Rename</c>.</param>
        /// <param name="validate">
        /// Returns null or empty when the candidate is acceptable, otherwise the reason it is not. Null
        /// accepts anything non-blank.
        /// </param>
        /// <returns>The accepted value, or <c>null</c> when cancelled.</returns>
        internal static string ForValue(
            string title,
            string explanation,
            string fieldLabel,
            string initialValue,
            string acceptLabel,
            Func<string, string> validate = null) =>
            MolcaValuePrompt.ForValue(title, explanation, fieldLabel, initialValue, acceptLabel, validate);
    }
}
