using System.Collections.Generic;
using Molca.ColorID.Editor.Remediation;
using Molca.ContentPackage.Editor.Remediation;
using Molca.Editor.Networking.Remediation;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// Core's own project-wide remediation domains, contributed through the same seam a fork uses.
    /// </summary>
    /// <remarks>
    /// <para><b>References is deliberately absent.</b> Reference repair is a revision-pinned transaction
    /// (<c>ReferenceRepairPlanner</c> → <c>ReferenceRepairExecutor</c>) that rejects a plan built against a
    /// snapshot the project has moved past. Driving it from a generic sweep would either discard that
    /// guarantee or fake it. The References workspace stays the place reference repairs are approved; the
    /// remediation panel links to it rather than pretending to own it.</para>
    /// <para><b>Sequence is deliberately absent</b> for a different reason: it is per-controller, so a
    /// project-wide button would have to invent which controllers to touch.</para>
    /// </remarks>
    internal sealed class CoreRemediationDomainProvider : IMolcaRemediationDomainProvider
    {
        /// <inheritdoc/>
        public IEnumerable<MolcaRemediationDomain> GetDomains() => new[]
        {
            new MolcaRemediationDomain(
                NetworkRemediationBridge.Domain, "Networking",
                createRequest: policy => NetworkRemediationBridge.Request(policy),
                order: 10),

            new MolcaRemediationDomain(
                ContentPackageRemediationBridge.Domain, "Content Packages",
                createRequest: policy => ContentPackageRemediationBridge.Request(policy),
                order: 20),

            new MolcaRemediationDomain(
                ColorThemeRemediationBridge.Domain, "Colour Theme",
                createRequest: policy => ColorThemeRemediationBridge.Request(policy),
                order: 30),
        };
    }
}
