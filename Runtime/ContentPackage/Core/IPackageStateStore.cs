using System.Collections.Generic;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// Durable storage for package install state, behind an interface so the service does not
    /// depend on a file.
    ///
    /// The seam earns its place: <see cref="PackageManifest"/> writes to one fixed path under
    /// <c>persistentDataPath</c>, so every test fixture that constructs a
    /// <see cref="Molca.ContentPackage.Services.PackageService"/> shares a single file and has to
    /// clean up after the others. That coupling produced tests reading state they never wrote, and
    /// it makes failure paths — a full disk, a corrupt document, a rejected write — awkward to
    /// exercise at all.
    ///
    /// Every mutation returns whether it reached durable storage. Callers must not treat a package
    /// as installed on a false: this store is the only record that installed content exists, and
    /// state that never committed is state the next launch will not have.
    /// </summary>
    public interface IPackageStateStore
    {
        /// <summary>Gets the stored state for a package, or null when it is not tracked.</summary>
        /// <param name="packageId">The unique identifier of the package.</param>
        PackageState GetState(string packageId);

        /// <summary>Gets a snapshot of every tracked state.</summary>
        List<PackageState> GetAllStates();

        /// <summary>Stores one state and commits.</summary>
        /// <param name="state">The state to store.</param>
        /// <returns>True when the change reached durable storage.</returns>
        bool SetState(PackageState state);

        /// <summary>Stores many states and commits once.</summary>
        /// <param name="states">The states to store. Null entries are skipped.</param>
        /// <returns>True when the change reached durable storage.</returns>
        bool SetStatesBatch(IEnumerable<PackageState> states);

        /// <summary>Removes every tracked state.</summary>
        /// <returns>True when the change reached durable storage.</returns>
        bool Clear();

        /// <summary>The content release version installed on this device, or empty when none is.</summary>
        string InstalledContentVersion { get; set; }

        /// <summary>The content release ID installed on this device, recorded with its version.</summary>
        string InstalledReleaseId { get; set; }

        /// <summary>
        /// The active and staged release records. Never null — an absent record reads as "nothing
        /// activated yet", which is a real state rather than a missing one.
        /// </summary>
        /// <remarks>
        /// Returns the live record so a caller can mutate and commit it in one step. Callers that
        /// need a snapshot to compare against must take a
        /// <see cref="ReleaseActivationRecord.Clone"/>.
        /// </remarks>
        ReleaseActivationRecord Activation { get; }

        /// <summary>Commits the activation record.</summary>
        /// <param name="record">The record to store. Null is ignored.</param>
        /// <returns>
        /// True when the change reached durable storage. A false here must fail the activation:
        /// committing a release the next launch will not remember produces a device running content
        /// it believes it never installed.
        /// </returns>
        bool SetActivation(ReleaseActivationRecord record);
    }
}
