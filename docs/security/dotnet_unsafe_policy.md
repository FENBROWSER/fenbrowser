# .NET Unsafe Policy for FenJS

Default for JS core projects:
- `AllowUnsafeBlocks=false`
- nullable enabled
- warnings as errors
- analyzers enabled

Unsafe/native code is disallowed in JS core projects:
- FenBrowser.Js.Lexer
- FenBrowser.Js.Parser
- FenBrowser.Js.Ast
- FenBrowser.Js.Bytecode
- FenBrowser.Js.Runtime
- FenBrowser.Js.Heap
- FenBrowser.Js.Objects
- FenBrowser.Js.Builtins
- FenBrowser.Js.Host

Unsafe/native code is allowed only in isolated platform/interop layers:
- FenBrowser.Platform
- FenBrowser.NativeInterop
- FenBrowser.Rendering.SkiaBridge
- future experimental JIT memory manager

Every unsafe/native boundary must document:
- JS reachability
- owner/lifetime model
- navigation/frame destruction safety
- origin/sandbox constraints
- GC/reentrancy interaction
