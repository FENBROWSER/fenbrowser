# FenJS Engine Scope

FenJS is a standalone ECMAScript engine embedded by FenBrowser.

FenJS owns:
- lexer
- parser
- AST and early errors
- bytecode compiler and verifier
- interpreter
- JS value model
- logical JS heap and handle scopes
- object model and builtins
- promises and modules
- realms
- host binding interface
- test262 harness and diagnostics

FenBrowser owns:
- DOM/CSSOM/layout/rendering
- event loop integration at browser boundary
- networking/storage/permissions
- process model/sandboxing
- Web APIs and navigation lifecycle

Boundary rules:
- no DOM/layout/native host objects inside `JsValue`
- host interop only through explicit handles and host hooks
- JS core remains platform-agnostic and safe-by-default

Standalone shell:
- `FenBrowser.Js.Shell` runs engine smoke checks without browser host integration
- `--eval [code]` and `--file <path>` execute through the bytecode verifier and interpreter
- empty `--eval` input is valid and returns `undefined`
