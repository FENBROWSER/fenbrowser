using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.DependencyInjection;

/// <summary>
/// Lightweight service container for dependency injection.
/// Registrations become immutable as soon as resolution or scope creation starts,
/// which keeps the service graph deterministic for long-lived browser components.
/// </summary>
public interface IServiceContainer
{
    void RegisterSingleton<TService>(TService instance) where TService : class;
    void RegisterSingleton<TService>(Func<IServiceContainer, TService> factory) where TService : class;
    void RegisterTransient<TService>(Func<IServiceContainer, TService> factory) where TService : class;
    void RegisterScoped<TService>(Func<IServiceContainer, TService> factory) where TService : class;
    TService Resolve<TService>() where TService : class;
    bool TryResolve<TService>(out TService? service) where TService : class;
    IServiceScope CreateScope();
}

public interface IServiceScope : IDisposable
{
    IServiceContainer Services { get; }
}

public sealed class ServiceContainer : IServiceContainer, IDisposable
{
    private static readonly AsyncLocal<Stack<ResolutionFrame>?> ResolutionStack = new();
    private static readonly object SingletonWaitGraphLock = new();
    private static readonly Dictionary<int, int> SingletonWaitGraph = new();

    private readonly Dictionary<Type, ServiceDescriptor> _services = new();
    private readonly object _lock = new();
    private readonly ServiceContainer? _parent;
    private readonly Dictionary<Type, object> _scopedInstances = new();
    private readonly List<IDisposable> _ownedSingletonDisposables = new();
    private bool _registrationsFrozen;
    private bool _disposed;

    public ServiceContainer(ServiceContainer? parent = null)
    {
        _parent = parent;
    }

