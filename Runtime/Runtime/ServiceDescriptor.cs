using System;

namespace Molca
{
    /// <summary>
    /// Describes a service registration in the RuntimeManager service container.
    /// </summary>
    internal class ServiceDescriptor
    {
        public Type ServiceType { get; }
        public Type ImplementationType { get; }
        public object Instance { get; set; }
        public Func<object> Factory { get; }
        public ServiceLifetime Lifetime { get; }

        /// <summary>
        /// True once the singleton instance has had its [Inject] members populated.
        /// Lets <see cref="RuntimeManager.GetService(Type)"/> skip re-injection on
        /// every subsequent resolve.
        /// </summary>
        public bool DependenciesInjected { get; set; }
        
        // Constructor for singleton with instance
        public ServiceDescriptor(Type serviceType, object instance)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            Instance = instance ?? throw new ArgumentNullException(nameof(instance));
            ImplementationType = instance.GetType();
            Lifetime = ServiceLifetime.Singleton;
        }
        
        // Constructor for singleton with implementation type
        public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            ImplementationType = implementationType ?? throw new ArgumentNullException(nameof(implementationType));
            Lifetime = lifetime;
            
            if (!serviceType.IsAssignableFrom(implementationType))
            {
                throw new ArgumentException($"{implementationType.Name} does not implement {serviceType.Name}");
            }
        }
        
        // Constructor for transient with factory
        public ServiceDescriptor(Type serviceType, Func<object> factory)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Lifetime = ServiceLifetime.Transient;
        }
        
        public object CreateInstance()
        {
            if (Lifetime == ServiceLifetime.Singleton && Instance != null)
                return Instance;
            
            if (Factory != null)
                return Factory();
            
            if (ImplementationType != null)
            {
                var instance = Activator.CreateInstance(ImplementationType);
                if (Lifetime == ServiceLifetime.Singleton)
                    Instance = instance;
                return instance;
            }
            
            throw new InvalidOperationException($"Cannot create instance of {ServiceType.Name}");
        }
    }
}
