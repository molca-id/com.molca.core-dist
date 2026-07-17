using UnityEngine;

namespace Molca.Sequence.Auxiliary
{
    /// <summary>
    /// Controls transform properties when step events occur.
    /// Attach this to the same GameObject as a Step component for automatic event hooking.
    /// </summary>
    [AuxiliaryMenu("Utility/Transform Controller")]
    public class StepTransformController : StepAuxiliary
    {
        [System.Serializable]
        public class TransformState
        {
            [Tooltip("The target GameObject to control (null = this GameObject).")]
            public GameObject target;
            
            [Tooltip("Target position for the transform.")]
            public Vector3 position;
            
            [Tooltip("Target rotation for the transform (in euler angles).")]
            public Vector3 rotation;
            
            [Tooltip("Target scale for the transform.")]
            public Vector3 scale;
            
            [Tooltip("Whether to modify position.")]
            public bool modifyPosition = true;
            
            [Tooltip("Whether to modify rotation.")]
            public bool modifyRotation = true;
            
            [Tooltip("Whether to modify scale.")]
            public bool modifyScale = true;
            
            [Tooltip("Time to animate to this state (0 = instant).")]
            public float animationTime = 0f;
            
            [Tooltip("Animation curve for smooth transitions.")]
            public AnimationCurve animationCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        }
        
        [Header("Transform Control Settings")]
        [Tooltip("Transform state to apply when the step begins.")]
        public TransformState beginState;
        
        [Tooltip("Transform state to apply when the step ends.")]
        public TransformState endState;
        
        [Tooltip("Transform state to apply when the step is completed.")]
        public TransformState completeState;
        
        [Tooltip("Transform state to apply when the step is reset.")]
        public TransformState resetState;
        
        [Header("Control Options")]
        [Tooltip("If true, will restore original transform when the step ends.")]
        public bool restoreOnEnd = false;
        
        [Tooltip("If true, will also control child transforms.")]
        public bool controlChildren = false;
        
        [Tooltip("If true, will log all transform changes for debugging.")]
        public bool logChanges = false;
        
        private TransformState[] _originalStates;
        private Transform[] _controlledTransforms;
        
        protected override void OnInitialize()
        {
            // StepAuxiliary is a plain serializable class, not a MonoBehaviour —
            // Unity never calls Awake() on it, so collection must happen in the
            // auxiliary init hook driven by Step.Initialize().
            CollectControlledTransforms();
        }
        
        public override void OnStepBegin()
        {
            ApplyTransformState(beginState, "begin");
        }
        
        public override void OnStepEnd()
        {
            if (restoreOnEnd)
            {
                RestoreOriginalStates();
            }
            else
            {
                ApplyTransformState(endState, "end");
            }
        }
        
        public override void OnStepCompleted()
        {
            ApplyTransformState(completeState, "complete");
        }
        
        public override void OnStepReset()
        {
            ApplyTransformState(resetState, "reset");
        }
        
        private void CollectControlledTransforms()
        {
            var transforms = new System.Collections.Generic.List<Transform>();
            
            // Add this GameObject's transform
            transforms.Add(Step.transform);
            
            // Add child transforms if requested
            if (controlChildren)
            {
                transforms.AddRange(Step.GetComponentsInChildren<Transform>());
            }
            
            _controlledTransforms = transforms.ToArray();
            
            // Store original states
            _originalStates = new TransformState[_controlledTransforms.Length];
            for (int i = 0; i < _controlledTransforms.Length; i++)
            {
                _originalStates[i] = new TransformState
                {
                    target = _controlledTransforms[i].gameObject,
                    position = _controlledTransforms[i].localPosition,
                    rotation = _controlledTransforms[i].localEulerAngles,
                    scale = _controlledTransforms[i].localScale,
                    modifyPosition = true,
                    modifyRotation = true,
                    modifyScale = true,
                    animationTime = 0f
                };
            }
        }
        
