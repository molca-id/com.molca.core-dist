using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.Events;
using Molca.Utilities;

namespace Molca
{
    /// <summary>
    /// Core runtime management and dependency injection container.
    /// Manages subsystem lifecycle and provides service resolution capabilities.
    /// </summary>
    public class RuntimeManager : MonoBehaviour
    {
        private static RuntimeManager _main;
        private const float SubsystemInitTimeoutSeconds = 20f;
        private bool _isInitializing;
        private bool _isShuttingDown;

        // Bootstrap-lifetime cancellation: cancelled on shutdown/quit so any
        // still-pending subsystem InitializeAsync unwinds instead of leaking.
        private readonly System.Threading.CancellationTokenSource _bootstrapCts =
            new System.Threading.CancellationTokenSource();

        // Subsystem management
        private RuntimeSubsystem[] _subsystems;
        private List<RuntimeSubsystem> _registeredSubsystems;
        // Resolved subsystem initialization order (after [DependsOn] topo sort + dedup).
        // Shutdown walks this in reverse so teardown is the inverse of init.
        private List<RuntimeSubsystem> _initOrder;
        private bool _isReady;
        
        // Service container (DI)
        private Dictionary<Type, ServiceDescriptor> _services = new Dictionary<Type, ServiceDescriptor>();
        private HashSet<Type> _resolvingTypes = new HashSet<Type>(); // Circular dependency detection

        // Objects opted into auto-injection before the runtime is ready. Flushed once
        // bootstrap reaches the readiness step. See RegisterForAutoInjection.
        private static readonly List<object> _pendingInjection = new List<object>();

        // Per-type [Inject] member cache. Reflection metadata is domain-stable, so this
        // survives RuntimeManager shutdown; it turns every repeat injection from a full
        // GetFields/GetProperties + attribute scan into a dictionary hit. Main thread only.
        private static readonly Dictionary<Type, InjectionPlan> _injectionPlans =
            new Dictionary<Type, InjectionPlan>();

        private sealed class InjectionPlan
        {
            public static readonly InjectionPlan Empty = new InjectionPlan(
                Array.Empty<(FieldInfo, InjectAttribute)>(),
                Array.Empty<(PropertyInfo, InjectAttribute)>());

            public readonly (FieldInfo Field, InjectAttribute Attribute)[] Fields;
            public readonly (PropertyInfo Property, InjectAttribute Attribute)[] Properties;

            public bool HasInjections => Fields.Length > 0 || Properties.Length > 0;

            public InjectionPlan(
                (FieldInfo, InjectAttribute)[] fields,
                (PropertyInfo, InjectAttribute)[] properties)
            {
                Fields = fields;
                Properties = properties;
            }
        }

        private static InjectionPlan GetInjectionPlan(Type type)
        {
            if (_injectionPlans.TryGetValue(type, out var plan))
                return plan;

            List<(FieldInfo, InjectAttribute)> fields = null;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var injectAttr = field.GetCustomAttribute<InjectAttribute>();
                if (injectAttr != null)
                    (fields ??= new List<(FieldInfo, InjectAttribute)>()).Add((field, injectAttr));
            }

            List<(PropertyInfo, InjectAttribute)> properties = null;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var injectAttr = property.GetCustomAttribute<InjectAttribute>();
                if (injectAttr != null && property.CanWrite)
                    (properties ??= new List<(PropertyInfo, InjectAttribute)>()).Add((property, injectAttr));
            }

            plan = (fields == null && properties == null)
                ? InjectionPlan.Empty
                : new InjectionPlan(
                    fields?.ToArray() ?? Array.Empty<(FieldInfo, InjectAttribute)>(),
                    properties?.ToArray() ?? Array.Empty<(PropertyInfo, InjectAttribute)>());

            _injectionPlans[type] = plan;
            return plan;
        }

        public static bool IsReady => _main != null && _main._isReady;

        private void Awake()
        {
            _registeredSubsystems = new List<RuntimeSubsystem>();
        }

        #region Initialize
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void NullCheck()
        {
            if (_main == null)
                GetInstance();
        }

        private static async void GetInstance()
        {
            try 
            {
                var projectSettings = await GetProjectSettingsAsync();
                if (projectSettings == null)
                {
                    Debug.LogError("MolcaProjectSettings failed to load. RuntimeManager cannot initialize.");
                    return;
                }

                if (projectSettings.RuntimeManager == null)
                {
                    Debug.LogError("RuntimeManager prefab is not set in MolcaProjectSettings!");
                    return;
                }

                _main = Instantiate(projectSettings.RuntimeManager);
                DontDestroyOnLoad(_main.gameObject);
                await _main.InitializeAsync();
            }
            catch (Exception e)
            {
                Debug.LogError($"Error initializing RuntimeManager: {e}");
            }
        }

        private static Awaitable<MolcaProjectSettings> GetProjectSettingsAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return MolcaProjectSettings.LoadAsync();
#else
            var completed = new AwaitableCompletionSource<MolcaProjectSettings>();
            completed.SetResult(MolcaProjectSettings.Instance);
            return completed.Awaitable;
