using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;
using Molca.ContentPackage.Release;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>How content is resolved, and which keys are permitted to sign a release.</summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built by <see cref="ContentWorkspaceView"/> for the <c>protocol</c> node.
    /// <para>
    /// <b>These are the fields the read-only inspector rule exists for.</b> A settings asset living
    /// inside a package is replaced on upgrade, and this page is where that costs the most: a project
    /// would silently revert to whatever trust the package shipped. So every control here is disabled
    /// when the service refuses the asset, not merely accompanied by a warning.
    /// </para>
    /// <para>
    /// Keys are added and removed one at a time through the service's set-the-whole-list operation,
    /// which refuses an incomplete key rather than storing it. An incomplete key does not fail here — it
    /// fails at runtime, as a signature error indistinguishable from tampering.
    /// </para>
    /// </remarks>
    internal sealed class ContentProtocolView : VisualElement
    {
        private readonly ContentWorkspaceContext _context;

        /// <summary>Builds the page.</summary>
        /// <param name="context">The workspace context.</param>
        public ContentProtocolView(ContentWorkspaceContext context)
        {
            _context = context;

            Add(new MolcaWorkspaceHeader("Protocol & keys", "How releases are resolved and trusted"));

            BuildProtocol();
            BuildKeys();
        }

        private void BuildProtocol()
        {
            var settings = _context.Settings;
            var card = ContentWorkspaceUi.Card(
                "Release protocol",
                settings.EnableReleaseProtocol ? "Enabled" : "Disabled",
                settings.EnableReleaseProtocol ? MolcaStatusKind.Ok : MolcaStatusKind.Idle,
                settings.EnableReleaseProtocol ? "Routed" : "Off");

            card.Body.Add(MolcaFields.EditToggle(
                "Enabled",
                settings.EnableReleaseProtocol,
                value => _context.ApplySettingsEdit(_context.Editing.SetReleaseProtocolEnabled(value)),
                "Off by default and deliberately opt-in. Turning it on changes where every byte of " +
                "content comes from, and a project without a service id and a trusted key fails closed."));

            card.Body.Add(MolcaFields.EditText(
                "Content service",
                settings.ContentServiceId,
                value => _context.ApplySettingsEdit(_context.Editing.SetContentServiceId(value)),
                "A network catalog service id, not a URL: the routed pipeline resolves the origin from " +
                "the catalog, so a build token cannot be sent anywhere the project did not authorize.",
                placeholder: "molca-content"));

            card.Body.Add(MolcaFields.EditText(
                "Path prefix",
                settings.ContentPathPrefix,
                value => _context.ApplySettingsEdit(_context.Editing.SetContentPathPrefix(value)),
                placeholder: "/content/v1"));

            if (settings.EnableReleaseProtocol && settings.TrustedReleaseKeys.Count == 0)
            {
                card.Body.Add(ContentWorkspaceUi.Warn(
                    "The protocol is on with no trusted keys, so every release will be refused as untrusted."));
            }

            Disable(card.Body);
            Add(card);
        }

        private void BuildKeys()
        {
            var keys = _context.Settings.TrustedReleaseKeys;
            var card = ContentWorkspaceUi.Card(
                "Trusted release keys",
                $"{keys.Count} key(s)",
                keys.Count == 0 ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                keys.Count == 0 ? "None" : "Provisioned");

            if (!_context.IsReadOnly)
                card.AddHeaderAction(MolcaButtons.Mini("Add key…", AddKey));

            if (keys.Count == 0)
            {
                card.Body.Add(MolcaFields.Note(
                    "No keys. Nothing can verify a release manifest, which is the correct state for a " +
                    "project that has not enabled the protocol and a broken one for a project that has."));
            }

            foreach (var key in keys)
            {
                var row = new MolcaListRow(key.KeyId, key.IsComplete ? null : "incomplete");
                row.AddMetadata(new MolcaStatusBadge(
                    key.IsComplete ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                    key.IsComplete ? "Complete" : "Unusable"));

                row.AddDetail(MolcaFields.ReadOnly("Modulus", Abbreviate(key.ModulusBase64)));
                row.AddDetail(MolcaFields.ReadOnly("Exponent", key.ExponentBase64));

                if (!_context.IsReadOnly)
                {
                    var captured = key.KeyId;
                    row.AddAction(MolcaButtons.Mini("Remove", () => RemoveKey(captured)));
                }

                card.Body.Add(row);
            }

            Add(card);
        }

        /// <summary>
        /// Shows the first and last few characters of a long base64 value.
        /// </summary>
        /// <remarks>
        /// Enough to compare two keys by eye, short enough not to turn the card into a wall. The value is
        /// public key material, so abbreviating is readability rather than secrecy.
        /// </remarks>
        private static string Abbreviate(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= 24 ? value : $"{value.Substring(0, 12)}…{value.Substring(value.Length - 8)}";
        }

        private void AddKey()
        {
            string keyId = MolcaValuePrompt.ForValue(
                "Add trusted key",
                "The key id, as issued by the Molca control plane. A release signed by any key listed " +
                "here verifies; a release signed by anything else is refused.",
                "Key ID", "", "Next",
                candidate => _context.Settings.TrustedReleaseKeys.Any(key =>
                    string.Equals(key.KeyId, candidate?.Trim(), System.StringComparison.Ordinal))
                    ? "That key id is already trusted."
                    : null);
            if (string.IsNullOrWhiteSpace(keyId)) return;

            string modulus = MolcaValuePrompt.ForValue(
                "Add trusted key",
                $"The RSA modulus for '{keyId}', standard base64 — not base64url.",
                "Modulus", "", "Next");
            if (string.IsNullOrWhiteSpace(modulus)) return;

            string exponent = MolcaValuePrompt.ForValue(
                "Add trusted key",
                $"The RSA public exponent for '{keyId}', standard base64. Usually AQAB.",
                "Exponent", "AQAB", "Add");
            if (string.IsNullOrWhiteSpace(exponent)) return;

            var next = new List<ReleaseTrustedKey>(_context.Settings.TrustedReleaseKeys)
            {
                new ReleaseTrustedKey
                {
                    KeyId = keyId.Trim(),
                    ModulusBase64 = modulus.Trim(),
                    ExponentBase64 = exponent.Trim(),
                },
            };

            var result = _context.Editing.SetTrustedReleaseKeys(next);
            if (!result.Changed) EditorUtility.DisplayDialog("Key refused", result.Message, "OK");
            _context.ApplySettingsEdit(result);
        }

        private void RemoveKey(string keyId)
        {
            if (!EditorUtility.DisplayDialog("Remove trusted key",
                    $"Stop trusting '{keyId}'?\n\nReleases signed only by this key stop verifying, " +
                    "including ones already published.",
                    "Remove", "Cancel"))
                return;

            var next = _context.Settings.TrustedReleaseKeys
                .Where(key => !string.Equals(key.KeyId, keyId, System.StringComparison.Ordinal))
                .ToList();

            _context.ApplySettingsEdit(_context.Editing.SetTrustedReleaseKeys(next));
        }

        private void Disable(VisualElement body)
        {
            if (!_context.IsReadOnly) return;
            body.SetEnabled(false);
        }
    }
}
