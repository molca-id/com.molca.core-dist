using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.App.Utilities
{
    public class RectTransformInterpolate : MonoBehaviour
    {
        public float duration;
        [Header("Position")]
        public Vector2 onPosition;
        public Vector2 offPosition;
        [Header("Rotation")]
        public Vector3 offRotation;
        public Vector3 onRotation;

        private RectTransform _rect;

        private void Awake()
        {
            _rect = transform as RectTransform;
        }

        public void InterpolatePosition(bool value)
        {
            IEnumerator Interpolation(Vector2 target)
            {
                Vector2 from = _rect.anchoredPosition;
                float a = 0f;
                while(a < 1f)
                {
                    a += Time.deltaTime / duration;
                    _rect.anchoredPosition = Vector2.Lerp(from, target, a);
                    yield return new WaitForEndOfFrame();
                }
            }

            Vector2 target = value ? onPosition : offPosition;
            if(_rect.anchoredPosition != target)
                StartCoroutine(Interpolation(target));
        }

        public void InterpolateRotation(bool value)
        {
            IEnumerator Interpolation(Quaternion target)
            {
                Quaternion from = _rect.rotation;
                float a = 0f;
                while (a < 1f)
                {
                    a += Time.deltaTime / duration;
                    _rect.rotation = Quaternion.Lerp(from, target, a);
                    yield return new WaitForEndOfFrame();
                }
            }

            Quaternion target = Quaternion.Euler(value ? onRotation : offRotation);
            if (_rect.rotation != target)
                StartCoroutine(Interpolation(target));
        }
    }
}