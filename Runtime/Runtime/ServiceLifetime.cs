namespace Molca
{
    /// <summary>
    /// Defines the lifetime of a registered service in the RuntimeManager service container.
    /// </summary>
    public enum ServiceLifetime
    {
        /// <summary>
        /// A single instance is created and shared across all requests.
        /// The instance is created on first request (lazy) or during registration if an instance is provided.
        /// </summary>
        Singleton,
        
        /// <summary>
        /// A new instance is created for each request.
        /// Requires a factory function to be registered.
        /// </summary>
        Transient
    }
}
