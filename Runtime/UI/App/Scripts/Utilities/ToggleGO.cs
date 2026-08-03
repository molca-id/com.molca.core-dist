using UnityEngine;

namespace Molca.App.Utilities
{
    public class ToggleGO : MonoBehaviour
    {
        [SerializeField, TextArea]
        private string note;
        [SerializeField]
        private GameObject[] active;
        [SerializeField]
        private GameObject[] inactive;

        public void Invoke()
        {
            foreach (GameObject go in active)
                if(go) go.SetActive(true);

            foreach (GameObject go in inactive)
                if(go) go.SetActive(false);
        }
    }
}