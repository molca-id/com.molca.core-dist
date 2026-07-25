using UnityEditor;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The single decision on whether the editor's current play state satisfies a command's required
    /// <see cref="MolcaCommandMode"/> (§13). Shared by the executor (which turns a failure into a refusal)
    /// and the plan preview (which reports it without running), so both read the gate from one place.
    /// </summary>
    public static class MolcaModeGate
    {
        /// <summary>Checks whether <paramref name="mode"/> is satisfied by the editor's current play state.</summary>
        /// <param name="mode">The mode the command requires.</param>
        /// <returns>
        /// <c>ok</c> true when the mode is satisfied; otherwise a stable <c>code</c> and human
        /// <c>message</c> describing the mismatch (both null when ok).
        /// </returns>
        public static (bool ok, string code, string message) Check(MolcaCommandMode mode)
        {
            var playing = EditorApplication.isPlayingOrWillChangePlaymode;
            if (mode == MolcaCommandMode.Edit && playing)
                return (false, "mode.edit_required", "This command requires Edit mode; the editor is in Play mode.");
            if (mode == MolcaCommandMode.Play && !playing)
                return (false, "mode.play_required", "This command requires Play mode; the editor is in Edit mode.");
            return (true, null, null);
        }
    }
}
