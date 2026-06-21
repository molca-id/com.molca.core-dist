using UnityEngine;
using UnityEditor;
using Molca.Sequence;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Molca.Editor
{
    /// <summary>
    /// Utility class for constructing Step hierarchy relationships in editor mode.
    /// This is completely separate from runtime logic and only used for editor visualization.
    /// </summary>
    public static class StepHierarchyBuilder
    {
        private static readonly FieldInfo _childrenStepsField;
        private static readonly FieldInfo _parentSearchedField;
        private static readonly FieldInfo _parentField;

        static StepHierarchyBuilder()
        {
            // Cache reflection info for performance
            _childrenStepsField = typeof(Step).GetField("_childrenSteps", BindingFlags.NonPublic | BindingFlags.Instance);
            _parentSearchedField = typeof(Step).GetField("_parentSearched", BindingFlags.NonPublic | BindingFlags.Instance);
            _parentField = typeof(Step).GetField("_parent", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        /// <summary>
        /// Builds the complete step hierarchy for a given SequenceController.
        /// This method constructs parent-child relationships based on the Transform hierarchy,
        /// independent of runtime initialization.
        /// </summary>
        /// <param name="controller">The SequenceController to build hierarchy for</param>
        /// <returns>List of all steps with properly established relationships</returns>
        public static List<Step> BuildHierarchy(SequenceController controller)
        {
            if (controller == null) return new List<Step>();

            // Get all Step components in the controller's hierarchy
            var allSteps = controller.GetComponentsInChildren<Step>(true).ToList();

            if (!allSteps.Any()) return new List<Step>();

            // Clear any existing relationships to prevent stale data
            ClearExistingRelationships(allSteps);

            // Build parent-child relationships based on Transform hierarchy
            BuildParentChildRelationships(allSteps);

            return allSteps;
        }

        /// <summary>
        /// Builds parent-child relationships for a list of steps based on Transform hierarchy.
        /// </summary>
        /// <param name="allSteps">All steps to establish relationships for</param>
        public static void BuildParentChildRelationships(List<Step> allSteps)
        {
            // Group steps by their transform parent
            var stepsByParent = allSteps.GroupBy(step => step.transform.parent)
                                        .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var step in allSteps)
            {
                // Find parent step by traversing up the transform hierarchy
                Step parentStep = FindParentStep(step, allSteps);

                if (parentStep != null)
                {
                    // Set parent relationship
                    SetParent(step, parentStep);

                    // Add to parent's children list
                    AddChild(parentStep, step);
                }
                else
                {
                    // This is a root step
                    SetParent(step, null);
                }
            }
        }

        /// <summary>
        /// Finds the closest parent Step component by traversing up the transform hierarchy.
        /// </summary>
        private static Step FindParentStep(Step step, List<Step> allSteps)
        {
            Transform current = step.transform.parent;

            while (current != null)
            {
                // Check if this transform has a Step component
                Step parentStep = current.GetComponent<Step>();
                if (parentStep != null && allSteps.Contains(parentStep))
                {
                    return parentStep;
                }

                current = current.parent;
            }

            return null;
        }

        /// <summary>
        /// Clears any existing parent-child relationships to prevent stale data.
        /// Also ensures every step has a non-null children list so editor builds are stable.
        /// </summary>
        private static void ClearExistingRelationships(List<Step> steps)
        {
            foreach (var step in steps)
            {
                // Clear or create children list
                if (_childrenStepsField != null)
                {
                    var childrenList = _childrenStepsField.GetValue(step) as List<Step>;
                    if (childrenList == null)
                    {
                        childrenList = new List<Step>();
                        _childrenStepsField.SetValue(step, childrenList);
                    }
                    else
                    {
                        childrenList.Clear();
                    }
                }

                // Reset parent search flag and parent reference
                if (_parentSearchedField != null)
                {
                    _parentSearchedField.SetValue(step, false);
                }

                if (_parentField != null)
                {
                    _parentField.SetValue(step, null);
                }
            }
        }

        /// <summary>
        /// Sets the parent of a step using reflection.
        /// </summary>
        private static void SetParent(Step step, Step parent)
        {
            if (_parentField != null)
            {
                _parentField.SetValue(step, parent);
            }

            if (_parentSearchedField != null)
            {
                _parentSearchedField.SetValue(step, true);
            }
        }

        /// <summary>
        /// Adds a child to a parent's children list using reflection.
        /// Guarantees the list exists to avoid partial hierarchies.
        /// </summary>
        private static void AddChild(Step parent, Step child)
        {
            if (_childrenStepsField != null)
            {
                var childrenList = _childrenStepsField.GetValue(parent) as List<Step>;
                if (childrenList == null)
                {
                    childrenList = new List<Step>();
                    _childrenStepsField.SetValue(parent, childrenList);
                }

                if (!childrenList.Contains(child))
                {
                    childrenList.Add(child);
                }
            }
        }

        /// <summary>
        /// This is useful for building the hierarchy from a list that's already been fetched, 
        /// like the runtime list of steps from the controller.
        /// </summary>
        /// <param name="allSteps">A list of all steps to build the hierarchy from.</param>
        public static void BuildHierarchyFromList(IReadOnlyList<Step> allSteps)
        {
            if (allSteps == null) return;
    
            var stepMap = allSteps.ToDictionary(s => s.transform, s => s);

            // Clear existing parent/child relationships to ensure a clean build
            foreach (var step in allSteps)
            {
                SetParent(step, null);
                if (_childrenStepsField != null)
                {
                    var childrenList = _childrenStepsField.GetValue(step) as List<Step>;
                    childrenList?.Clear();
                }
            }

            // Build the hierarchy by linking steps to their parents
            foreach (var step in allSteps)
            {
                var parentTransform = step.transform.parent;
                if (parentTransform != null && stepMap.TryGetValue(parentTransform, out var parentStep))
                {
                    SetParent(step, parentStep);
                    AddChild(parentStep, step);
                }
            }
        }

        /// <summary>
        /// Gets the root steps (steps with no parent Step) from a given list of steps.
        /// This version filters for active and enabled GameObjects.
        /// </summary>
        public static List<Step> GetRootSteps(IReadOnlyList<Step> steps)
        {
            return steps.Where(step => GetParent(step) == null &&
                                      step.gameObject.activeInHierarchy &&
                                      step.enabled).ToList();
        }

        /// <summary>
        /// Gets the parent of a step using reflection.
        /// </summary>
        private static Step GetParent(Step step)
        {
            if (_parentSearchedField != null && _parentField != null)
            {
                bool parentSearched = (bool)_parentSearchedField.GetValue(step);
                if (parentSearched)
                {
                    return _parentField.GetValue(step) as Step;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if the hierarchy needs to be rebuilt based on changes in the scene.
        /// </summary>
        public static bool NeedsHierarchyRebuild(SequenceController controller, List<Step> currentSteps)
        {
            if (controller == null) return true;

            var currentSceneSteps = controller.GetComponentsInChildren<Step>(true).ToList();

            // If step count changed, hierarchy needs rebuild
            if (currentSteps == null || currentSteps.Count != currentSceneSteps.Count)
            {
                return true;
            }

            // If any step's transform hierarchy changed, rebuild
            foreach (var step in currentSteps)
            {
                if (step == null || !currentSceneSteps.Contains(step))
                {
                    return true;
                }

                // Check if parent relationship changed
                Step expectedParent = FindParentStep(step, currentSceneSteps);
                Step currentParent = GetParent(step);

                if (expectedParent != currentParent)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
