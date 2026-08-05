using System;
using System.Collections.Generic;

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
    private readonly Dictionary<Type, ServiceDescriptor> _services = new();
    private readonly object _lock = new();
    private readonly ServiceContainer? _parent;
    private readonly Dictionary<Type, object?> _scopedInstances = new();

    public ServiceContainer(ServiceContainer? parent = null)
    {
        _parent = parent;
    }

    public void RegisterSingleton<TService>(TService instance) where TService : class
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(ServiceLifetime.Singleton, _ => instance, instance);
        }
    }

    public void RegisterSingleton<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(ServiceLifetime.Singleton, factory, null);
        }
    }

    public void RegisterTransient<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(ServiceLifetime.Transient, factory, null);
        }
    }

    public void RegisterScoped<TService>(Func<IServiceContainer, TService> factory) where TService : class
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (_lock)
        {
            _services[typeof(TService)] = new ServiceDescriptor(ServiceLifetime.Scoped, factory, null);
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
        var type = typeof(TService);
        lock (_lock)
        {
            if (_services.TryGetValue(type, out var descriptor))
            {
                service = CreateInstance<TService>(descriptor);
                return true;
            }
        }

        if (_parent != null)
        {
            return _parent.TryResolve(out service);
        }

        service = null;
        return false;
    }

    public IServiceScope CreateScope()
    {
        return new ServiceScope(this);
    }

    private TService? CreateInstance<TService>(ServiceDescriptor descriptor) where TService : class
    {
        return descriptor.Lifetime switch
        {
            ServiceLifetime.Singleton => descriptor.Instance as TService ?? descriptor.Factory(this) as TService,
            ServiceLifetime.Transient => descriptor.Factory(this) as TService,
            ServiceLifetime.Scoped => CreateScopedInstance<TService>(descriptor),
            _ => throw new InvalidOperationException($"Unknown service lifetime: {descriptor.Lifetime}")
        };
    }

    private TService? CreateScopedInstance<TService>(ServiceDescriptor descriptor) where TService : class
    {
        lock (_lock)
        {
            if (_scopedInstances.TryGetValue(typeof(TService), out var instance))
            {
                return (TService?)instance;
            }
            var newInstance = descriptor.Factory(this) as TService;
            _scopedInstances[typeof(TService)] = newInstance!;
            return newInstance;
        }
    }

    private sealed class ServiceDescriptor
    {
        public ServiceLifetime Lifetime { get; }
        public Func<IServiceContainer, object?> Factory { get; }
        public object? Instance { get; }

        public ServiceDescriptor(ServiceLifetime lifetime, Func<IServiceContainer, object?> factory, object? instance)
        {
            Lifetime = lifetime;
            Factory = factory;
            Instance = instance;
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
        private readonly ServiceContainer _container;

        public ServiceScope(ServiceContainer container)
        {
            _container = container;
            Services = new ServiceContainer(container);
        }

        public IServiceContainer Services { get; }

        public void Dispose()
        {
            // Scoped instances are cleaned up when scope is disposed
        }
    }
}