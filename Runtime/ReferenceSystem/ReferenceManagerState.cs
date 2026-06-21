using Molca.Settings;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Mutable runtime state paired with <see cref="ReferenceManagerSettings"/>.
    /// Holds the persisted toggles so the module's SerializeFields stay authored
    /// defaults only (SO cardinal rule).
    /// </summary>
    public class ReferenceManagerState : SettingState
    {
        public bool EnableDebugLogging;
        public bool AutoValidateOnScan;
        public bool ShowValidationResults;

        public ReferenceManagerState(ReferenceManagerSettings module)
        {
            // Seed initial state from authored defaults.
            EnableDebugLogging = module.DefaultEnableDebugLogging;
            AutoValidateOnScan = module.DefaultAutoValidateOnScan;
            ShowValidationResults = module.DefaultShowValidationResults;
        }

        public override void Load(SettingModule owner)
        {
            EnableDebugLogging = owner.LoadInt(nameof(EnableDebugLogging), EnableDebugLogging ? 1 : 0) == 1;
            AutoValidateOnScan = owner.LoadInt(nameof(AutoValidateOnScan), AutoValidateOnScan ? 1 : 0) == 1;
            ShowValidationResults = owner.LoadInt(nameof(ShowValidationResults), ShowValidationResults ? 1 : 0) == 1;
        }

        public override void Save(SettingModule owner)
        {
            owner.SaveInt(nameof(EnableDebugLogging), EnableDebugLogging ? 1 : 0);
            owner.SaveInt(nameof(AutoValidateOnScan), AutoValidateOnScan ? 1 : 0);
            owner.SaveInt(nameof(ShowValidationResults), ShowValidationResults ? 1 : 0);
        }
    }
}
