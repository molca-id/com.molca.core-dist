using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Molca.App.UI
{
    public class UIReference : MonoBehaviour
    {
        [Serializable]
        private struct ObjectReference<T> where T : UnityEngine.Object
        {
            public string key;
            public T value;
        }

        [SerializeField]
        private ObjectReference<TextMeshProUGUI>[] texts;
        [SerializeField]
        private ObjectReference<Image>[] images;
        [SerializeField]
        private ObjectReference<Button>[] buttons;
        [SerializeField]
        private ObjectReference<GameObject>[] objects;

        public TextMeshProUGUI GetText(string key)
        {
            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i].key == key)
                    return texts[i].value;
            }

            Debug.LogError($"No text with key: {key}");
            return null;
        }

        public Image GetImage(string key)
        {
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i].key == key)
                    return images[i].value;
            }

            Debug.LogError($"No image with key: {key}");
            return null;
        }

        public Button GetButton(string key)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].key == key)
                    return buttons[i].value;
            }

            Debug.LogError($"No button with key: {key}");
            return null;
        }

        public GameObject GetObject(string key)
        {
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i].key == key)
                    return objects[i].value;
            }

            Debug.LogError($"No gameobject with key: {key}");
            return null;
        }
    }
}