        private void ApplyTransformState(TransformState state, string eventType)
        {
            if (state == null) return;
            
            Transform targetTransform = state.target != null ? state.target.transform : Step.transform;
            
            if (state.animationTime > 0)
            {
                Step.StartCoroutine(AnimateTransform(targetTransform, state, eventType));
            }
            else
            {
                ApplyTransformInstant(targetTransform, state, eventType);
            }
        }
        
        private void ApplyTransformInstant(Transform targetTransform, TransformState state, string eventType)
        {
            if (state.modifyPosition)
            {
                targetTransform.localPosition = state.position;
            }
            
            if (state.modifyRotation)
            {
                targetTransform.localEulerAngles = state.rotation;
            }
            
            if (state.modifyScale)
            {
                targetTransform.localScale = state.scale;
            }
            
            if (logChanges)
            {
                Debug.Log($"StepTransformController: Applied {eventType} transform state to '{targetTransform.name}'.", Step);
            }
        }
        
        private System.Collections.IEnumerator AnimateTransform(Transform targetTransform, TransformState state, string eventType)
        {
            Vector3 startPosition = targetTransform.localPosition;
            Vector3 startRotation = targetTransform.localEulerAngles;
            Vector3 startScale = targetTransform.localScale;
            
            float elapsedTime = 0f;
            
            while (elapsedTime < state.animationTime)
            {
                elapsedTime += Time.deltaTime;
                float progress = elapsedTime / state.animationTime;
                float curveValue = state.animationCurve.Evaluate(progress);
                
                if (state.modifyPosition)
                {
                    targetTransform.localPosition = Vector3.Lerp(startPosition, state.position, curveValue);
                }
                
                if (state.modifyRotation)
                {
                    targetTransform.localEulerAngles = Vector3.Lerp(startRotation, state.rotation, curveValue);
                }
                
                if (state.modifyScale)
                {
                    targetTransform.localScale = Vector3.Lerp(startScale, state.scale, curveValue);
                }
                
                yield return null;
            }
            
            // Ensure final state is exact
            ApplyTransformInstant(targetTransform, state, eventType);
        }
        
        private void RestoreOriginalStates()
        {
            if (_originalStates == null || _controlledTransforms == null) return;
            
            for (int i = 0; i < _originalStates.Length && i < _controlledTransforms.Length; i++)
            {
                if (_originalStates[i] != null && _controlledTransforms[i] != null)
                {
                    ApplyTransformInstant(_controlledTransforms[i], _originalStates[i], "restore");
                }
            }
            
            if (logChanges)
            {
                Debug.Log($"StepTransformController: Restored original transform states for step '{gameObject.name}'.", Step);
            }
        }
        
        /// <summary>
        /// Manually apply a transform state.
        /// </summary>
        public void ApplyCustomTransformState(TransformState state, float animationTime = 0f)
        {
            if (state == null) return;
            
            if (animationTime > 0)
            {
                state.animationTime = animationTime;
            }
            
            ApplyTransformState(state, "custom");
        }
        
        /// <summary>
        /// Manually restore original transform states.
        /// </summary>
        public void RestoreTransforms()
        {
            RestoreOriginalStates();
        }
        
        /// <summary>
        /// Get the original transform state for a specific GameObject.
        /// </summary>
        public TransformState GetOriginalState(GameObject target)
        {
            if (_originalStates == null || _controlledTransforms == null) return null;
            
            for (int i = 0; i < _controlledTransforms.Length; i++)
            {
                if (_controlledTransforms[i] != null && _controlledTransforms[i].gameObject == target)
                {
                    return _originalStates[i];
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Check if a transform is being controlled by this component.
        /// </summary>
        public bool IsControllingTransform(Transform targetTransform)
        {
            if (_controlledTransforms == null) return false;
            
            return System.Array.Exists(_controlledTransforms, t => t == targetTransform);
        }
    }
} 