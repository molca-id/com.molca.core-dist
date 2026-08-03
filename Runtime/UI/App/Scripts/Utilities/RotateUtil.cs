using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.Utilities
{
    public class RotateUtil : MonoBehaviour
    {
        public Vector3 rotateValue;
        public float updateInterval;

        public UnityEvent onUpdate;

        private float _lastUpdate;

        // Update is called once per frame
        void Update()
        {
            if (Time.time - _lastUpdate < updateInterval)
                return;

            transform.Rotate(rotateValue);
            _lastUpdate = Time.time;
            onUpdate?.Invoke();
        }
    }
}