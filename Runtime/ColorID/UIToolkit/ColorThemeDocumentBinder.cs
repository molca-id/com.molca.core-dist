using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.ColorID
{
    /// <summary>
    /// Keeps a runtime <see cref="UIDocument"/> on the active theme variant by swapping in the
    /// generated stylesheet for that variant.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/UIToolkit/</c>.
    /// <b>Base class:</b> <see cref="MonoBehaviour"/>.
    /// <b>Registration:</b> added to a GameObject with a <see cref="UIDocument"/>; resolves
    /// <see cref="IColorThemeService"/> after bootstrap.
    /// <para/>
    /// This is what closes the gap the revamp identified as fragmentation: uGUI switched with
    /// <c>ColorSchemeManager</c> while runtime UI Toolkit rendered a hardcoded dark theme with an
    /// unrelated accent and never switched at all. Both surfaces now follow the same active variant,
    /// resolved from the same theme set.
    /// <para/>
    /// It adds and removes only <i>generated variant sheets</i>. Unity's default control theme and any
    /// structural TSS stay assigned through <c>PanelSettings</c> — this component owns the palette, not
    /// the control styling.
    /// </remarks>
    [AddComponentMenu("Molca/Utilities/Color Theme Document Binder")]
    [RequireComponent(typeof(UIDocument))]
    public class ColorThemeDocumentBinder : MonoBehaviour
    {
        [SerializeField, Tooltip("Generated theme output. Produced by Molca ▸ ColorID ▸ Generate "
                                 + "UI Toolkit Theme Output.")]
        private ColorThemeManifest _manifest;

        [SerializeField, Tooltip("Warn when the generated output is missing or stale relative to the "
                                 + "theme set.")]
        private bool _reportProblems = true;

        private UIDocument _document;
        private IColorThemeService _themeService;

        // The sheet this component added, so it can remove exactly that one and never a sheet somebody
        // else assigned.
        private StyleSheet _appliedStylesheet;
        private VisualElement _appliedRoot;

        /// <summary>The generated output this binder reads.</summary>
        public ColorThemeManifest Manifest => _manifest;

        /// <summary>The stylesheet currently applied by this binder, or <c>null</c>.</summary>
        public StyleSheet AppliedStylesheet => _appliedStylesheet;

        // async void is permitted only as a Unity entry point, and only as a thin shim.
        private async void Start()
        {
            try
            {
                _document = GetComponent<UIDocument>();

                await RuntimeManager.WaitForInitialization();
                if (this == null) return;

                _themeService = RuntimeManager.GetService<IColorThemeService>();
                if (_themeService == null)
                {
                    Report("No IColorThemeService is available; this project has not installed a "
                           + "ColorThemeSettings module, so no variant stylesheet can be applied.");
                    return;
                }

                _themeService.ThemeChanged += OnThemeChanged;
                ApplyActiveVariant();
            }
            catch (OperationCanceledException)
            {
                // Quit during bootstrap. Cancellation is not an error.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void OnDestroy()
        {
            if (_themeService != null) _themeService.ThemeChanged -= OnThemeChanged;
            RemoveAppliedStylesheet();
        }

        private void OnEnable()
        {
            // A UIDocument rebuilds its root when re-enabled, dropping any sheet added to the old one,
            // so the sheet has to be re-added rather than assumed still present.
            if (_themeService != null) ApplyActiveVariant();
        }

        private void OnDisable() => RemoveAppliedStylesheet();

        private void OnThemeChanged(ColorThemeChanged change) => Apply(change.Theme);

        /// <summary>Applies the stylesheet for the currently active variant.</summary>
        public void ApplyActiveVariant() => Apply(_themeService?.ActiveTheme);

        /// <summary>
        /// Applies the generated stylesheet for a snapshot's variant.
        /// </summary>
        /// <param name="theme">The snapshot whose variant should be shown.</param>
        /// <returns><c>true</c> when a stylesheet is applied afterwards.</returns>
        public bool Apply(ResolvedColorTheme theme)
        {
            if (theme == null) return false;

            if (_manifest == null)
            {
                Report("No ColorThemeManifest is assigned, so there is no generated stylesheet to "
                       + "apply. Generate the UI Toolkit theme output and assign it here.");
                return false;
            }

            var root = _document != null ? _document.rootVisualElement : null;
            if (root == null)
            {
                // Normal during the frame a document is being (re)built; the OnEnable path retries.
                return false;
            }

            var stylesheet = _manifest.GetStylesheet(theme.VariantId);
            if (stylesheet == null)
            {
                Report($"the generated output contains no stylesheet for variant "
                       + $"'{theme.VariantId}'. Regenerate the theme output.");
                return false;
            }

            if (!_manifest.IsFresh(theme, ColorThemeUssGeneratorVersion.Current, out string staleReason))
            {
                // Applied anyway: stale colours are better than none, and the build gate is where
                // staleness is meant to be fatal. Reporting it here is what makes it visible in play
                // mode rather than only at build time.
                Report($"generated theme output is stale — {staleReason}");
            }

            // Remove before add so switching variants cannot leave both sheets on the root, where the
            // later-added one would win by cascade order rather than by intent.
            RemoveAppliedStylesheet();

            root.AddToClassList(ColorThemeUssNaming.ThemeClass);
            root.styleSheets.Add(stylesheet);
            _appliedStylesheet = stylesheet;
            _appliedRoot = root;
            return true;
        }

        private void RemoveAppliedStylesheet()
        {
            if (_appliedStylesheet == null) return;

            // The recorded root, not the current one: a rebuilt document has a different root, and
            // removing from the new one would be a no-op that silently leaks the old sheet.
            if (_appliedRoot != null && _appliedRoot.styleSheets.Contains(_appliedStylesheet))
                _appliedRoot.styleSheets.Remove(_appliedStylesheet);

            _appliedStylesheet = null;
            _appliedRoot = null;
        }

        private void Report(string message)
        {
            if (_reportProblems) Debug.LogWarning($"[ColorThemeDocumentBinder] '{name}': {message}", this);
        }
    }

    /// <summary>The current UI Toolkit theme generator version.</summary>
    /// <remarks>
    /// Lives in the runtime assembly because both the editor generator (which writes it) and the
    /// runtime binder (which checks it) need the same number, and the binder cannot reference editor
    /// code. Bump it when the generated USS shape changes, to invalidate output that is otherwise
    /// fingerprint-identical.
    /// </remarks>
    public static class ColorThemeUssGeneratorVersion
    {
        /// <summary>The current generator version.</summary>
        public const int Current = 1;
    }
}
