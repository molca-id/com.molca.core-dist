using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.App.Utilities
{
    public class RectTransformHelper : MonoBehaviour
    {
        private RectTransform _rect;

        private void Awake()
        {
            _rect = transform as RectTransform;
        }

        public void SetAnchoredPosition(Vector2 pos)
        {
            _rect.anchoredPosition = pos;
        }

        public void SetAnchorMax(Vector2 anchor)
        {
            _rect.anchorMax = anchor;
        }

        public void SetAnchorMin(Vector2 anchor)
        {
            _rect.anchorMin = anchor;
        }

        public void SetAnchorTop(float anchor)
        {
            _rect.anchorMax = new Vector2(_rect.anchorMax.x, anchor);
        }

        public void SetAnchorRight(float anchor)
        {
            _rect.anchorMax = new Vector2(anchor, _rect.anchorMax.y);
        }

        public void SetAnchorBottom(float anchor)
        {
            _rect.anchorMin = new Vector2(_rect.anchorMin.x, anchor);
        }

        public void SetAnchorLeft(float anchor)
        {
            _rect.anchorMin = new Vector2(anchor, _rect.anchorMax.y);
        }
    }
}