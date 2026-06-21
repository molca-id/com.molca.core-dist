using UnityEngine;
using TMPro;
using UnityEngine.UI;
using Molca.Utils;

namespace Molca.Modals
{
    public class ModalMessage : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI messageText;
        [SerializeField] private CanvasGroup canvasGroup;

        private static readonly Color StripeColor = new Color(.8f, .8f, .8f);

        // Incremented on every Initialize. A fade loop from a previous pool use
        // notices the bump and exits instead of fighting the new fade for the
        // alpha value and returning the message to the pool early.
        private int _generation;

        /// <summary>
        /// Displays the message, fades it out over <paramref name="duration"/> seconds,
        /// then returns it to <paramref name="pool"/>.
        /// </summary>
        /// <param name="stripe">When true uses plain <paramref name="color"/>; false multiplies by <see cref="StripeColor"/> for alternating row tinting.</param>
        public async void Initialize(string msg, Color color, ObjectPool<ModalMessage> pool, float duration, bool stripe)
        {
            int generation = ++_generation;

            messageText.text  = msg;
            messageText.color = stripe ? color : color * StripeColor;
            canvasGroup.alpha = 1f;

            LayoutRebuilder.ForceRebuildLayoutImmediate(messageText.rectTransform);

            float lifeTime = duration;
            while (lifeTime > 0f)
            {
                if (this == null || generation != _generation) return;

                lifeTime -= Time.deltaTime;
                float t = 1f - (lifeTime / duration);

                // Fade out over the last 5 % of lifetime.
                canvasGroup.alpha = t > 0.95f ? 1f - ((t - 0.95f) / 0.05f) : 1f;

                await Awaitable.NextFrameAsync();
            }

            if (this != null && generation == _generation)
                pool.Return(this);
        }
    }
}
