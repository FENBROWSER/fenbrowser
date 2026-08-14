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
        if (service == null)
        {
            throw new InvalidOperationException(
                $"Factory registered for {serviceType.FullName} returned null or an incompatible service instance.");
        }

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
                if (instance is not TService typedInstance)
                {
                    throw new InvalidOperationException(
                        $"Cached scoped instance for {typeof(TService).FullName} is null or incompatible.");
                }

                return typedInstance;
            }
        }

        // Factories may resolve other services. Do not execute them while holding
        // the container lock or a dependency chain can deadlock on re-entry.
        var created = descriptor.Factory(this);
        if (created is not TService newInstance)
        {
            throw new InvalidOperationException(
                $"Scoped factory registered for {typeof(TService).FullName} returned null or an incompatible service instance.");
        }

        lock (_lock)
        {
            ThrowIfScopedInstancesDisposed();

            if (_scopedInstances.TryGetValue(typeof(TService), out var existing))
            {
                if (!ReferenceEquals(existing, newInstance) && newInstance is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                if (existing is not TService typedExisting)
                {
                    throw new InvalidOperationException(
                        $"Cached scoped instance for {typeof(TService).FullName} is null or incompatible.");
                }

                return typedExisting;
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
