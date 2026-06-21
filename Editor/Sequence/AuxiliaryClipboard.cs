using System;
using Molca.Sequence.Auxiliary;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// Single editor-session clipboard for <see cref="StepAuxiliary"/> values, shared by
    /// <see cref="StepEditor"/> and <see cref="AuxiliaryBatchPanel"/> so copy in one surface
    /// can be pasted in the other.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="EditorJsonUtility"/> for serialization, which deep-copies the managed
    /// fields while preserving <c>UnityEngine.Object</c> references (by instance id) within
    /// the session — the same round-trip <see cref="StepEditingService"/> uses to clone
    /// auxiliaries. This retires the old reflection-based field/Unity-object buffer.
    /// Paste is only permitted into an auxiliary of the exact copied type.
    /// </remarks>
    public static class AuxiliaryClipboard
    {
        private static string _json;

        /// <summary>The concrete type of the copied auxiliary, or <c>null</c> when empty.</summary>
        public static Type CopiedType { get; private set; }

        /// <summary>Whether a value has been copied this session.</summary>
        public static bool HasData => CopiedType != null && _json != null;

        /// <summary>Returns whether the clipboard holds a value compatible with <paramref name="target"/>.</summary>
        public static bool CanPasteInto(StepAuxiliary target) =>
            HasData && target != null && target.GetType() == CopiedType;

        /// <summary>Returns whether the clipboard holds a value of the given type.</summary>
        public static bool CanPasteType(Type type) => HasData && type == CopiedType;

        /// <summary>Copies the values of <paramref name="auxiliary"/> into the clipboard.</summary>
        public static void Copy(StepAuxiliary auxiliary)
        {
            if (auxiliary == null) return;
            CopiedType = auxiliary.GetType();
            _json = EditorJsonUtility.ToJson(auxiliary);
        }

        /// <summary>
        /// Overwrites <paramref name="target"/> with the clipboard values. The caller is
        /// responsible for undo recording and dirtying the owning object.
        /// </summary>
        /// <returns><c>true</c> if the paste was applied; <c>false</c> on a type mismatch or empty clipboard.</returns>
        public static bool Paste(StepAuxiliary target)
        {
            if (!CanPasteInto(target)) return false;
            EditorJsonUtility.FromJsonOverwrite(_json, target);
            return true;
        }
    }
}
