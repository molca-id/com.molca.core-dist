using Molca.Editor.Networking.Validation;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// Maps a validation finding to the workspace view that can fix it.
    /// </summary>
    /// <remarks>
    /// One mapping, used by the Overview action list, the Diagnostics tree, and Doctor. Findings are
    /// addressed to an entity kind and id precisely so this translation is mechanical — a finding that
    /// could not say where it belongs would be a finding a user cannot act on.
    /// <para>
    /// Pure and Unity-free, so a test can assert that every entity kind lands somewhere sensible without
    /// building a view.
    /// </para>
    /// </remarks>
    internal static class NetworkHubDeepLinks
    {
        /// <summary>
        /// Where a finding should navigate.
        /// </summary>
        /// <param name="finding">The finding; <c>null</c> targets Diagnostics.</param>
        /// <returns>A navigation target. Never empty.</returns>
        internal static NetworkHubNavigationTarget For(NetworkValidationFinding finding)
        {
            if (finding == null)
                return new NetworkHubNavigationTarget(NetworkHubViews.Diagnostics);

            string entity = finding.EntityId;
            string environment = finding.EnvironmentId;

            switch (finding.EntityKind)
            {
                case NetworkValidationEntityKind.Environment:
                    return new NetworkHubNavigationTarget(NetworkHubViews.Environments, entity, entity);

                case NetworkValidationEntityKind.Service:
                    return new NetworkHubNavigationTarget(NetworkHubViews.Services, entity, environment);

                // A binding finding is authored on the service's binding grid, not on the environment, so
                // it navigates to the service with the environment previewed.
                case NetworkValidationEntityKind.Binding:
                    return new NetworkHubNavigationTarget(NetworkHubViews.Services, entity, environment);

                case NetworkValidationEntityKind.PolicyProfile:
                    return new NetworkHubNavigationTarget(NetworkHubViews.Policies, entity, environment);

                case NetworkValidationEntityKind.CredentialProfile:
                    return new NetworkHubNavigationTarget(NetworkHubViews.Credentials, entity, environment);

                case NetworkValidationEntityKind.EndpointCollection:
                case NetworkValidationEntityKind.Endpoint:
                    return new NetworkHubNavigationTarget(NetworkHubViews.Endpoints, entity, environment);

                // A catalog-level finding is about the asset as a whole — the default environment, a
                // schema version, a transition flag — and Overview is where those are shown.
                default:
                    return new NetworkHubNavigationTarget(NetworkHubViews.Overview, entity, environment);
            }
        }
    }
}
