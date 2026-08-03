using UnityEngine;

namespace Molca.App.UI
{
    /// <summary>
    /// Makes UI elements always face the camera and maintain consistent size regardless of distance
    /// </summary>
    public class BillboardUI : MonoBehaviour
    {
        [Header("Billboard Settings")]
        [SerializeField] private bool faceCamera = true;
        [SerializeField] private bool maintainSize = true;
        [SerializeField] private float baseDistance = 5f;
        [SerializeField] private Vector3 baseScale = Vector3.one;
        
        private Camera mainCamera;
        private Vector3 originalScale;
        
        private void Start()
        {
            mainCamera = Camera.main;
            originalScale = transform.localScale;
            
            if (mainCamera == null)
            {
                mainCamera = FindFirstObjectByType<Camera>();
            }
        }
        
        private void Update()
        {
            if (mainCamera == null) return;
            
            if (faceCamera)
            {
                // Make UI face camera
                transform.LookAt(mainCamera.transform);
                transform.Rotate(0, 180, 0); // Flip to face camera
            }
            
            if (maintainSize)
            {
                // Calculate distance to camera
                float distance = Vector3.Distance(transform.position, mainCamera.transform.position);
                
                // Scale based on distance to maintain apparent size
                float scaleFactor = distance / baseDistance;
                transform.localScale = baseScale * scaleFactor;
            }
        }
        
        /// <summary>
        /// Set the base distance for size calculation
        /// </summary>
        public void SetBaseDistance(float distance)
        {
            baseDistance = distance;
        }
        
        /// <summary>
        /// Set the base scale for size calculation
        /// </summary>
        public void SetBaseScale(Vector3 scale)
        {
            baseScale = scale;
        }
        
        /// <summary>
        /// Enable or disable camera facing
        /// </summary>
        public void SetFaceCamera(bool enabled)
        {
            faceCamera = enabled;
        }
        
        /// <summary>
        /// Enable or disable size maintenance
        /// </summary>
        public void SetMaintainSize(bool enabled)
        {
            maintainSize = enabled;
            if (!enabled)
            {
                transform.localScale = originalScale;
            }
        }
    }
} 