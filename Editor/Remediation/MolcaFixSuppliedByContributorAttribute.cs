using System;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// Marks an <see cref="IMolcaFix"/> implementation that an <see cref="IMolcaFixContributor"/> supplies,
    /// so <see cref="MolcaFixRegistry"/> skips it during <c>TypeCache</c> discovery instead of reporting it as
    /// missing a parameterless constructor.
    /// </summary>
    /// <remarks>
    /// Adapters that wrap an existing per-domain fix (a scene fix, a sequence validator fix) necessarily take
    /// the wrapped instance in their constructor. Without this marker the registry could not tell a
    /// deliberately contributor-supplied fix apart from a fix author's mistake, and every domain reload would
    /// log a false discovery warning.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class MolcaFixSuppliedByContributorAttribute : Attribute
    {
    }
}
