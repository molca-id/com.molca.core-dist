using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Applies canonical colour tokens to components on this GameObject, and reapplies them whenever
    /// the active theme variant changes.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/Bindings/</c>.
    /// <b>Base class:</b> <see cref="MonoBehaviour"/>.
    /// <b>Registration:</b> added explicitly by authoring or migration. Subscribes to
    /// <see cref="IColorThemeService.ThemeChanged"/> after
    /// <c>RuntimeManager.WaitForInitialization()</c> and unsubscribes on destroy.
    /// <para/>
    /// The V2 counterpart to <see cref="ColorID"/>, and deliberately narrower in scope:
    /// <list type="bullet">
    /// <item><description>
    /// <b>One component, many bindings.</b> Several tokens on several targets are configured here
    /// rather than by stacking components.
    /// </description></item>
    /// <item><description>
    /// <b>No hierarchy scanning at runtime.</b> Each binding names its own target. A theme change
    /// iterates the authored list and nothing else — no <c>GetComponentsInChildren</c>, no scene
    /// search. Target discovery is an explicit authoring command, not something that happens on every
    /// variant switch.
    /// </description></item>
    /// <item><description>
    /// <b>No parallel cache.</b> The index-skew defect in V1 was a consequence of keeping one; there is
    /// nothing here to fall out of step.
    /// </description></item>
    /// <item><description>
    /// <b>Generation-gated.</b> A repeat notification for a generation already applied is ignored, so
    /// duplicate publishes are cheap rather than redundant work across every binding in the scene.
    /// </description></item>
    /// </list>
    /// </remarks>
    [AddComponentMenu("Molca/Utilities/Color Theme Binding")]
    public class ColorThemeBinding : MonoBehaviour
    {
        [SerializeField] private List<ColorBinding> _bindings = new List<ColorBinding>();

        [SerializeField, Tooltip(
             "Log a warning when a binding cannot be applied — an unsupported target, a missing "
             + "shader property, or a token absent from the active variant. Turn off for objects that "
             + "are intentionally themed only in some variants.")]
        private bool _reportProblems = true;

        // Cached so OnDestroy unsubscribes from the same instance even if the service registry is
        // already gone during teardown.
        private IColorThemeService _themeService;

        // Last applied generation. -1 means "nothing applied yet", which is distinct from generation 0.
        private int _appliedGeneration = -1;

        /// <summary>This component's bindings.</summary>
        public IReadOnlyList<ColorBinding> Bindings => _bindings;

        /// <summary>The theme generation currently applied, or -1 before the first application.</summary>
        public int AppliedGeneration => _appliedGeneration;

        // async void is permitted only as a Unity entry point, and only as a thin shim that cannot
        // let an exception escape into Unity's synchronization context unobserved.
        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();

                // If destroyed during the await, OnDestroy has already run — subscribing now would
                // leak a handler on a dead object.
                if (this == null) return;

                _themeService = RuntimeManager.GetService<IColorThemeService>();
                if (_themeService == null)
                {
                    if (_reportProblems)
                    {
                        Debug.LogWarning(
                            "[ColorThemeBinding] No IColorThemeService is available. This project has "
                            + "not installed a ColorThemeSettings module, so canonical colour tokens "
                            + "cannot resolve. Legacy ColorID components are unaffected.", this);
                    }
                    return;
                }

                _themeService.ThemeChanged += OnThemeChanged;

                // Apply the already-published snapshot: this component may have been instantiated
                // after activation, in which case no further notification is coming.
                ApplyActiveTheme();
            }
            catch (OperationCanceledException)
            {
                // Bootstrap torn down (quit during initialization). Cancellation is not an error.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void OnDestroy()
        {
            if (_themeService != null) _themeService.ThemeChanged -= OnThemeChanged;
        }

        private void OnEnable()
        {
            // Re-enabled after a variant changed while disabled: the notification was missed, so the
            // generation gate catches it up here rather than leaving stale colours on screen.
            if (_themeService != null) ApplyActiveTheme();
        }

        private void OnThemeChanged(ColorThemeChanged change) => Apply(change.Theme);

        /// <summary>Applies the currently active snapshot, if there is one.</summary>
        public void ApplyActiveTheme() => Apply(_themeService?.ActiveTheme);

        /// <summary>
        /// Reapplies every binding, ignoring the generation gate.
        /// </summary>
        /// <remarks>
        /// For after a target's component set changed — a target replaced, a colour overwritten by
        /// another system. It does not rediscover targets: adding or removing a binding is authoring.
        /// </remarks>
        public void RefreshBindings()
        {
            _appliedGeneration = -1;
            ApplyActiveTheme();
        }

        /// <summary>
        /// Applies a snapshot to every binding.
        /// </summary>
        /// <param name="theme">The snapshot to apply. Ignored when <c>null</c>.</param>
        /// <returns>The number of bindings that were written.</returns>
        /// <remarks>
        /// Resolution reads from the snapshot passed in, never from the service — a handler that
        /// re-read the service could pick up a newer activation mid-loop and leave the object
        /// half-themed across two variants.
        /// </remarks>
        public int Apply(ResolvedColorTheme theme)
        {
            if (theme == null) return 0;
            if (theme.Generation == _appliedGeneration) return 0;

            bool allowEditModeMaterialWrite = false;
#if UNITY_EDITOR
            // Edit-mode application of the generic material path is opt-in only through preview; see
            // MaterialPropertyAdapter for why a property block written here would not persist.
            allowEditModeMaterialWrite = false;
#endif

            int applied = 0;
            for (int i = 0; i < _bindings.Count; i++)
            {
                if (ApplyBinding(_bindings[i], i, theme, allowEditModeMaterialWrite)) applied++;
            }

            _appliedGeneration = theme.Generation;
            return applied;
        }

        private bool ApplyBinding(ColorBinding binding, int index, ResolvedColorTheme theme,
            bool allowEditModeMaterialWrite)
        {
            if (binding == null) return false;

            if (!binding.Token.IsAssigned)
            {
                // An unassigned binding is an incomplete authoring state, not a failure to report on
                // every theme switch.
                return false;
            }

            var target = binding.TargetComponent;
            if (target == null)
            {
                Report($"binding {index} ('{binding.Token}') has no target component; it may have been "
                       + "removed. Fix or delete the binding.");
                return false;
            }

            if (!binding.Token.TryResolve(theme, out Color resolved))
            {
                Report($"token '{binding.Token}' is not present in active variant "
                       + $"'{theme.VariantId}'. Add it to that variant, or mark the token optional.");
                return false;
            }

            Color finalColor = ColorTargetAdapterRegistry.ApplyAlphaPolicy(resolved,
                binding.AlphaPolicy, target, binding.TargetChannel, binding.CustomAlpha);

            var result = ColorTargetAdapterRegistry.Apply(target, binding.TargetChannel, finalColor,
                binding.CreateContext(allowEditModeMaterialWrite));

            if (result.IsProblem)
            {
                Report($"binding {index} ('{binding.Token}' -> {target.GetType().Name}."
                       + $"{binding.TargetChannel}) failed: {result}");
                return false;
            }

            return result.Applied;
        }

        private void Report(string message)
        {
            if (_reportProblems) Debug.LogWarning($"[ColorThemeBinding] '{name}': {message}", this);
        }

        /// <summary>Repoints the single binding at a different token and reapplies it.</summary>
        /// <param name="tokenId">The canonical token ID to read from now on.</param>
        /// <remarks>
        /// <b>Signature shaped for <see cref="UnityEngine.Events.UnityEvent"/>:</b> one string argument, so
        /// it can be wired straight to a button's toggle or click event from the Inspector. That is the V2
        /// replacement for <c>ColorID.SetColorId</c>, which the same wiring used to call with a
        /// <c>"Swatch/Color"</c> composite; the argument here is a canonical token ID.
        /// <para/>
        /// Only valid when this component has exactly one binding — with several, which one a bare
        /// <c>SetToken("…")</c> meant would be positional and silent, so it logs and does nothing instead.
        /// Use <see cref="SetToken(int, string)"/> there.
        /// </remarks>
        public void SetToken(string tokenId)
        {
            if (_bindings.Count != 1)
            {
                Debug.LogWarning(
                    $"[ColorThemeBinding] '{name}': SetToken(string) needs exactly one binding but this "
                    + $"component has {_bindings.Count}. Use SetToken(index, tokenId) to say which.", this);
                return;
            }

            SetToken(0, tokenId);
        }

        /// <summary>Repoints one binding at a different token and reapplies it.</summary>
        /// <param name="index">The binding's index in <see cref="Bindings"/>.</param>
        /// <param name="tokenId">The canonical token ID to read from now on.</param>
        /// <remarks>
        /// Reapplies immediately, bypassing the generation gate: the theme has not changed, this binding
        /// has, and the gate would otherwise swallow the change as already-applied.
        /// </remarks>
        public void SetToken(int index, string tokenId)
        {
            if (index < 0 || index >= _bindings.Count)
            {
                Debug.LogWarning(
                    $"[ColorThemeBinding] '{name}': binding {index} does not exist "
                    + $"(this component has {_bindings.Count}).", this);
                return;
            }

            var binding = _bindings[index];
            if (binding == null) return;

            binding.Retarget(new ColorTokenReference(tokenId));
            RefreshBindings();
        }

        /// <summary>Adds a binding at runtime or from an authoring tool.</summary>
        /// <param name="binding">The binding to add.</param>
        /// <remarks>
        /// Does not apply it — call <see cref="RefreshBindings"/> afterwards. Kept separate so a tool
        /// adding several bindings applies once rather than once per addition.
        /// </remarks>
        public void AddBinding(ColorBinding binding)
        {
            if (binding == null) return;
            _bindings.Add(binding);
        }

        /// <summary>Removes every binding.</summary>
        /// <returns>How many were removed.</returns>
        /// <remarks>
        /// For an authoring tool replacing this object's styling wholesale — applying a catalog token
        /// re-derives the whole list rather than accumulating a second binding onto the same target.
        /// Resets the generation gate so the next apply is not skipped as already-applied.
        /// </remarks>
        public int ClearBindings()
        {
            int removed = _bindings.Count;
            if (removed == 0) return 0;

            _bindings.Clear();
            _appliedGeneration = -1;
            return removed;
        }

        /// <summary>Removes every binding whose target component is gone.</summary>
        /// <returns>The number removed.</returns>
        /// <remarks>
        /// A maintenance operation for authoring, not something application does automatically: a
        /// binding with a missing target is a reportable authoring problem, and silently deleting it
        /// would hide the fact that a target was lost.
        /// </remarks>
        public int RemoveOrphanedBindings()
        {
            int removed = _bindings.RemoveAll(b => b == null || b.TargetComponent == null);
            if (removed > 0) _appliedGeneration = -1;
            return removed;
        }
    }
}
