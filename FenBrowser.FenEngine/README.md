# FenEngine

> A modular, security-first JavaScript engine for .NET

## Overview

FenEngine is a standalone JavaScript engine designed with security and modularity as core principles. Built to avoid the architectural pitfalls and CVE patterns found in V8 and SpiderMonkey.

## Design Principles

### 🛡️ Security First

- **No JIT compilation** - Bytecode VM execution only, eliminates JIT-related CVEs
- **Deny by default** - Explicit permissions required for all operations
- **Resource limits** - Hard limits on stack depth, memory, execution time
- **Safe errors** - No information leakage in error messages
- **Pure managed code** - No unsafe blocks, no native interop

### 🔧 Modular Architecture

- **Interface-based** - Easy to extend without modifying core
- **Plugin system** - Add new types/APIs via registries
- **Separation of concerns** - Core, Security, Executor, Native APIs isolated
- **Independent testing** - Each module testable in isolation

### 📦 Extensible

- Add new types (Symbol, BigInt) by implementing `IValue`
- Add new APIs (fetch, Promise) by implementing `INativeBinding`
- Add new language features via `ExecutorRegistry`
- No core modifications needed for extensions

## Architecture

```
FenBrowser.FenEngine/
├── Core/
│   ├── Interfaces/
│   │   ├── IValue.cs           # Value type interface
│   │   └── IObject.cs          # Object interface
│   ├── Types/
│   │   ├── FenValue.cs         # Basic value implementation
│   │   ├── FenObject.cs        # Object implementation
│   │   └── FenFunction.cs      # Function implementation
│   ├── FenRuntime.cs           # Global runtime
│   └── ExecutionContext.cs     # Per-execution context
├── Security/
│   ├── IPermissionManager.cs   # Permission interface
│   ├── IResourceLimits.cs      # Resource limits interface
│   └── PermissionManager.cs    # Permission implementation
└── Errors/
    └── FenError.cs             # Type-safe error hierarchy
```

## Features

### Current Capabilities

- ✅ Variables and assignments
- ✅ Basic operators (+, -, \*, /)
- ✅ String operations
- ✅ console.log
- ✅ Type coercion
- ✅ Safe error handling

### Security Features

- ✅ 20+ granular permissions (Console, DOM, Network, Storage, etc.)
- ✅ Resource limits (stack, memory, time)
- ✅ Security violation logging
- ✅ Execution timeout enforcement
- ✅ Call stack depth limiting

### Roadmap

- [ ] Functions (user-defined)
- [ ] Control flow (if/else, loops)
- [ ] Arrays and objects
- [ ] Promises
- [ ] async/await
- [ ] DOM APIs
- [ ] fetch/XHR

## Usage

### Basic Example

```csharp
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Security;

// Create runtime with permissions
var runtime = new FenRuntime();

// Execute JavaScript
var result = runtime.ExecuteSimple("var x = 5; console.log(x);");
```

### With Security

```csharp
using FenBrowser.FenEngine.Security;

// Create execution context with custom permissions
var permissions = new PermissionManager(
    JsPermissions.Console | JsPermissions.DomRead
);

var limits = new DefaultResourceLimits();
var context = new ExecutionContext(permissions, limits);

// Execute with constraints
// - Only console and DOM read allowed
// - 5 second timeout
// - Stack depth limited to 100
```

### Permission Model

```csharp
// Available permissions
[Flags]
public enum JsPermissions
{
    Console,            // console.log
    DomRead,            // document.getElementById
    DomWrite,           // innerHTML
    DomEvents,          // addEventListener
    Fetch,              // fetch()
    LocalStorage,       // localStorage
    SetTimeout,         // setTimeout
    // ... and more
}

// Check permission
if (context.Permissions.Check(JsPermissions.Fetch))
{
    // Allowed
}
```

### Resource Limits

```csharp
public interface IResourceLimits
{
    int MaxCallStackDepth { get; }      // Default: 100
    TimeSpan MaxExecutionTime { get; }  // Default: 5s
    long MaxTotalMemory { get; }        // Default: 50MB
    int MaxStringLength { get; }        // Default: 1MB
    int MaxArrayLength { get; }         // Default: 100K
    int MaxObjectProperties { get; }    // Default: 10K
}
```

## Security Guarantees

### Memory Safety

- ✅ Pure C# managed memory
- ✅ No unsafe code blocks
- ✅ No native interop (no P/Invoke)
- ✅ Automatic garbage collection

### DoS Prevention

- ✅ Execution timeout (prevents infinite loops)
- ✅ Stack depth limit (prevents stack overflow)
- ✅ Memory limit (prevents memory exhaustion)
- ✅ String/array size limits (prevents allocation bombs)

### Information Security

- ✅ Safe error messages (no internal details)
- ✅ Permission violations logged
- ✅ Audit trail available
- ✅ No stack traces to JavaScript

## CVE Lessons Applied

| V8/SpiderMonkey CVE            | FenEngine Prevention            |
| ------------------------------ | ------------------------------- |
| CVE-2020-6383 (Type confusion) | Strong typing via interfaces    |
| CVE-2021-30551 (OOB access)    | Bounds checking, managed arrays |
| CVE-2019-5869 (JIT bug)        | No JIT compilation              |
| CVE-2020-15999 (Native lib)    | No native dependencies          |

## Testing

```bash
# Build FenEngine
dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj

# Run tests (future)
dotnet test FenBrowser.FenEngine.Tests/
```

## Contributing

When adding features:

1. **Implement interfaces** - Don't modify core
2. **Use registries** - Register new types/APIs
3. **Add tests** - Unit tests for new functionality
4. **Check security** - Ensure no new attack vectors
5. **Document** - Update README and API docs

## License

Part of FenBrowser project.

## Why FenEngine?

**Existing engines (V8, SpiderMonkey, JavaScriptCore):**

- ❌ Complex (millions of lines)
- ❌ CVE history (JIT, type confusion, OOB)
- ❌ Monolithic (hard to audit)
- ❌ Implicit trust model

**FenEngine:**

- ✅ Simple (auditable)
- ✅ Security-first design
- ✅ Modular (extensible)
- ✅ Explicit permissions

**Trade-off:** Performance for safety and simplicity. FenEngine is not designed to compete with V8's speed, but to provide a secure, maintainable alternative for browser and embedded scenarios.
