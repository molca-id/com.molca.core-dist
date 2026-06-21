using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Sequence
{
    [System.Serializable]
    public class AnimationInfo
    {
        public Animator animator;
        public string triggerParameter = "Play";
        public string animationClipName;
        
        [HideInInspector] public float elapsedTime;
        [HideInInspector] public float clipLength;
        [HideInInspector] public bool isComplete;
    }

    public class AnimationStep : Step
    {
        [Header("Animation Settings")]
        [SerializeField] private List<AnimationInfo> animations = new List<AnimationInfo>();
        
        [Header("Completion Settings")]
        [Tooltip("If true, waits for all animations to finish. If false, completes when the longest animation finishes.")]
        [SerializeField] private bool waitForAllAnimations = true;
        
        private bool isPaused = false;
        
        private void Awake()
        {
            // Auto-populate with animator on this GameObject if list is empty
            if (animations.Count == 0)
            {
                var animator = GetComponent<Animator>();
                if (animator != null)
                {
                    animations.Add(new AnimationInfo { animator = animator });
                }
            }
        }

        protected override void OnStepActivated()
        {
            isPaused = false;
            
            if (animations.Count == 0)
            {
                Debug.LogWarning($"AnimationStep '{gameObject.name}': No animations configured!", this);
                Complete();
                return;
            }
            
            // Initialize and trigger all animations
            bool anyAnimationStarted = false;
            foreach (var animInfo in animations)
            {
                animInfo.elapsedTime = 0f;
                animInfo.isComplete = false;
                
                if (animInfo.animator != null)
                {
                    animInfo.animator.SetTrigger(animInfo.triggerParameter);
                    animInfo.clipLength = GetAnimationLength(animInfo);
                    anyAnimationStarted = true;
                }
                else
                {
                    Debug.LogWarning($"AnimationStep '{gameObject.name}': Animator is null for one of the animations!", this);
                    animInfo.isComplete = true;
                }
            }
            
            if (!anyAnimationStarted)
            {
                Debug.LogError($"AnimationStep '{gameObject.name}': No valid animators found!", this);
                Complete();
            }
        }

        public override void UpdateStep()
        {
            base.UpdateStep();
            
            // Only update if not paused and step is active
            if (!isPaused && CurrentStatus == StepStatus.Active)
            {
                bool allComplete = true;
                
                foreach (var animInfo in animations)
                {
                    if (animInfo.isComplete) continue;
                    
                    if (animInfo.animator != null)
                    {
                        animInfo.elapsedTime += Time.deltaTime;
                        
                        if (animInfo.elapsedTime >= animInfo.clipLength)
                        {
                            animInfo.isComplete = true;
                        }
                        else
                        {
                            allComplete = false;
                        }
                    }
                }
                
                // Complete the step when appropriate
                if (waitForAllAnimations && allComplete)
                {
                    Complete();
                }
                else if (!waitForAllAnimations)
                {
                    // Find the longest animation duration
                    float longestDuration = animations.Max(a => a.clipLength);
                    float maxElapsedTime = animations.Max(a => a.elapsedTime);
                    
                    if (maxElapsedTime >= longestDuration)
                    {
                        Complete();
                    }
                }
            }
        }

        protected override void OnStepPaused()
        {
            isPaused = true;
            
            foreach (var animInfo in animations)
            {
                if (animInfo.animator != null)
                {
                    animInfo.animator.speed = 0f;
                }
            }
        }

        protected override void OnStepResumed()
        {
            isPaused = false;
            
            foreach (var animInfo in animations)
            {
                if (animInfo.animator != null)
                {
                    animInfo.animator.speed = 1f;
                }
            }
        }

        protected override void OnStepDeactivated()
        {
            isPaused = false;
            
            foreach (var animInfo in animations)
            {
                animInfo.elapsedTime = 0f;
                animInfo.isComplete = false;
                
                if (animInfo.animator != null)
                {
                    animInfo.animator.speed = 1f;
                }
            }
        }

        private float GetAnimationLength(AnimationInfo animInfo)
        {
            if (animInfo.animator == null || animInfo.animator.runtimeAnimatorController == null)
                return 1f;
            
            if (string.IsNullOrEmpty(animInfo.animationClipName))
                return 1f;

            foreach (AnimationClip clip in animInfo.animator.runtimeAnimatorController.animationClips)
            {
                if (clip.name == animInfo.animationClipName)
                    return clip.length;
            }
            
            Debug.LogWarning($"AnimationStep '{gameObject.name}': Could not find animation clip '{animInfo.animationClipName}' in animator", this);
            return 1f;
        }
        
        /// <summary>
        /// Gets the total progress of all animations (0-1).
        /// </summary>
        public float GetProgress()
        {
            if (animations.Count == 0) return 1f;
            
            float totalProgress = 0f;
            foreach (var animInfo in animations)
            {
                if (animInfo.clipLength > 0)
                {
                    totalProgress += Mathf.Clamp01(animInfo.elapsedTime / animInfo.clipLength);
                }
            }
            
            return totalProgress / animations.Count;
        }
        
        /// <summary>
        /// Gets the number of animations configured.
        /// </summary>
        public int GetAnimationCount()
        {
            return animations.Count;
        }
        
        /// <summary>
        /// Gets the number of completed animations.
        /// </summary>
        public int GetCompletedAnimationCount()
        {
            return animations.Count(a => a.isComplete);
        }
    }
} 