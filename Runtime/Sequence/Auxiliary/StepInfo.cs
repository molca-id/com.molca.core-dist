using Molca.Localization;
using UnityEngine;
using UnityEngine.Events;
using System;

namespace Molca.Sequence.Auxiliary
{
    [Serializable, AuxiliaryMenu("Base/Info")]
    public class StepInfo : StepAuxiliary
    {
        [SerializeField] private DynamicLocalization title;
        [SerializeField] private DynamicLocalization description;

        [SerializeField] private UnityEvent<StepStatus> onStatusChanged;

        public DynamicLocalization Title => title;
        public DynamicLocalization Description => description;

        /// <summary>
        /// Ensures <see cref="Title"/> and <see cref="Description"/> are non-null. They are
        /// null on a freshly constructed <see cref="StepInfo"/> (e.g. one created via
        /// reflection by an importer) until Unity serialization materializes them. Editor
        /// authoring helper — does not alter runtime behavior.
        /// </summary>
        public void EnsureLocalizationInitialized()
        {
            title ??= new DynamicLocalization();
            description ??= new DynamicLocalization();
        }

        public StepStatus CurrentStatus => Step.CurrentStatus;

        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }

        public TimeSpan Duration => CurrentStatus == StepStatus.Completed ? EndTime - StartTime : TimeSpan.Zero;

        protected override void OnInitialize()
        {
            title.Init($"Step_{Step.GetInstanceID()}_Title");
            description.Init($"Step_{Step.GetInstanceID()}_Description");

            Step.OnStatusChanged += OnStepStatusChanged;
        }

        public override void OnStepBegin()
        {
            StartTime = DateTime.Now;
        }
        
        public override void OnStepCompleted()
        {
            EndTime = DateTime.Now;
        }

        public override void OnStepReset()
        {
            StartTime = DateTime.MinValue;
            EndTime = DateTime.MinValue;
        }

        private void OnStepStatusChanged(StepStatus status)
        {
            onStatusChanged?.Invoke(status);
        }
    }
}