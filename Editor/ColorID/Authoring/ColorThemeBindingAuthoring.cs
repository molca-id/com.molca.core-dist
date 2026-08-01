#if UNITY_EDITOR
using System.Collections.Generic;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Creates and repoints <see cref="ColorThemeBinding"/> components from authoring tools.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Authoring/</c>.
    /// <b>Shape:</b> editor-only static service. Used by the UI token resolver and the Themes workspace.
    /// <para/>
    /// <b>Target discovery is an authoring step, not a runtime one.</b> That is the central difference from
    /// V1: <c>ColorID</c> rediscovered its targets by walking the hierarchy on every refresh, and kept a
    /// parallel cache list that could fall out of step with the configuration list — which is how one
    /// removed component shifted every later target's colour onto the wrong object. Here the components
    /// are resolved once, here, and each binding carries its own target reference forever after.
    /// <para/>
    /// Discovery asks <see cref="ColorTargetAdapterRegistry"/> which components it can actually write,
    /// rather than hard-coding a type list. A project that registered its own adapter therefore gets its
    /// component types discovered too, with no change here.
    /// </remarks>
    public static class ColorThemeBindingAuthoring
    {
        /// <summary>
        /// Finds the components on a GameObject that a colour adapter can write.
        /// </summary>
        /// <param name="target">The object to inspect.</param>
        /// <returns>The writable components, in <c>GetComponents</c> order. Empty when none.</returns>
        /// <remarks>
        /// Self only — no descendants. A style applier says "this object is this colour", and silently
        /// recolouring children would make one authored token change objects the author was not looking at.
        /// A tool that wants descendants calls this per object.
        /// </remarks>
        public static List<Component> DiscoverColorTargets(GameObject target)
        {
            var found = new List<Component>();
            if (target == null) return found;

            foreach (var component in target.GetComponents<Component>())
            {
                // A missing script deserializes as a null component; skipping it here keeps a broken
                // prefab from producing a binding with no target.
                if (component == null) continue;
                if (component is ColorThemeBinding) continue;

                if (ColorTargetAdapterRegistry.TryGetAdapter(component, ColorChannels.Color, out _))
                    found.Add(component);
            }

            return found;
        }

        /// <summary>
        /// Points a GameObject's <see cref="ColorThemeBinding"/> at one canonical token, replacing whatever
        /// it had.
        /// </summary>
        /// <param name="target">The object to style.</param>
        /// <param name="token">The canonical token to bind.</param>
        /// <param name="error">Why nothing was written, when the result is 0.</param>
        /// <param name="undoName">Undo group label.</param>
        /// <returns>How many bindings were written.</returns>
        /// <remarks>
        /// Replaces rather than appends: applying a catalog token twice must leave one binding per target,
        /// not two fighting over the same component in registration order.
        /// <para/>
        /// The component is added when absent, and every mutation is registered with <see cref="Undo"/>, so
        /// a mis-applied token is one Ctrl+Z away. Nothing is written when no component on the object can
        /// take a colour — an empty binding list would look applied and render nothing.
        /// </remarks>
        public static int ApplyToken(GameObject target, ColorTokenReference token, out string error,
            string undoName = "Apply Colour Token")
        {
            error = null;

            if (target == null)
            {
                error = "No target GameObject.";
                return 0;
            }

            if (!token.IsAssigned)
            {
                error = "The token reference is unassigned.";
                return 0;
            }

            var targets = DiscoverColorTargets(target);
            if (targets.Count == 0)
            {
                error = $"'{target.name}' has no component a colour adapter can write. Add an Image, "
                        + "TMP text, SpriteRenderer or another supported target first.";
                return 0;
            }

            var binding = target.GetComponent<ColorThemeBinding>();
            if (binding == null) binding = Undo.AddComponent<ColorThemeBinding>(target);
            else Undo.RecordObject(binding, undoName);

            if (binding == null)
            {
                error = $"Could not add a ColorThemeBinding to '{target.name}'.";
                return 0;
            }

            binding.ClearBindings();
            foreach (var component in targets)
            {
                binding.AddBinding(new ColorBinding(token, component));
            }

            // Applies immediately when a theme is live (in-Editor play, or a preview), and is a harmless
            // no-op otherwise — the component reapplies on Start regardless.
            binding.RefreshBindings();
            EditorUtility.SetDirty(binding);
            return targets.Count;
        }

        /// <summary>
        /// Reports which components on a GameObject a colour adapter can write, for an inspector hint.
        /// </summary>
        /// <param name="target">The object to inspect.</param>
        /// <returns>A comma-separated list of type names, or <c>null</c> when none are writable.</returns>
        public static string DescribeColorTargets(GameObject target)
        {
            var targets = DiscoverColorTargets(target);
            if (targets.Count == 0) return null;

            var names = new List<string>(targets.Count);
            foreach (var component in targets) names.Add(component.GetType().Name);
            return string.Join(", ", names);
        }
    }
}
#endif
