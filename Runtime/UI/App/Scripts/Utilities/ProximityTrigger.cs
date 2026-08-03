using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.Utilities
{
    /// <summary>
    /// A utility component that triggers events when objects enter or exit a specified proximity range.
    /// Can work with both single targets and multiple targets.
    /// </summary>
    public class ProximityTrigger : MonoBehaviour
    {
        [Header("Proximity Settings")]
        [SerializeField] private float proximityRadius = 2f;
        [SerializeField] private LayerMask targetLayers = -1;
        [SerializeField] private bool useTagFilter = false;
        [SerializeField] private string targetTag = "Player";
        
        [Header("Target Options")]
        [SerializeField] private bool useSpecificTarget = false;
        [SerializeField] private Transform specificTarget;
        
        [Header("Events")]
        public UnityEvent<GameObject> onEnterProximity;
        public UnityEvent<GameObject> onExitProximity;
        public UnityEvent<GameObject> onStayInProximity;
        
        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;
        [SerializeField] private Color gizmoColor = Color.yellow;
        
        private Collider[] nearbyColliders = new Collider[10];
        private bool isInProximity = false;
        private GameObject currentTarget;
        
        /// <summary>
        /// Gets or sets the proximity radius
        /// </summary>
        public float ProximityRadius
        {
            get => proximityRadius;
            set => proximityRadius = Mathf.Max(0f, value);
        }
        
        /// <summary>
        /// Gets whether the trigger is currently detecting a target in proximity
        /// </summary>
        public bool IsInProximity => isInProximity;
        
        /// <summary>
        /// Gets the current target in proximity (if any)
        /// </summary>
        public GameObject CurrentTarget => currentTarget;

        private void OnDisable()
        {
            if(currentTarget != null)
            {
                TriggerExitProximity(currentTarget);
            }
            isInProximity = false;
            currentTarget = null;
        }
        
        private void Update()
        {
            CheckProximity();
        }
        
        private void CheckProximity()
        {
            GameObject detectedTarget = null;
            
            if (useSpecificTarget && specificTarget != null)
            {
                // Check specific target
                float distance = Vector3.Distance(transform.position, specificTarget.position);
                if (distance <= proximityRadius)
                {
                    detectedTarget = specificTarget.gameObject;
                }
            }
            else
            {
                // Check for targets in radius
                int hitCount = Physics.OverlapSphereNonAlloc(transform.position, proximityRadius, nearbyColliders, targetLayers);
                
                for (int i = 0; i < hitCount; i++)
                {
                    GameObject obj = nearbyColliders[i].gameObject;
                    
                    // Skip self
                    if (obj == gameObject) continue;
                    
                    // Check tag filter if enabled
                    if (useTagFilter && !obj.CompareTag(targetTag)) continue;
                    
                    detectedTarget = obj;
                    break; // Use first valid target found
                }
            }
            
            // Handle proximity state changes
            if (detectedTarget != null && !isInProximity)
            {
                // Entered proximity
                isInProximity = true;
                currentTarget = detectedTarget;
                onEnterProximity?.Invoke(detectedTarget);
            }
            else if (detectedTarget == null && isInProximity)
            {
                // Exited proximity
                onExitProximity?.Invoke(currentTarget);
                isInProximity = false;
                currentTarget = null;
            }
            else if (detectedTarget != null && isInProximity)
            {
                // Staying in proximity
                onStayInProximity?.Invoke(detectedTarget);
            }
        }
        
        /// <summary>
        /// Manually trigger the enter proximity event
        /// </summary>
        /// <param name="target">The target object</param>
        public void TriggerEnterProximity(GameObject target)
        {
            onEnterProximity?.Invoke(target);
        }
        
        /// <summary>
        /// Manually trigger the exit proximity event
        /// </summary>
        /// <param name="target">The target object</param>
        public void TriggerExitProximity(GameObject target)
        {
            Debug.Log($"TriggerExitProximity called for {target.name}");
            onExitProximity?.Invoke(target);
        }
        
        /// <summary>
        /// Set a specific target to track
        /// </summary>
        /// <param name="target">The target transform to track</param>
        public void SetSpecificTarget(Transform target)
        {
            specificTarget = target;
            useSpecificTarget = target != null;
        }
        
        /// <summary>
        /// Clear the specific target and return to radius-based detection
        /// </summary>
        public void ClearSpecificTarget()
        {
            specificTarget = null;
            useSpecificTarget = false;
        }
        
        private void OnDrawGizmosSelected()
        {
            if (!showDebugGizmos) return;
            
            Gizmos.color = gizmoColor;
            Gizmos.DrawWireSphere(transform.position, proximityRadius);
            
            // Draw line to current target if in proximity
            if (isInProximity && currentTarget != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, currentTarget.transform.position);
            }
        }
        
        private void OnDrawGizmos()
        {
            if (!showDebugGizmos) return;
            
            // Draw a subtle gizmo even when not selected
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.3f);
            Gizmos.DrawWireSphere(transform.position, proximityRadius);
        }
    }
} 