#endif
        }

        private async Awaitable InitializeAsync()
        {
            if (_isInitializing || _isReady) return;

            _isInitializing = true;
            Debug.Log("[RuntimeManager] Initializing...");

            try
            {
                // Run any bootstrap extensions configured on MolcaProjectSettings before
                // GlobalSettings or subsystems initialize. SDK layers attach extensions
                // here for layer-specific bootstrap config (e.g., XR rig prefabs).
                await RunBootstrapExtensionsAsync();

                GlobalSettings.main.Initialize();
                GlobalSettings.main.LoadAllSettings();

                _subsystems = GetComponentsInChildren<RuntimeSubsystem>().Where(x => x.enabled).ToArray();

                // Collect all candidate subsystems (child + externally registered).
                var allSubsystems = GetAllSubsystems();

                // Ensure only one instance of each subsystem type exists.
                var subsystemTypes = new Dictionary<Type, RuntimeSubsystem>();
                var subsystemsToRemove = new List<RuntimeSubsystem>();

                foreach (var subsystem in allSubsystems)
                {
                    var subsystemType = subsystem.GetType();
                    if (subsystemTypes.ContainsKey(subsystemType))
                    {
                        Debug.LogWarning($"[RuntimeManager] Multiple instances of subsystem type {subsystemType.Name} detected. Keeping the first instance and removing duplicates.");
                        subsystemsToRemove.Add(subsystem);
                    }
                    else
                    {
                        subsystemTypes[subsystemType] = subsystem;
                    }
                }

                foreach (var subsystem in subsystemsToRemove)
                {
                    allSubsystems.Remove(subsystem);
                }

                // Resolve initialization order: topological over [DependsOn] declarations,
                // with InitializationPriority (descending) as the tiebreaker for nodes
                // that are unrelated in the dependency graph.
                allSubsystems = SortSubsystemsForInitialization(allSubsystems);
                _initOrder = allSubsystems;

                var initAwaitables = new List<Awaitable<bool>>();
                int completed = 0;
                foreach (var subsystem in allSubsystems)
                {
                    if(!subsystem.IsRuntimeValid) continue;
                    var completion = new AwaitableCompletionSource<bool>();
                    // Timeout (or bootstrap teardown) cancels this token so a stalled
                    // InitializeAsync unwinds instead of running forever.
                    var subsystemCts = System.Threading.CancellationTokenSource
                        .CreateLinkedTokenSource(_bootstrapCts.Token);
                    var state = new SubsystemInitState();
                    _ = DriveSubsystemInitialization(
                        subsystem, completion, state, subsystemCts.Token,
                        () => Debug.Log($"[RuntimeManager] Initialize subsystem: {subsystem.GetType().Name} ({++completed}/{allSubsystems.Count})"));
                    initAwaitables.Add(completion.Awaitable);
                    _ = MonitorSubsystemInitialization(subsystem, state, completion, subsystemCts, SubsystemInitTimeoutSeconds);
                }

                await WaitForAll(initAwaitables.ToArray());

                // Mark every initialized subsystem as active now that the full init
                // sequence is complete. This is the single authoritative activation point —
                // subsystems must not call MarkActive themselves.
                foreach (var subsystem in allSubsystems)
                    if (subsystem.IsRuntimeValid)
                        subsystem.MarkActive();

                // Register subsystems as services (by concrete type and interfaces)
                RegisterSubsystemServices(allSubsystems);

                // Auto-inject dependencies into scene objects
                await InjectSceneObjectsAsync();

                // Flush any objects that registered for auto-injection before the
                // runtime was ready (e.g., factories or scene scripts that ran in Awake).
                FlushPendingInjections();

                _isReady = true;
                _isInitializing = false;
                Debug.Log("[RuntimeManager] Initialization complete!");

                // Dispatch application initialized event
                TypedEvents.ApplicationInitialized.Dispatch();
            }
            catch (Exception e)
            {
                _isInitializing = false;
                Debug.LogError($"[RuntimeManager] Failed to initialize: {e}");
                throw;
            }
        }

        private async Awaitable RunBootstrapExtensionsAsync()
        {
            var projectSettings = MolcaProjectSettings.Instance;
            if (projectSettings == null) return;

            var extensions = projectSettings.BootstrapExtensions;
            if (extensions == null || extensions.Count == 0) return;

            for (int i = 0; i < extensions.Count; i++)
            {
                var ext = extensions[i];
                if (ext == null)
                {
                    Debug.LogWarning($"[RuntimeManager] Null BootstrapExtension entry at index {i} in MolcaProjectSettings; skipping.");
                    continue;
                }

                Debug.Log($"[RuntimeManager] Running bootstrap extension: {ext.GetType().Name}");
                try
                {
                    var awaitable = ext.OnBootstrap(projectSettings);
                    if (awaitable != null) await awaitable;
                }
                catch (Exception e)
                {
                    // A failing extension should not prevent the rest of bootstrap from
                    // proceeding — log and continue. Individual extensions own their own
                    // failure semantics.
                    Debug.LogError($"[RuntimeManager] BootstrapExtension {ext.GetType().Name} threw: {e}");
                }
            }
        }

        private static List<RuntimeSubsystem> SortSubsystemsForInitialization(List<RuntimeSubsystem> candidates)
        {
            var tiebreaker = Comparer<RuntimeSubsystem>.Create(
                (a, b) => b.InitializationPriority.CompareTo(a.InitializationPriority));

            var sorted = TopologicalSort.Sort(
                candidates,
                s => GetDeclaredDependencies(s, candidates),
                tiebreaker,
                out var cycleParticipants);

            if (cycleParticipants != null && cycleParticipants.Count > 0)
            {
                var names = string.Join(", ", cycleParticipants.Select(s => s.GetType().Name));
                Debug.LogError(
                    $"[RuntimeManager] [DependsOn] cycle detected among subsystems: {names}. " +
                    "Falling back to InitializationPriority-only order for these subsystems. " +
                    "Fix the cyclic dependency declaration to silence this error.");
            }

            return sorted;
        }

        private static IEnumerable<RuntimeSubsystem> GetDeclaredDependencies(
            RuntimeSubsystem subsystem,
            IReadOnlyList<RuntimeSubsystem> candidates)
        {
            var attrs = subsystem.GetType().GetCustomAttributes<DependsOnAttribute>(inherit: true);
            foreach (var attr in attrs)
            {
                if (attr.Dependencies == null) continue;
                foreach (var depType in attr.Dependencies)
                {
                    if (depType == null) continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (depType.IsInstanceOfType(candidates[i]))
                        {
                            yield return candidates[i];
                        }
                    }
                }
            }
        }

        private static void FlushPendingInjections()
        {
            if (_pendingInjection.Count == 0) return;

            int flushed = 0;
            for (int i = 0; i < _pendingInjection.Count; i++)
            {
                var target = _pendingInjection[i];
                if (target == null) continue;

                // For UnityEngine.Object, also skip if it's been destroyed since registration.
                if (target is UnityEngine.Object uo && uo == null) continue;

                InjectDependencies(target);
                flushed++;
            }
            _pendingInjection.Clear();
            Debug.Log($"[RuntimeManager] Flushed {flushed} pending auto-injection target(s).");
        }

        private void Shutdown()
        {
            if (_isShuttingDown || !_isReady) return;

            Debug.Log("[RuntimeManager] Shutting down...");
            _isShuttingDown = true;

            // Unwind any still-pending bootstrap work (e.g., quit during init).
            _bootstrapCts.Cancel();
            
            // Shutdown in reverse of the resolved init order (LIFO).
            // Falls back to discovery order if init never completed.
            List<RuntimeSubsystem> subsystemsList;
            if (_initOrder != null)
            {
                subsystemsList = new List<RuntimeSubsystem>(_initOrder);
            }
            else
            {
                subsystemsList = GetAllSubsystems();
            }
            subsystemsList.Reverse();

            foreach (var subsystem in subsystemsList)
            {
                subsystem.Shutdown();
            }
            
            GlobalSettings.main.SaveAllSettings();
            GlobalSettings.main.DeInitialize();
            
            // Clear service container
            _services.Clear();
            _resolvingTypes.Clear();
            _pendingInjection.Clear();
            _initOrder = null;

            Debug.Log("[RuntimeManager] Shutdown complete.");
            _isReady = false;
            _isShuttingDown = false;
            _main = null;
        }
        #endregion

        #region Subsystem Registration

        /// <summary>
        /// Registers a subsystem that exists outside the RuntimeManager hierarchy
        /// </summary>
        /// <param name="subsystem">The subsystem to register</param>
        /// <returns>Completes once the subsystem has finished initializing. Awaiting is optional.</returns>
        public static async Awaitable RegisterSubsystem(RuntimeSubsystem subsystem)
        {
            if (_main == null)
            {
                Debug.LogError("RuntimeManager is not initialized. Cannot register subsystem.");
                return;
            }

            if (subsystem == null)
            {
                Debug.LogError("Cannot register null subsystem.");
                return;
            }
            
            await WaitForInitialization();

            if (_main._registeredSubsystems.Contains(subsystem))
            {
                Debug.LogWarning($"Subsystem {subsystem.GetType().Name} is already registered.");
                return;
            }

            _main._registeredSubsystems.Add(subsystem);
            Debug.Log($"Registered subsystem: {subsystem.GetType().Name}");

            try
            {
                await subsystem.InitializeAsync(_main._bootstrapCts.Token);
            }
            catch (Exception e)
            {
                // Don't rethrow: most call sites fire-and-forget this method, so a
                // rethrown exception would go unobserved.
                Debug.LogError($"[RuntimeManager] Registered subsystem {subsystem.GetType().Name} threw during initialization: {e}");
                return;
            }

            // Bring the external subsystem to parity with bootstrap-discovered ones:
            // activate it and register it in the service container (concrete type +
            // interfaces) so [Inject] and GetService resolve it.
            if (subsystem.IsRuntimeValid)
                subsystem.MarkActive();
            _main.RegisterSubsystemServices(new List<RuntimeSubsystem> { subsystem });

            Debug.Log($"Initialized registered subsystem: {subsystem.GetType().Name}");
        }

        /// <summary>
        /// Deregisters a subsystem that was previously registered
        /// </summary>
        /// <param name="subsystem">The subsystem to deregister</param>
        public static void DeregisterSubsystem(RuntimeSubsystem subsystem)
        {
            if (_main == null) return;

            if (subsystem == null)
            {
                Debug.LogError("Cannot deregister null subsystem.");
                return;
            }

            if (_main._registeredSubsystems.Remove(subsystem))
            {
                Debug.Log($"Deregistered subsystem: {subsystem.GetType().Name}");
                
                // If RuntimeManager is initialized, shutdown the subsystem
                if (_main._isReady)
                {
                    subsystem.Shutdown();
                }
            }
            else
            {
                Debug.LogWarning($"Subsystem {subsystem.GetType().Name} was not registered.");
            }
        }

        /// <summary>
        /// Gets all subsystems (both child and registered)
        /// </summary>
        private List<RuntimeSubsystem> GetAllSubsystems()
        {
            var allSubsystems = new List<RuntimeSubsystem>();
            
            // Add child subsystems
            if (_subsystems != null)
            {
                allSubsystems.AddRange(_subsystems);
            }
            
            // Add registered subsystems
            allSubsystems.AddRange(_registeredSubsystems);

            return allSubsystems;
        }

        /// <summary>
        /// Returns a snapshot of all discovered subsystems (child + externally registered), for
        /// read-only introspection by tooling. Returns an empty list before bootstrap.
        /// </summary>
        /// <remarks>Order is discovery order, not the resolved init order — see <see cref="GetResolvedInitOrder"/>.</remarks>
        public static IReadOnlyList<RuntimeSubsystem> GetSubsystems()
            => _main == null ? Array.Empty<RuntimeSubsystem>() : _main.GetAllSubsystems();

        /// <summary>
        /// Returns the resolved subsystem initialization order (after the <see cref="DependsOnAttribute"/>
        /// topological sort), or an empty list if bootstrap has not completed.
        /// </summary>
        public static IReadOnlyList<RuntimeSubsystem> GetResolvedInitOrder()
            => _main?._initOrder == null
                ? (IReadOnlyList<RuntimeSubsystem>)Array.Empty<RuntimeSubsystem>()
                : _main._initOrder.ToArray();

        #endregion
        
        #region Service Container (Dependency Injection)
        
        /// <summary>
        /// Register a singleton service instance.
        /// </summary>
        public static void RegisterService<TInterface>(TInterface instance) where TInterface : class
        {
            if (_main == null)
            {
                Debug.LogError("[RuntimeManager] Cannot register service before initialization.");
                return;
            }
            
            RegisterService(typeof(TInterface), instance);
        }
        
        /// <summary>
        /// Register a singleton service instance (non-generic).
        /// </summary>
        public static void RegisterService(Type serviceType, object instance)
        {
            if (_main == null)
            {
                Debug.LogError("[RuntimeManager] Cannot register service before initialization.");
                return;
            }
            
            if (serviceType == null)
            {
                Debug.LogError("[RuntimeManager] Service type cannot be null.");
                return;
            }
            
            if (instance == null)
            {
                Debug.LogError($"[RuntimeManager] Cannot register null instance for type {serviceType.Name}.");
                return;
            }
            
            var descriptor = new ServiceDescriptor(serviceType, instance);
            _main._services[serviceType] = descriptor;
            
            Debug.Log($"[RuntimeManager] Registered service: {serviceType.Name} -> {instance.GetType().Name}");
        }
        
        /// <summary>
        /// Bind an interface to an implementation type (lazy singleton).
        /// Instance will be created on first request.
        /// </summary>
        public static void BindService<TInterface, TImplementation>() 
            where TImplementation : class, TInterface, new()
        {
            if (_main == null)
            {
                Debug.LogError("[RuntimeManager] Cannot bind service before initialization.");
                return;
            }
            
            var serviceType = typeof(TInterface);
            var implementationType = typeof(TImplementation);
            
            var descriptor = new ServiceDescriptor(serviceType, implementationType, ServiceLifetime.Singleton);
            _main._services[serviceType] = descriptor;
            
            Debug.Log($"[RuntimeManager] Bound service: {serviceType.Name} -> {implementationType.Name}");
        }
        
        /// <summary>
        /// Register a factory for creating service instances.
        /// Factory is called each time the service is requested (transient lifetime).
        /// </summary>
        public static void RegisterFactory<T>(Func<T> factory) where T : class
        {
            if (_main == null)
            {
                Debug.LogError("[RuntimeManager] Cannot register factory before initialization.");
                return;
            }
            
            if (factory == null)
            {
                Debug.LogError("[RuntimeManager] Factory cannot be null.");
                return;
            }
            
            var serviceType = typeof(T);
            var descriptor = new ServiceDescriptor(serviceType, () => factory());
            _main._services[serviceType] = descriptor;
            
            Debug.Log($"[RuntimeManager] Registered factory for: {serviceType.Name}");
        }
        
        /// <summary>
        /// Resolve a service from the container.
        /// Works with subsystems, registered services, and interface bindings.
        /// </summary>
        public static T GetService<T>() where T : class
        {
            return GetService(typeof(T)) as T;
        }
        
        /// <summary>
        /// Resolve a service from the container (non-generic).
        /// </summary>
        public static object GetService(Type serviceType)
        {
            return GetServiceInternal(serviceType, logIfNotFound: true);
        }

        private static object GetServiceInternal(Type serviceType, bool logIfNotFound)
        {
            if (_main == null)
            {
                Debug.LogWarning($"[RuntimeManager] Not initialized. Cannot resolve {serviceType.Name}.");
                return null;
            }
            
            if (serviceType == null)
            {
                Debug.LogError("[RuntimeManager] Service type cannot be null.");
                return null;
            }
            
            // Check for circular dependencies
            if (_main._resolvingTypes.Contains(serviceType))
            {
                Debug.LogError($"[RuntimeManager] Circular dependency detected while resolving {serviceType.Name}");
                return null;
            }
            
            try
            {
                _main._resolvingTypes.Add(serviceType);
                
                // 1. Check if service is registered in container
                if (_main._services.TryGetValue(serviceType, out var descriptor))
                {
                    var instance = descriptor.CreateInstance();

                    if (descriptor.Lifetime == ServiceLifetime.Singleton)
                    {
                        // Inject singleton dependencies exactly once, on first resolve
                        // (previously re-ran on every resolve). Flag set first so a
                        // self-referential [Inject] can't recurse.
                        if (!descriptor.DependenciesInjected)
                        {
                            descriptor.DependenciesInjected = true;
                            InjectDependencies(instance);
                        }
                    }
                    else
                    {
                        InjectDependencies(instance);
                    }

                    return instance;
                }
                
                // 2. Check if it's a RuntimeSubsystem (backward compatibility)
                if (typeof(RuntimeSubsystem).IsAssignableFrom(serviceType))
                {
                    var subsystem = GetSubsystemInternal(serviceType);
                    if (subsystem != null)
                    {
                        // Cache it as a service for future lookups
                        RegisterService(serviceType, subsystem);
                        return subsystem;
                    }
                }
                
                // 3. Try to find by implemented interfaces
                foreach (var kvp in _main._services)
                {
                    if (serviceType.IsAssignableFrom(kvp.Value.ImplementationType))
                    {
                        return kvp.Value.CreateInstance();
                    }
                }
                
                if (logIfNotFound)
                    Debug.LogWarning($"[RuntimeManager] Service not found: {serviceType.Name}");
                return null;
            }
            finally
            {
                _main._resolvingTypes.Remove(serviceType);
            }
        }
        
        /// <summary>
        /// Try to resolve a service. Returns true if successful.
        /// </summary>
        public static bool TryGetService<T>(out T service) where T : class
        {
            service = GetService<T>();
            return service != null;
        }
        
        /// <summary>
        /// Check if a service is registered.
        /// </summary>
        public static bool HasService<T>() where T : class
        {
            return HasService(typeof(T));
        }
        
        /// <summary>
        /// Check if a service is registered (non-generic).
        /// </summary>
        public static bool HasService(Type serviceType)
        {
            if (_main == null || serviceType == null)
                return false;
            
            return _main._services.ContainsKey(serviceType);
        }
        
        /// <summary>
        /// Get all registered service types.
        /// </summary>
        public static Type[] GetRegisteredServiceTypes()
        {
            if (_main == null)
                return Array.Empty<Type>();
            
            return _main._services.Keys.ToArray();
        }

        /// <summary>
        /// Read-only snapshot of one service-container registration, for tooling/introspection
        /// (e.g. the MCP <c>molca_services</c> tool). Avoids exposing the internal descriptor type.
        /// </summary>
        public readonly struct ServiceRegistrationInfo
        {
            /// <summary>The registered service type (interface or concrete).</summary>
            public Type ServiceType { get; }
            /// <summary>The implementation type, or null for a factory registration.</summary>
            public Type ImplementationType { get; }
            /// <summary>"Singleton" or "Transient".</summary>
            public ServiceLifetime Lifetime { get; }
            /// <summary>True if an eager singleton instance has already been created.</summary>
            public bool HasInstance { get; }
            /// <summary>True if registered via a factory delegate.</summary>
            public bool IsFactory { get; }

            internal ServiceRegistrationInfo(Type serviceType, Type implementationType,
                ServiceLifetime lifetime, bool hasInstance, bool isFactory)
            {
                ServiceType = serviceType;
                ImplementationType = implementationType;
                Lifetime = lifetime;
                HasInstance = hasInstance;
                IsFactory = isFactory;
            }
        }

        /// <summary>
        /// Returns a snapshot of all service-container registrations (eager singletons, lazy bindings,
        /// and factories), for read-only introspection. Empty before bootstrap.
        /// </summary>
        public static IReadOnlyList<ServiceRegistrationInfo> GetServiceRegistrations()
        {
            if (_main == null)
                return Array.Empty<ServiceRegistrationInfo>();

            var list = new List<ServiceRegistrationInfo>(_main._services.Count);
            foreach (var kvp in _main._services)
            {
                var d = kvp.Value;
                list.Add(new ServiceRegistrationInfo(
                    d.ServiceType, d.ImplementationType, d.Lifetime,
                    hasInstance: d.Instance != null, isFactory: d.Factory != null));
            }
            return list;
        }

        #endregion

        #region Dependency Injection
        
        /// <summary>
        /// Inject dependencies into an object's fields and properties marked with
        /// <see cref="InjectAttribute"/>.
        /// </summary>
        /// <exception cref="MissingDependencyException">
        /// Thrown if any field or property marked <c>[Inject]</c> with
        /// <see cref="InjectAttribute.Required"/> = <c>true</c> cannot be resolved.
        /// Failing fast at injection time produces a stack trace pointing at the
        /// injection site rather than a downstream <see cref="NullReferenceException"/>.
        /// </exception>
        public static void InjectDependencies(object target)
        {
            if (target == null || _main == null)
                return;

            var type = target.GetType();
            var plan = GetInjectionPlan(type);
            List<string> unresolvedRequired = null;

            foreach (var (field, injectAttr) in plan.Fields)
            {
                InjectField(target, field, injectAttr, ref unresolvedRequired);
            }

            foreach (var (property, injectAttr) in plan.Properties)
            {
                InjectProperty(target, property, injectAttr, ref unresolvedRequired);
            }

            if (unresolvedRequired != null)
            {
                throw new MissingDependencyException(type, unresolvedRequired);
            }
        }

        private static void InjectField(object target, FieldInfo field, InjectAttribute attribute, ref List<string> unresolvedRequired)
        {
            // Skip if already set (unless ForceInject is true). A destroyed
            // UnityEngine.Object is fake-null (boxed reference non-null) and must
            // count as unset, or the stale reference would survive re-injection.
            if (!attribute.ForceInject && HasUsableValue(field.GetValue(target)))
                return;

            var fieldType = field.FieldType;
            var service = ResolveForInjection(fieldType, attribute.Required);

            if (service != null)
            {
                field.SetValue(target, service);
            }
            else if (attribute.Required)
            {
                Debug.LogError($"[RuntimeManager] Required dependency {fieldType.Name} not found for {target.GetType().Name}.{field.Name}");
                (unresolvedRequired ??= new List<string>()).Add($"{field.Name} ({fieldType.Name})");
            }
        }

        private static void InjectProperty(object target, PropertyInfo property, InjectAttribute attribute, ref List<string> unresolvedRequired)
        {
            // Skip if already set (unless ForceInject is true). See InjectField for
            // the destroyed-object (fake-null) rule.
            if (!attribute.ForceInject && HasUsableValue(property.GetValue(target)))
                return;

            var propertyType = property.PropertyType;
            var service = ResolveForInjection(propertyType, attribute.Required);

            if (service != null)
            {
                property.SetValue(target, service);
            }
            else if (attribute.Required)
            {
                Debug.LogError($"[RuntimeManager] Required dependency {propertyType.Name} not found for {target.GetType().Name}.{property.Name}");
                (unresolvedRequired ??= new List<string>()).Add($"{property.Name} ({propertyType.Name})");
            }
        }

        // A member value blocks injection only if it is genuinely usable:
        // non-null, and not a destroyed UnityEngine.Object.
        private static bool HasUsableValue(object value)
        {
            if (value == null) return false;
            if (value is UnityEngine.Object uo && uo == null) return false;
            return true;
        }

        // Optional injections resolve without the "Service not found" warning —
        // staying null is their documented behavior, not a problem to report.
        private static object ResolveForInjection(Type serviceType, bool required)
        {
            return GetServiceInternal(serviceType, logIfNotFound: required);
        }
        
        /// <summary>
        /// Create an instance with constructor injection and field/property injection.
        /// </summary>
        public static T CreateWithInjection<T>() where T : class
        {
            var type = typeof(T);
            var constructors = type.GetConstructors();
            
            if (constructors.Length == 0)
            {
                Debug.LogError($"[RuntimeManager] No public constructor found for {type.Name}");
                return null;
            }
            
            // Find constructor with [Inject] attribute, or use the one with most parameters
            var constructor = constructors
                .OrderByDescending(c => c.GetCustomAttribute<InjectAttribute>() != null ? 1000 : c.GetParameters().Length)
                .First();
            
            var parameters = constructor.GetParameters();
            var args = new object[parameters.Length];
            
            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = GetService(parameters[i].ParameterType);
                if (args[i] == null)
                {
                    Debug.LogError($"[RuntimeManager] Cannot resolve parameter '{parameters[i].Name}' of type {parameters[i].ParameterType.Name} for {type.Name}");
                    return null;
                }
            }
            
            var instance = Activator.CreateInstance(type, args) as T;
            
            // Also inject fields and properties
            InjectDependencies(instance);
            
            return instance;
        }
        
        /// <summary>
        /// Automatically inject dependencies into all MonoBehaviours in the scene.
        /// Called automatically after RuntimeManager initialization.
        /// </summary>
        private async Awaitable InjectSceneObjectsAsync()
        {
            await Awaitable.NextFrameAsync(); // Ensure scene is fully loaded
            
            var monoBehaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, 
                FindObjectsSortMode.None
            );
            
            int injectedCount = 0;
            foreach (var mb in monoBehaviours)
            {
                if (mb == null || mb == this)
                    continue;

                // The cached plan makes this a dictionary hit for repeated component types.
                if (GetInjectionPlan(mb.GetType()).HasInjections)
                {
                    InjectDependencies(mb);
                    injectedCount++;
                }
            }
            
            Debug.Log($"[RuntimeManager] Injected dependencies into {injectedCount} MonoBehaviours");
        }
        
        /// <summary>
        /// Manually trigger dependency injection for a specific object.
        /// Useful for dynamically instantiated objects.
        /// </summary>
        public static void InjectInto(MonoBehaviour target)
        {
            if (target == null)
            {
                Debug.LogError("[RuntimeManager] Cannot inject into null target.");
                return;
            }

            InjectDependencies(target);
        }

        /// <summary>
        /// Opts <paramref name="target"/> into automatic dependency injection regardless of
        /// when it is registered relative to <see cref="RuntimeManager"/> readiness.
        /// </summary>
        /// <remarks>
        /// If the runtime is already initialized, the target is injected immediately.
        /// Otherwise it is queued and injected during the readiness step of the bootstrap
        /// pipeline, before <see cref="TypedEvents.ApplicationInitialized"/> is dispatched.
        /// <para>
        /// Use this from factories, spawners, or scene scripts that may run during
        /// <see cref="MonoBehaviour.Awake"/> — earlier than the runtime's automatic
        /// scene-injection pass — and want a single, timing-agnostic call site.
        /// </para>
        /// </remarks>
        /// <param name="target">The object to inject. Null is logged and ignored.</param>
        public static void RegisterForAutoInjection(object target)
        {
            if (target == null)
            {
                Debug.LogWarning("[RuntimeManager] RegisterForAutoInjection called with null target; ignoring.");
                return;
            }

            if (IsReady)
            {
                InjectDependencies(target);
            }
            else
            {
                _pendingInjection.Add(target);
            }
        }

        #endregion
        
        #region Service Registration Helpers
        
        /// <summary>
        /// Register all subsystems as services (by concrete type and interfaces).
        /// </summary>
        private void RegisterSubsystemServices(List<RuntimeSubsystem> subsystems)
        {
            foreach (var subsystem in subsystems)
            {
                var type = subsystem.GetType();
                
                // Register by concrete type
                RegisterService(type, subsystem);
                
                // Register by all implemented interfaces (except base framework interfaces)
                var interfaces = type.GetInterfaces()
                    .Where(i => i != typeof(IRuntimeSubsystem) 
                             && !i.IsGenericType
                             && i.Assembly != typeof(IRuntimeSubsystem).Assembly);
                
                foreach (var @interface in interfaces)
                {
                    // Only register if not already registered
                    if (!_services.ContainsKey(@interface))
                    {
                        RegisterService(@interface, subsystem);
                    }
                }
            }
            
            Debug.Log($"[RuntimeManager] Registered {subsystems.Count} subsystems as services");
        }
        
        #endregion

        /// <summary>
        /// Get a subsystem by type. Prefer using GetService&lt;T&gt;() for new code.
        /// This method is kept for backward compatibility.
        /// </summary>
        public static T GetSubsystem<T>() where T : RuntimeSubsystem
        {
            return GetService<T>();
        }
        
        /// <summary>
        /// Internal method to get subsystem without service caching.
        /// </summary>
        private static RuntimeSubsystem GetSubsystemInternal(Type subsystemType)
        {
            if (_main == null)
                return null;
            
            // Search in child subsystems first
            if (_main._subsystems != null)
            {
                for (int i = 0; i < _main._subsystems.Length; i++)
                {
                    if (subsystemType.IsInstanceOfType(_main._subsystems[i]))
                        return _main._subsystems[i];
                }
            }
            
            // Search in registered subsystems
            foreach (var subsystem in _main._registeredSubsystems)
            {
                if (subsystemType.IsInstanceOfType(subsystem))
                    return subsystem;
            }
            
            return null;
        }

        public static Coroutine RunCoroutine(IEnumerator enumerator)
        {
            return _main.StartCoroutine(enumerator);
        }

        public static async Awaitable WaitForInitialization()
        {
            while (!IsReady)
            {
                await Awaitable.NextFrameAsync();
            }
        }

        private static async Awaitable WaitForAwaitable(Awaitable awaitable, Action onComplete)
        {
            // onComplete must run even when the awaitable faults — WaitForAll counts
            // completions, and a skipped callback deadlocks every WaitForAll caller
            // (including bootstrap). The exception is logged, not rethrown: these
            // calls are fire-and-forget.
            try
            {
                await awaitable;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                onComplete?.Invoke();
            }
        }

        private static async Awaitable WaitForAwaitable<T>(Awaitable<T> awaitable, Action onComplete)
        {
            try
            {
                await awaitable;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                onComplete?.Invoke();
            }
        }

        public static async Awaitable WaitForAll(params Awaitable[] awaitables)
        {
            if (awaitables == null || awaitables.Length == 0)
                return;

            var completion = new AwaitableCompletionSource<bool>();
            int remainingCount = awaitables.Length;

            void OnComplete()
            {
                remainingCount--;
                if (remainingCount == 0)
                    completion.SetResult(true);
            }

            foreach (var awaitable in awaitables)
            {
                _ = WaitForAwaitable(awaitable, OnComplete);
            }

            await completion.Awaitable;
        }

        public static async Awaitable WaitForAll<T>(params Awaitable<T>[] awaitables)
        {
            if (awaitables == null || awaitables.Length == 0)
                return;

            var completion = new AwaitableCompletionSource<bool>();
            int remainingCount = awaitables.Length;

            void OnComplete()
            {
                remainingCount--;
                if (remainingCount == 0)
                    completion.SetResult(true);
            }

            foreach (var awaitable in awaitables)
            {
                _ = WaitForAwaitable(awaitable, OnComplete);
            }

            await completion.Awaitable;
        }

        public static Awaitable AwaitWithTimeout(
            Awaitable awaitable,
            float timeoutSeconds,
            string label = null)
        {
            return AwaitWithTimeout(awaitable, timeoutSeconds, System.Threading.CancellationToken.None, label);
        }

        /// <summary>
        /// Awaits <paramref name="awaitable"/> with a timeout and a cancellation token.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels the wait (not the underlying awaitable) — throws
        /// <see cref="OperationCanceledException"/> when cancelled.
        /// </param>
        /// <exception cref="TimeoutException">Thrown when the timeout elapses first.</exception>
        public static async Awaitable AwaitWithTimeout(
            Awaitable awaitable,
            float timeoutSeconds,
            System.Threading.CancellationToken cancellationToken,
            string label = null)
        {
            var completion = new AwaitableCompletionSource<bool>();
            var completed = false;

            // Explicit fire-and-forget: AwaitAndSignalAsync owns its exceptions
            // (routes them into the completion source).
            _ = AwaitAndSignalAsync(awaitable, completion, () => completed = true);

            var startTime = Time.realtimeSinceStartup;
            while (!completed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Time.realtimeSinceStartup - startTime > timeoutSeconds)
                {
                    var message = string.IsNullOrEmpty(label)
                        ? $"Awaitable timed out after {timeoutSeconds} seconds."
                        : $"{label} timed out after {timeoutSeconds} seconds.";
                    throw new TimeoutException(message);
                }
                await Awaitable.NextFrameAsync();
            }

            await completion.Awaitable;
        }

        public static Awaitable<T> AwaitWithTimeout<T>(
            Awaitable<T> awaitable,
            float timeoutSeconds,
            string label = null)
        {
            return AwaitWithTimeout(awaitable, timeoutSeconds, System.Threading.CancellationToken.None, label);
        }

        /// <summary>
        /// Awaits <paramref name="awaitable"/> with a timeout and a cancellation token.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels the wait (not the underlying awaitable) — throws
        /// <see cref="OperationCanceledException"/> when cancelled.
        /// </param>
        /// <exception cref="TimeoutException">Thrown when the timeout elapses first.</exception>
        public static async Awaitable<T> AwaitWithTimeout<T>(
            Awaitable<T> awaitable,
            float timeoutSeconds,
            System.Threading.CancellationToken cancellationToken,
            string label = null)
        {
            var completion = new AwaitableCompletionSource<T>();
            var completed = false;

            // Explicit fire-and-forget: AwaitAndSignalAsync owns its exceptions
            // (routes them into the completion source).
            _ = AwaitAndSignalAsync(awaitable, completion, () => completed = true);

            var startTime = Time.realtimeSinceStartup;
            while (!completed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Time.realtimeSinceStartup - startTime > timeoutSeconds)
                {
                    var message = string.IsNullOrEmpty(label)
                        ? $"Awaitable timed out after {timeoutSeconds} seconds."
                        : $"{label} timed out after {timeoutSeconds} seconds.";
                    throw new TimeoutException(message);
                }
                await Awaitable.NextFrameAsync();
            }

            return await completion.Awaitable;
        }

        private static async Awaitable AwaitAndSignalAsync(
            Awaitable awaitable,
            AwaitableCompletionSource<bool> completion,
            Action onComplete)
        {
            try
            {
                await awaitable;
                completion.SetResult(true);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                onComplete?.Invoke();
            }
        }

        private static async Awaitable AwaitAndSignalAsync<T>(
            Awaitable<T> awaitable,
            AwaitableCompletionSource<T> completion,
            Action onComplete)
        {
            try
            {
                var result = await awaitable;
                completion.SetResult(result);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                onComplete?.Invoke();
            }
        }

        public static Awaitable<T> FromResult<T>(T result)
        {
            var completion = new AwaitableCompletionSource<T>();
            completion.SetResult(result);
            return completion.Awaitable;
        }

        public static async Awaitable AwaitHandle<T>(AsyncOperationHandle<T> handle)
        {
            if (handle.IsDone)
            {
                return;
            }

            var tcs = new AwaitableCompletionSource<bool>();
            handle.Completed += _ => tcs.SetResult(true);
            await tcs.Awaitable;
        }

        public static async Awaitable AwaitHandle(AsyncOperationHandle handle)
        {
            if (handle.IsDone)
            {
                return;
            }

            var tcs = new AwaitableCompletionSource<bool>();
            handle.Completed += _ => tcs.SetResult(true);
            await tcs.Awaitable;
        }

        private sealed class SubsystemInitState
        {
            public bool Completed;
        }

        private static async Awaitable DriveSubsystemInitialization(
            RuntimeSubsystem subsystem,
            AwaitableCompletionSource<bool> completion,
            SubsystemInitState state,
            System.Threading.CancellationToken cancellationToken,
            Action onInitialized)
        {
            try
            {
                await subsystem.InitializeAsync(cancellationToken);
                state.Completed = true;
                // TrySet: the timeout monitor may have already failed this source.
                if (completion.TrySetResult(true))
                    onInitialized?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Timeout or bootstrap teardown — the monitor/owner already logged.
                completion.TrySetResult(false);
            }
            catch (Exception e)
            {
                Debug.LogError($"[RuntimeManager] Subsystem {subsystem.GetType().Name} threw during initialization: {e}");
                state.Completed = true;
                completion.TrySetResult(false);
            }
        }

        private static async Awaitable MonitorSubsystemInitialization(
            RuntimeSubsystem subsystem,
            SubsystemInitState state,
            AwaitableCompletionSource<bool> completion,
            System.Threading.CancellationTokenSource subsystemCts,
            float timeoutSeconds)
        {
            try
            {
                await Awaitable.WaitForSecondsAsync(timeoutSeconds);
                if (!state.Completed)
                {
                    Debug.LogError($"[RuntimeManager] Subsystem {subsystem.GetType().Name} did not finish initialization within {timeoutSeconds} seconds (InitializeAsync still pending / finishCallback never invoked). Failing its init so bootstrap can continue.");
                    // Cancel the stalled init and resolve its awaitable so WaitForAll
                    // in InitializeAsync can finish and the app doesn't soft-lock at boot.
                    subsystemCts.Cancel();
                    completion.TrySetResult(false);
                }
            }
            finally
            {
                subsystemCts.Dispose();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                TypedEvents.ApplicationPausing.Dispatch();
            }
            else
            {
                TypedEvents.ApplicationResuming.Dispatch();
            }
        }

        private void OnApplicationQuit()
        {
            TypedEvents.ApplicationQuitting.Dispatch();

            // Quit during bootstrap: Shutdown() below won't run (not ready yet),
            // so cancel in-flight init work here.
            _bootstrapCts.Cancel();

            if (IsReady)
            {
                Shutdown();
            }
        }
    }
}