# Dependency Injection in Molca Framework

The RuntimeManager now includes a powerful dependency injection (DI) container that makes it easy to manage dependencies between systems and write testable code.

## Table of Contents

- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
- [Usage Patterns](#usage-patterns)
- [Service Registration](#service-registration)
- [Service Resolution](#service-resolution)
- [Injection Methods](#injection-methods)
- [Advanced Usage](#advanced-usage)
- [Testing](#testing)
- [Best Practices](#best-practices)
- [Migration Guide](#migration-guide)

## Quick Start

### Simple Field Injection

```csharp
using Molca;
using Molca.Events;

public class GameManager : MonoBehaviour
{
    // Dependencies are automatically injected after RuntimeManager initializes
    [Inject] private EventDispatcher _eventDispatcher;
    [Inject] private ReferenceManager _referenceManager;
    [Inject] private ModalManager _modalManager;
    
    private void Start()
    {
        // Dependencies are ready to use!
        _eventDispatcher.DispatchEvent("GameStarted");
        _modalManager.AddMessage("Welcome!");
    }
}
```

### Property Injection

```csharp
public class UIController : MonoBehaviour
{
    [Inject] 
    private EventDispatcher EventDispatcher { get; set; }
    
    [Inject(required: false)] // Optional dependency
    private IAnalyticsService Analytics { get; set; }
    
    private void OnEnable()
    {
        EventDispatcher.RegisterEvent("UIUpdate", OnUIUpdate);
        
        // Safe to use even if Analytics is not available
        Analytics?.TrackEvent("UI_Opened");
    }
}
```

## Core Concepts

### Service Lifetime

Services can have two lifetimes:

1. **Singleton** (default): One instance shared across all requests
2. **Transient**: New instance created for each request

### Service Types

1. **Subsystems**: All `RuntimeSubsystem` components are automatically registered as services
2. **Custom Services**: Any class can be registered as a service
3. **Interface Services**: Services can be registered by interface for loose coupling

## Usage Patterns

### Pattern 1: Field Injection (Recommended)

```csharp
public class PlayerController : MonoBehaviour
{
    [Inject] private EventDispatcher _events;
    [Inject] private IScoreService _scoreService;
    
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Coin"))
        {
            _scoreService.AddPoints(10);
            _events.DispatchEvent("CoinCollected");
        }
    }
}
```

**Pros:**
- Clear and explicit dependencies
- Works with Unity's serialization
- Auto-injected after RuntimeManager initialization

**Cons:**
- Dependencies are not available in Awake()
- Relies on RuntimeManager being initialized

### Pattern 2: Manual Resolution

```csharp
public class GameBootstrap : MonoBehaviour
{
    private async void Start()
    {
        await RuntimeManager.WaitForInitialization();
        
        var eventDispatcher = RuntimeManager.GetService<EventDispatcher>();
        var modalManager = RuntimeManager.GetService<ModalManager>();
        
        // Use services...
    }
}
```

**Pros:**
- Full control over when services are resolved
- Can check if service exists before using

**Cons:**
- More boilerplate code
- Still coupled to RuntimeManager

### Pattern 3: Constructor Injection (For Non-MonoBehaviour Classes)

```csharp
public class GameStateMachine
{
    private readonly EventDispatcher _events;
    private readonly IScoreService _scoreService;
    
    [Inject]
    public GameStateMachine(EventDispatcher events, IScoreService scoreService)
    {
        _events = events;
        _scoreService = scoreService;
    }
    
    public void EnterState(string state)
    {
        _events.DispatchEvent($"State_{state}");
    }
}

// Usage
var stateMachine = RuntimeManager.CreateWithInjection<GameStateMachine>();
```

## Service Registration

### Automatic Registration

All `RuntimeSubsystem` components are automatically registered:

```csharp
// EventDispatcher is a RuntimeSubsystem
// Automatically registered as:
// - EventDispatcher (concrete type)
// - Any interfaces it implements

var dispatcher = RuntimeManager.GetService<EventDispatcher>();
```

### Manual Registration - Singleton Instance

```csharp
// Register a pre-created instance
var myService = new MyService();
RuntimeManager.RegisterService<IMyService>(myService);
```

### Manual Registration - Lazy Singleton

```csharp
// Instance created on first request
RuntimeManager.BindService<IScoreService, ScoreService>();

// Later...
var scoreService = RuntimeManager.GetService<IScoreService>(); // Creates instance here
```

### Manual Registration - Factory (Transient)

```csharp
// New instance created each time
RuntimeManager.RegisterFactory<ILogger>(() => new Logger(DateTime.Now));

var logger1 = RuntimeManager.GetService<ILogger>(); // New instance
var logger2 = RuntimeManager.GetService<ILogger>(); // Different instance
```

## Service Resolution

### Generic Resolution

```csharp
var eventDispatcher = RuntimeManager.GetService<EventDispatcher>();
var modalManager = RuntimeManager.GetService<ModalManager>();
```

### Try-Get Pattern

```csharp
if (RuntimeManager.TryGetService<IAnalyticsService>(out var analytics))
{
    analytics.TrackEvent("Feature_Used");
}
```

### Check Service Existence

```csharp
if (RuntimeManager.HasService<IAnalyticsService>())
{
    // Service is registered
}
```

### Get All Registered Services

```csharp
var registeredTypes = RuntimeManager.GetRegisteredServiceTypes();
foreach (var type in registeredTypes)
{
    Debug.Log($"Registered: {type.Name}");
}
```

## Injection Methods

### 1. Automatic Scene Injection

All MonoBehaviours with `[Inject]` attributes are automatically injected after RuntimeManager initializes.

```csharp
public class MyScript : MonoBehaviour
{
    [Inject] private EventDispatcher _events;
    
    // _events is automatically injected!
}
```

### 2. Manual Injection

For dynamically instantiated objects:

```csharp
var prefab = Instantiate(myPrefab);
RuntimeManager.InjectInto(prefab);
```

### 3. InjectOnAwake Component

Add this component to prefabs that need automatic injection:

```csharp
// Add InjectOnAwake component to prefab
// All components on that GameObject will be injected automatically
```

### 4. Constructor Injection

For non-MonoBehaviour classes:

```csharp
var instance = RuntimeManager.CreateWithInjection<MyClass>();
```

## Advanced Usage

### Optional Dependencies

```csharp
public class FeatureController : MonoBehaviour
{
    [Inject(required: false)] 
    private IOptionalFeature _feature;
    
    private void Start()
    {
        if (_feature != null)
        {
            _feature.Enable();
        }
    }
}
```

### Force Re-Injection

```csharp
public class ConfigurableController : MonoBehaviour
{
    [Inject(ForceInject = true)] 
    private IConfigService _config;
    
    // Will always inject, even if field already has a value
}
```

### Interface-Based Services

```csharp
// Define interface
public interface IScoreService
{
    int GetScore();
    void AddPoints(int points);
}

// Implement
public class ScoreService : IScoreService
{
    private int _score = 0;
    
    public int GetScore() => _score;
    public void AddPoints(int points) => _score += points;
}

// Register
RuntimeManager.BindService<IScoreService, ScoreService>();

// Use anywhere
public class UIManager : MonoBehaviour
{
    [Inject] private IScoreService _scoreService;
    
    private void Update()
    {
        scoreText.text = _scoreService.GetScore().ToString();
    }
}
```

### Service Composition

```csharp
public class GameService
{
    [Inject] private IScoreService _scoreService;
    [Inject] private IAchievementService _achievementService;
    [Inject] private EventDispatcher _events;
    
    public void CompleteLevel()
    {
        _scoreService.AddPoints(1000);
        _achievementService.Unlock("level_complete");
        _events.DispatchEvent("LevelComplete");
    }
}

// Create with all dependencies injected
var gameService = RuntimeManager.CreateWithInjection<GameService>();
```

## Testing

Dependency injection makes unit testing much easier:

### Mock Dependencies

```csharp
public class MockScoreService : IScoreService
{
    public int GetScore() => 100;
    public void AddPoints(int points) { }
}

[Test]
public void TestGameController()
{
    // Arrange
    var mockScore = new MockScoreService();
    var mockEvents = new MockEventDispatcher();
    
    var controller = new GameController(mockScore, mockEvents);
    
    // Act
    controller.CollectCoin();
    
    // Assert
    Assert.AreEqual(1, mockEvents.EventCount);
}
```

### Test Without RuntimeManager

```csharp
[Test]
public void TestPlayerController()
{
    // Create instance with mocks
    var player = new GameObject().AddComponent<PlayerController>();
    
    // Manually inject mocks
    var mockScore = new MockScoreService();
    typeof(PlayerController)
        .GetField("_scoreService", BindingFlags.NonPublic | BindingFlags.Instance)
        .SetValue(player, mockScore);
    
    // Test...
}
```

## Best Practices

### 1. Prefer Interfaces Over Concrete Types

```csharp
// Good
[Inject] private IScoreService _scoreService;

// Less flexible
[Inject] private ScoreService _scoreService;
```

### 2. Keep Dependencies Minimal

```csharp
// Good - only what's needed
public class PlayerController : MonoBehaviour
{
    [Inject] private IInputService _input;
    [Inject] private IMovementService _movement;
}

// Bad - too many dependencies
public class GodClass : MonoBehaviour
{
    [Inject] private EventDispatcher _events;
    [Inject] private ModalManager _modals;
    [Inject] private AudioManager _audio;
    [Inject] private NetworkManager _network;
    // ... 10 more dependencies
}
```

### 3. Use Optional Dependencies Wisely

```csharp
// Good - truly optional feature
[Inject(required: false)] 
private IAnalyticsService _analytics;

// Bad - critical dependency marked as optional
[Inject(required: false)] 
private EventDispatcher _events; // Don't do this!
```

### 4. Register Services During Bootstrap

```csharp
public class GameBootstrap : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RegisterServices()
    {
        // Register custom services early
        RuntimeManager.BindService<IScoreService, ScoreService>();
        RuntimeManager.BindService<IAchievementService, AchievementService>();
    }
}
```

### 5. Avoid Service Locator Pattern

```csharp
// Bad - service locator anti-pattern
public class BadController : MonoBehaviour
{
    private void DoSomething()
    {
        var service = RuntimeManager.GetService<IMyService>();
        service.DoWork();
    }
}

// Good - dependency injection
public class GoodController : MonoBehaviour
{
    [Inject] private IMyService _service;
    
    private void DoSomething()
    {
        _service.DoWork();
    }
}
```

## Migration Guide

### From Static Singletons

**Before:**
```csharp
public class OldManager : MonoBehaviour
{
    public static OldManager Instance { get; private set; }
    
    private void Awake()
    {
        Instance = this;
    }
}

// Usage
OldManager.Instance.DoSomething();
```

**After:**
```csharp
public class NewManager : RuntimeSubsystem
{
    public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
    {
        // Initialize...
        finishCallback?.Invoke(this);
    }
}

// Usage
public class MyScript : MonoBehaviour
{
    [Inject] private NewManager _manager;
    
    private void Start()
    {
        _manager.DoSomething();
    }
}
```

### From GetSubsystem

**Before:**
```csharp
var events = RuntimeManager.GetSubsystem<EventDispatcher>();
```

**After:**
```csharp
// Still works (backward compatible)
var events = RuntimeManager.GetSubsystem<EventDispatcher>();

// Or use new DI approach
[Inject] private EventDispatcher _events;
```

### Gradual Migration Strategy

1. **Phase 1**: Add `[Inject]` attributes to new scripts
2. **Phase 2**: Convert critical systems to use DI
3. **Phase 3**: Refactor existing code gradually
4. **Phase 4**: Remove old singleton patterns

## Common Patterns

### Game State Management

```csharp
public interface IGameState
{
    void Enter();
    void Exit();
}

public class MenuState : IGameState
{
    [Inject] private ModalManager _modals;
    
    public void Enter() => _modals.ShowModal("MainMenu");
    public void Exit() => _modals.CloseAllModals();
}

public class GameStateMachine : RuntimeSubsystem
{
    private IGameState _currentState;
    
    public void ChangeState<T>() where T : IGameState
    {
        _currentState?.Exit();
        _currentState = RuntimeManager.CreateWithInjection<T>();
        _currentState.Enter();
    }
}
```

### Command Pattern

```csharp
public interface ICommand
{
    void Execute();
}

public class SaveGameCommand : ICommand
{
    [Inject] private ISaveService _saveService;
    [Inject] private EventDispatcher _events;
    
    public void Execute()
    {
        _saveService.Save();
        _events.DispatchEvent("GameSaved");
    }
}

// Usage
var command = RuntimeManager.CreateWithInjection<SaveGameCommand>();
command.Execute();
```

### Factory Pattern

```csharp
public interface IEnemyFactory
{
    Enemy CreateEnemy(EnemyType type);
}

public class EnemyFactory : IEnemyFactory
{
    [Inject] private IPoolService _poolService;
    [Inject] private IConfigService _config;
    
    public Enemy CreateEnemy(EnemyType type)
    {
        var enemy = _poolService.Get<Enemy>();
        enemy.Configure(_config.GetEnemyConfig(type));
        return enemy;
    }
}
```

## Troubleshooting

### Circular Dependencies

```
Error: Circular dependency detected while resolving TypeA
```

**Solution:** Refactor to break the circular dependency. Consider using events or a mediator pattern.

### Service Not Found

```
Warning: Service not found: IMyService
```

**Solutions:**
1. Ensure the service is registered before it's requested
2. Check that RuntimeManager is initialized
3. Verify the service type matches exactly

### Injection Not Working

**Possible causes:**
1. RuntimeManager not initialized yet
2. Script instantiated after initialization (use `InjectOnAwake` component)
3. Typo in attribute spelling: `[Inject]` not `[Injected]`

## Performance Considerations

- Service resolution is cached, so repeated calls are fast
- Field injection happens only once per object
- Constructor injection creates new instances each time
- Transient services have higher overhead than singletons

## Summary

The new DI system provides:
- ✅ Clear dependency management
- ✅ Testable code
- ✅ Decoupled architecture
- ✅ Automatic injection
- ✅ Interface-based programming
- ✅ Backward compatibility with existing code

Start using it today for cleaner, more maintainable Unity projects!