    public void RegisterSingleton<TService>(TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);

        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            ThrowIfRegistrationsFrozenNoLock();
            _services[typeof(TService)] = new ServiceDescriptor(
                typeof(TService),
                ServiceLifetime.Singleton,
                _ => instance,
                this,
                instance,
                ownsProvidedInstance: false);
        }
    }

    public void RegisterSingleton<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        RegisterFactory(
            typeof(TService),
            ServiceLifetime.Singleton,
            container => factory(container));
    }

    public void RegisterTransient<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        RegisterFactory(
            typeof(TService),
            ServiceLifetime.Transient,
            container => factory(container));
    }

    public void RegisterScoped<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        RegisterFactory(
            typeof(TService),
            ServiceLifetime.Scoped,
            container => factory(container));
    }

    private void RegisterFactory(
        Type serviceType,
        ServiceLifetime lifetime,
        Func<IServiceContainer, object?> factory)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            ThrowIfRegistrationsFrozenNoLock();
            _services[serviceType] = new ServiceDescriptor(
                serviceType,
                lifetime,
                factory,
                this,
                instance: null,
                ownsProvidedInstance: false);
        }
    }

    public TService Resolve<TService>() where TService : class
    {
        if (TryResolve<TService>(out var service))
        {
            return service!;
        }

        throw new InvalidOperationException(
            $"Service of type {typeof(TService).FullName} is not registered.");
    }

    public bool TryResolve<TService>(out TService? service) where TService : class
    {
        FreezeRegistrations();

        var serviceType = typeof(TService);
        var descriptor = FindDescriptor(serviceType);
        if (descriptor == null)
        {
            service = null;
            return false;
        }

        using var resolution = EnterResolution(serviceType);
        service = CreateInstance<TService>(descriptor);
        if (service == null)
        {
            throw new InvalidOperationException(
                $"Factory registered for {serviceType.FullName} returned null or an incompatible service instance.");
        }

        return true;
    }

    public IServiceScope CreateScope()
    {
        FreezeRegistrations();
        return new ServiceScope(this);
    }

    private void FreezeRegistrations()
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            _registrationsFrozen = true;
        }
    }

    private ResolutionGuard EnterResolution(Type serviceType)
    {
        var stack = ResolutionStack.Value;
        if (stack == null)
        {
            stack = new Stack<ResolutionFrame>();
            ResolutionStack.Value = stack;
        }

        var frame = new ResolutionFrame(this, serviceType);
        if (stack.Contains(frame))
        {
            var chain = stack
                .Reverse()
                .Append(frame)
                .Select(entry => entry.ServiceType.Name);
            throw new InvalidOperationException(
                $"Circular service dependency detected: {string.Join(" -> ", chain)}");
        }

        stack.Push(frame);
        return new ResolutionGuard(stack);
    }

    private ServiceDescriptor? FindDescriptor(Type serviceType)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (_services.TryGetValue(serviceType, out var descriptor))
            {
                return descriptor;
            }
        }

        return _parent?.FindDescriptor(serviceType);
    }

    private TService? CreateInstance<TService>(ServiceDescriptor descriptor) where TService : class
    {
        return descriptor.Lifetime switch
        {
            ServiceLifetime.Singleton => descriptor.GetOrCreateSingleton() as TService,
            ServiceLifetime.Transient => descriptor.Factory(this) as TService,
            ServiceLifetime.Scoped => CreateScopedInstance<TService>(descriptor),
            _ => throw new InvalidOperationException(
                $"Unknown service lifetime: {descriptor.Lifetime}")
        };
    }

    private TService? CreateScopedInstance<TService>(ServiceDescriptor descriptor) where TService : class
    {
        // A scoped service resolved from the root has no bounded lifetime. Reject it
        // rather than silently turning it into process-lifetime state. Singleton
        // factories execute against their registration owner, so singleton -> scoped
        // captive dependencies are rejected by the same rule.
        if (_parent == null)
        {
            throw new InvalidOperationException(
                $"Scoped service {typeof(TService).FullName} must be resolved from an IServiceScope.");
        }

        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (_scopedInstances.TryGetValue(typeof(TService), out var existing))
            {
                if (existing is not TService typedExisting)
                {
                    throw new InvalidOperationException(
                        $"Cached scoped instance for {typeof(TService).FullName} is incompatible.");
                }

                return typedExisting;
            }
        }

        // Factories can resolve arbitrary dependencies. Never execute them while the
        // structural container lock is held.
        var created = descriptor.Factory(this);
        if (created is not TService newInstance)
        {
            throw new InvalidOperationException(
                $"Scoped factory registered for {typeof(TService).FullName} returned null or an incompatible service instance.");
        }

        TService? result = null;
        IDisposable? disposeAfterLock = null;
        Exception? failure = null;

        lock (_lock)
        {
            if (_disposed)
            {
                disposeAfterLock = newInstance as IDisposable;
                failure = new ObjectDisposedException(nameof(ServiceContainer));
            }
            else if (_scopedInstances.TryGetValue(typeof(TService), out var existing))
            {
                if (existing is not TService typedExisting)
                {
                    disposeAfterLock = newInstance as IDisposable;
                    failure = new InvalidOperationException(
                        $"Cached scoped instance for {typeof(TService).FullName} is incompatible.");
                }
                else
                {
                    result = typedExisting;
                    if (!ReferenceEquals(existing, newInstance))
                    {
                        disposeAfterLock = newInstance as IDisposable;
                    }
                }
            }
            else
            {
                _scopedInstances[typeof(TService)] = newInstance;
                result = newInstance;
            }
        }

        // User disposal code is arbitrary and may re-enter the container.
        disposeAfterLock?.Dispose();
        if (failure != null)
        {
            throw failure;
        }

        return result;
    }

    private void TrackFactoryCreatedSingleton(object instance)
    {
        if (instance is not IDisposable disposable)
        {
            lock (_lock)
            {
                ThrowIfDisposedNoLock();
            }
            return;
        }

        var disposeBecauseContainerClosed = false;
        lock (_lock)
        {
            if (_disposed)
            {
                disposeBecauseContainerClosed = true;
            }
            else
            {
                _ownedSingletonDisposables.Add(disposable);
            }
        }

        if (disposeBecauseContainerClosed)
        {
            disposable.Dispose();
            throw new ObjectDisposedException(nameof(ServiceContainer));
        }
    }

    private static SingletonWaitGuard EnterSingletonWait(int ownerThreadId)
    {
        var waiterThreadId = Environment.CurrentManagedThreadId;
        if (waiterThreadId == ownerThreadId)
        {
            throw new InvalidOperationException(
                "Circular singleton activation detected on the same thread.");
        }

        lock (SingletonWaitGraphLock)
        {
            var current = ownerThreadId;
            var visited = new HashSet<int>();
            while (visited.Add(current) && SingletonWaitGraph.TryGetValue(current, out var next))
            {
                if (next == waiterThreadId)
                {
                    throw new InvalidOperationException(
                        "Cross-thread circular singleton dependency detected.");
                }

                current = next;
            }

            SingletonWaitGraph[waiterThreadId] = ownerThreadId;
        }

        return new SingletonWaitGuard(waiterThreadId);
    }

    private void ThrowIfDisposedNoLock()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ServiceContainer));
        }
    }

    private void ThrowIfRegistrationsFrozenNoLock()
    {
        if (_registrationsFrozen)
        {
            throw new InvalidOperationException(
                "Service registrations are immutable after resolution or scope creation begins. " +
                "Create a new container to change the service graph.");
        }
    }

    public void Dispose()
    {
        object[] scopedInstances;
        IDisposable[] singletonDisposables;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registrationsFrozen = true;
            scopedInstances = _scopedInstances.Values.ToArray();
            _scopedInstances.Clear();
            singletonDisposables = _ownedSingletonDisposables.ToArray();
            _ownedSingletonDisposables.Clear();
            _services.Clear();
        }

        List<Exception>? failures = null;

        for (var index = scopedInstances.Length - 1; index >= 0; index--)
        {
            if (scopedInstances[index] is not IDisposable disposable)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        for (var index = singletonDisposables.Length - 1; index >= 0; index--)
        {
            try
            {
                singletonDisposables[index].Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more container-owned services failed during disposal.",
                failures);
        }
    }

    private readonly record struct ResolutionFrame(ServiceContainer Container, Type ServiceType);

    private readonly struct ResolutionGuard : IDisposable
    {
        private readonly Stack<ResolutionFrame>? _stack;

        public ResolutionGuard(Stack<ResolutionFrame> stack)
        {
            _stack = stack;
        }

        public void Dispose()
        {
            if (_stack == null || _stack.Count == 0)
            {
                return;
            }

            _stack.Pop();
            if (_stack.Count == 0)
            {
                ResolutionStack.Value = null;
            }
        }
    }

    private readonly struct SingletonWaitGuard : IDisposable
    {
        private readonly int _waiterThreadId;

        public SingletonWaitGuard(int waiterThreadId)
        {
            _waiterThreadId = waiterThreadId;
        }

        public void Dispose()
        {
            if (_waiterThreadId == 0)
            {
                return;
            }

            lock (SingletonWaitGraphLock)
            {
                SingletonWaitGraph.Remove(_waiterThreadId);
            }
        }
    }

    private sealed class ServiceDescriptor
    {
        private readonly object _singletonStateLock = new();
        private object? _instance;
        private SingletonActivationState _singletonState;
        private int _creatingThreadId;
        private TaskCompletionSource<object?>? _creationCompletion;
        private ExceptionDispatchInfo? _creationFailure;

        public Type ServiceType { get; }
        public ServiceLifetime Lifetime { get; }
        public Func<IServiceContainer, object?> Factory { get; }
        public ServiceContainer Owner { get; }
        public bool OwnsProvidedInstance { get; }

        public ServiceDescriptor(
            Type serviceType,
            ServiceLifetime lifetime,
            Func<IServiceContainer, object?> factory,
            ServiceContainer owner,
            object? instance,
            bool ownsProvidedInstance)
        {
            ServiceType = serviceType;
            Lifetime = lifetime;
            Factory = factory;
            Owner = owner;
            OwnsProvidedInstance = ownsProvidedInstance;
            _instance = instance;
            _singletonState = instance != null
                ? SingletonActivationState.Created
                : SingletonActivationState.Uninitialized;
        }

        public object? GetOrCreateSingleton()
        {
            Task<object?>? waitTask = null;
            var creatorThreadId = 0;
            var ownsCreation = false;

            lock (_singletonStateLock)
            {
                switch (_singletonState)
                {
                    case SingletonActivationState.Created:
                        return _instance;

                    case SingletonActivationState.Failed:
                        _creationFailure!.Throw();
                        throw new InvalidOperationException("Unreachable singleton activation state.");

                    case SingletonActivationState.Uninitialized:
                        _singletonState = SingletonActivationState.Creating;
                        _creatingThreadId = Environment.CurrentManagedThreadId;
                        _creationCompletion = new TaskCompletionSource<object?>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        ownsCreation = true;
                        break;

                    case SingletonActivationState.Creating:
                        creatorThreadId = _creatingThreadId;
                        waitTask = _creationCompletion!.Task;
                        break;
                }
            }

            if (!ownsCreation)
            {
                using var waitGuard = EnterSingletonWait(creatorThreadId);
                return waitTask!.GetAwaiter().GetResult();
            }

            try
            {
                var created = Factory(Owner);
                if (created == null)
                {
                    throw new InvalidOperationException(
                        $"Singleton factory registered for {ServiceType.FullName} returned null.");
                }

                Owner.TrackFactoryCreatedSingleton(created);

                TaskCompletionSource<object?> completion;
                lock (_singletonStateLock)
                {
                    _instance = created;
                    _singletonState = SingletonActivationState.Created;
                    _creatingThreadId = 0;
                    completion = _creationCompletion!;
                }

                completion.TrySetResult(created);
                return created;
            }
            catch (Exception ex)
            {
                var failure = ExceptionDispatchInfo.Capture(ex);
                TaskCompletionSource<object?> completion;
                lock (_singletonStateLock)
                {
                    _creationFailure = failure;
                    _singletonState = SingletonActivationState.Failed;
                    _creatingThreadId = 0;
                    completion = _creationCompletion!;
                }

                completion.TrySetException(ex);
                failure.Throw();
                throw;
            }
        }
    }

    private enum ServiceLifetime
    {
        Singleton,
        Transient,
        Scoped
    }

    private enum SingletonActivationState
    {
        Uninitialized,
        Creating,
        Created,
        Failed
    }

    private sealed class ServiceScope : IServiceScope
    {
        private readonly ServiceContainer _scopedContainer;
        private bool _disposed;

        public ServiceScope(ServiceContainer container)
        {
            _scopedContainer = new ServiceContainer(container);
            Services = _scopedContainer;
        }

        public IServiceContainer Services { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scopedContainer.Dispose();
        }
    }
}
