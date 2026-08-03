using System;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// One canonical colour token bound to one channel on one component.
    /// </summary>
    /// <remarks>
    /// The V2 replacement for <c>ColorID.ColorTarget</c>, and different from it in the way that
    /// mattered: a binding <b>carries its own target reference</b> and is applied through that
    /// reference directly. V1 kept a parallel cache list rebuilt while skipping null components, then
    /// indexed the two lists together — so one removed component shifted every later target's
    /// configuration onto the wrong object. There is no second list here to fall out of step with.
    /// <para/>
    /// Serialized inside <see cref="ColorThemeBinding"/>; no registration of its own.
    /// </remarks>
    [Serializable]
    public class ColorBinding
    {
        [SerializeField] private ColorTokenReference _token;
        [SerializeField] private Component _targetComponent;
        [SerializeField] private string _targetChannel = ColorChannels.Color;
        [SerializeField] private ColorAlphaPolicy _alphaPolicy = ColorAlphaPolicy.UseTokenAlpha;
        [SerializeField, Range(0f, 1f)] private float _customAlpha = 1f;
        [SerializeField] private string _materialProperty;

        /// <summary>The canonical colour token this binding reads.</summary>
        public ColorTokenReference Token => _token;

        /// <summary>The component this binding writes to.</summary>
        public Component TargetComponent => _targetComponent;

        /// <summary>
        /// The channel to write. Blank or <see cref="ColorChannels.Color"/> means the component's
        /// primary colour.
        /// </summary>
        public string TargetChannel => _targetChannel;

        /// <summary>How the written colour's alpha is decided.</summary>
        public ColorAlphaPolicy AlphaPolicy => _alphaPolicy;

        /// <summary>Alpha used when <see cref="AlphaPolicy"/> is <see cref="ColorAlphaPolicy.Explicit"/>.</summary>
        public float CustomAlpha => _customAlpha;

        /// <summary>
        /// Shader colour property for <see cref="ColorChannels.MaterialProperty"/>, for example
        /// <c>_BaseColor</c>. Blank probes <c>_BaseColor</c> then <c>_Color</c>.
        /// </summary>
        public string MaterialProperty => _materialProperty;

        /// <summary>Whether this binding has both a token and a live target.</summary>
        public bool IsComplete => _token.IsAssigned && _targetComponent != null;

        /// <summary>Creates a binding. Intended for authoring tools, migration and tests.</summary>
        /// <param name="token">The canonical token to read.</param>
        /// <param name="target">The component to write.</param>
        /// <param name="channel">The channel; blank for the component's primary colour.</param>
        /// <param name="alphaPolicy">How alpha is decided.</param>
        /// <param name="customAlpha">Alpha for <see cref="ColorAlphaPolicy.Explicit"/>.</param>
        /// <param name="materialProperty">Shader property for the material channel.</param>
        public ColorBinding(ColorTokenReference token, Component target,
            string channel = ColorChannels.Color,
            ColorAlphaPolicy alphaPolicy = ColorAlphaPolicy.UseTokenAlpha,
            float customAlpha = 1f, string materialProperty = null)
        {
            _token = token;
            _targetComponent = target;
            _targetChannel = channel;
            _alphaPolicy = alphaPolicy;
            _customAlpha = customAlpha;
            _materialProperty = materialProperty;
        }

        /// <summary>Repoints this binding at a different canonical token.</summary>
        /// <param name="token">The token to read from now on.</param>
        /// <remarks>
        /// Changes only which token is read; the target, channel and alpha policy are authoring and stay
        /// put. Does not apply anything — <see cref="ColorThemeBinding.SetToken(int, string)"/> owns the
        /// reapply, so a caller repointing several bindings applies once rather than once per change.
        /// </remarks>
        internal void Retarget(ColorTokenReference token) => _token = token;

        /// <summary>Builds the adapter context for this binding.</summary>
        /// <param name="allowEditModeMaterialWrite">Whether edit-mode material writes are allowed.</param>
        internal ColorBindingContext CreateContext(bool allowEditModeMaterialWrite) =>
            new ColorBindingContext(_materialProperty, allowEditModeMaterialWrite, _customAlpha);
    }
}
