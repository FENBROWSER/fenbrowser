using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FenBrowser.DependencyInjection;

/// <summary>
/// Lightweight service container for dependency injection.
/// Provides a minimal abstraction over service registration and resolution.
/// </summary>
public interface IServiceContainer
{
    /// <summary>
    /// Registers a singleton service instance.
    /// </summary>
    void RegisterSingleton<TService>(TService instance) where TService : class;

    /// <summary>
    /// Registers a singleton service factory.
    /// </summary>
    void RegisterSingleton<TService>(Func<IServiceContainer, TService> factory) where TService : class;

    /// <summary>
    /// Registers a transient service factory (new instance each resolution).
    /// </summary>
    void RegisterTransient<TService>(Func<IServiceContainer, TService> factory) where TService : class;

    /// <summary>
    /// Registers a scoped service factory (same instance within a scope).
    /// </summary>
    void RegisterScoped<TService>(Func<IServiceContainer, TService> factory) where TService : class;

    /// <summary>
    /// Resolves a service instance.
    /// </summary>
    TService Resolve<TService>() where TService : class;

    /// <summary>
    /// Tries to resolve a service instance.
    /// </summary>
    bool TryResolve<TService>(out TService? service) where TService : class;

    /// <summary>
    /// Creates a new scope for scoped services.
    /// </summary>
    IServiceScope CreateScope();
}

/// <summary>
/// Represents a scope for scoped service lifetimes.
/// </summary>
public interface IServiceScope : IDisposable
{
    /// <summary>
    /// Gets the service provider for this scope.
    /// </summary>
    IServiceContainer Services { get; }
}

/// <summary>
/// Default implementation of a lightweight service container.
/// </summary>
public sealed class ServiceContainer : IServiceContainer
{
    private static readonly AsyncLocal<Stack<Type>?> _resolutionStack = new();
    private readonly Dictionary<Type, ServiceDescriptor> _services = new();
    private readonly object _lock = new();
    private readonly ServiceContainer? _parent;
    private readonly Dictionary<Type, object?> _scopedInstances = new();
    private bool _scopedInstancesDisposed;

    public ServiceContainer(ServiceContainer? parent = null)
    {
        _parent = parent;
    }

    public void RegisterSingleton<TService>(TService instance) where TService : class
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(
                ServiceLifetime.Singleton,
                _ => instance,
                this,
                instance);
        }
    }

    public void RegisterSingleton<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(
                ServiceLifetime.Singleton,
                factory,
                this,
                null);
        }
    }

    public void RegisterTransient<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(
                ServiceLifetime.Transient,
                factory,
                this,
                null);
        }
    }

    public void RegisterScoped<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(
                ServiceLifetime.Scoped,
                factory,
                this,
                null);
        }
    }

    public TService Resolve<TService>() where TService : class
    {
        if (TryResolve<TService>(out var service))
        {
            return service!;
        }

        throw new InvalidOperationException($"Service of type {typeof(TService).Name} is not registered.");
    }

    public bool TryResolve<TService>(out TService? service) where TService : class
    {
        var serviceType = typeof(TService);
        var descriptor = FindDescriptor(serviceType);
        if (descriptor == null)
        {
            service = null;
            return false;
        }

        using var resolution = EnterResolution(serviceType);
        service = CreateInstance<TService>(descriptor);
        return true;
    }

    public IServiceScope CreateScope()
    {
        return new ServiceScope(this);
    }

    private static ResolutionGuard EnterResolution(Type serviceType)
    {
        var stack = _resolutionStack.Value;
        if (stack == null)
        {
            stack = new Stack<Type>();
            _resolutionStack.Value = stack;
        }

        if (stack.Contains(serviceType))
        {
            var chain = stack.Reverse().Append(serviceType).Select(t => t.Name);
            throw new InvalidOperationException(
                $"Circular service dependency detected: {string.Join(" -> ", chain)}");
        }

        stack.Push(serviceType);
        return new ResolutionGuard(stack);
    }

    private ServiceDescriptor? FindDescriptor(Type serviceType)
    {
        lock (_lock)
        {
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
            _ => throw new InvalidOperationException($"Unknown service lifetime: {descriptor.Lifetime}")
        };
    }

    private TService? CreateScopedInstance<TService>(ServiceDescriptor descriptor) where TService : class
    {
        lock (_lock)
        {
            ThrowIfScopedInstancesDisposed();

            if (_scopedInstances.TryGetValue(typeof(TService), out var instance))
            {
                return (TService?)instance;
            }
        }

        // Factories may resolve other services. Do not execute them while holding
        // the container lock or a dependency chain can deadlock on re-entry.
        var newInstance = descriptor.Factory(this) as TService;

        lock (_lock)
        {
            ThrowIfScopedInstancesDisposed();

            if (_scopedInstances.TryGetValue(typeof(TService), out var existing))
            {
                if (!ReferenceEquals(existing, newInstance) && newInstance is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                return (TService?)existing;
            }

            _scopedInstances[typeof(TService)] = newInstance;
            return newInstance;
        }
    }

    private void DisposeScopedInstances()
    {
        object?[] instances;

        lock (_lock)
        {
            if (_scopedInstancesDisposed)
            {
                return;
            }

            _scopedInstancesDisposed = true;
            instances = _scopedInstances.Values.ToArray();
            _scopedInstances.Clear();
        }

        // Dispose outside the lock because Dispose implementations can call back
        // into the container or other services.
        for (var i = instances.Length - 1; i >= 0; i--)
        {
            if (instances[i] is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private void ThrowIfScopedInstancesDisposed()
    {
        if (_scopedInstancesDisposed)
        {
            throw new ObjectDisposedException(nameof(ServiceContainer));
        }
    }

    private readonly struct ResolutionGuard : IDisposable
    {
        private readonly Stack<Type>? _stack;

        public ResolutionGuard(Stack<Type> stack)
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
                _resolutionStack.Value = null;
            }
        }
    }

    private sealed class ServiceDescriptor
    {
        private readonly object _singletonLock = new();
        private object? _instance;
        private bool _singletonCreated;

        public ServiceLifetime Lifetime { get; }
        public Func<IServiceContainer, object?> Factory { get; }
        public ServiceContainer Owner { get; }

        public ServiceDescriptor(
            ServiceLifetime lifetime,
            Func<IServiceContainer, object?> factory,
            ServiceContainer owner,
            object? instance)
        {
            Lifetime = lifetime;
            Factory = factory;
            Owner = owner;
            _instance = instance;
            _singletonCreated = instance != null;
        }

        public object? GetOrCreateSingleton()
        {
            if (_singletonCreated)
            {
                return _instance;
            }

            lock (_singletonLock)
            {
                if (!_singletonCreated)
                {
                    _instance = Factory(Owner);
                    _singletonCreated = true;
                }

                return _instance;
            }
        }
    }

    private enum ServiceLifetime
    {
        Singleton,
        Transient,
        Scoped
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
            _scopedContainer.DisposeScopedInstances();
        }
    }
}