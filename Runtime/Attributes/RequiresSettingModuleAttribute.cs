using System;

namespace Molca
{
    /// <summary>
    /// Declares that a <see cref="RuntimeSubsystem"/> needs a <see cref="Molca.Settings.SettingModule"/>
    /// of the given type to be registered in <c>GlobalSettings.modules</c>.
    /// </summary>
    /// <remarks>
    /// <para>Without this, a subsystem whose module was never added fails at runtime with a null reference
    /// during bootstrap — long after the mistake was made, and nowhere near the declaration that implies
    /// the requirement. Declaring it lets the bootstrap check report a
    /// <c>bootstrap.module-missing</c> finding at edit time, and lets remediation offer to create and
    /// register the module.</para>
    /// <para>Mirrors <see cref="DependsOnAttribute"/>: class-level, inherited, and repeatable. It is an
    /// attribute rather than a member on <see cref="RuntimeSubsystem"/> so that declaring a requirement
    /// costs a fork nothing and adds no member to the subsystem surface.</para>
    /// <para>Only subsystems present on the project's <see cref="RuntimeManager"/> prefab are checked — a
    /// subsystem type that merely exists in an assembly is not in use, and reporting a missing module for
    /// it would be a false positive.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [RequiresSettingModule(typeof(AudioSettingsModule))]
    /// public class AudioSubsystem : RuntimeSubsystem { ... }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class RequiresSettingModuleAttribute : Attribute
    {
        /// <summary>The <see cref="Molca.Settings.SettingModule"/> types this subsystem requires.</summary>
        public Type[] ModuleTypes { get; }

        /// <summary>Declares that the decorated subsystem requires the given setting modules.</summary>
        /// <param name="moduleTypes">One or more concrete <see cref="Molca.Settings.SettingModule"/> types.</param>
        public RequiresSettingModuleAttribute(params Type[] moduleTypes)
        {
            ModuleTypes = moduleTypes ?? Array.Empty<Type>();
        }
    }
}
