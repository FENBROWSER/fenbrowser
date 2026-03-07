# WebIDL — Binding Generator and DOM Bindings

## Source Files

### Generator Infrastructure
- `FenBrowser.Core/WebIDL/WebIdlParser.cs` — recursive-descent IDL parser
- `FenBrowser.Core/WebIDL/WebIdlBindingGenerator.cs` — C# binding code emitter
- `FenBrowser.WebIdlGen/Program.cs` — CLI tool (`webidlgen`)

### IDL Source Files (`FenBrowser.Core/WebIDL/Idl/`)
| File | Interface |
|------|-----------|
| `EventTarget.idl` | EventTarget, EventListenerOptions, AddEventListenerOptions |
| `Event.idl` | Event, CustomEvent |
| `Node.idl` | Node |
| `Element.idl` | Element, ShadowRootInit, ShadowRootMode, SlotAssignmentMode |
| `Document.idl` | Document, GlobalEventHandlers mixin, DocumentAndElementEventHandlers mixin |
| `HTMLElement.idl` | HTMLElement, HTMLOrSVGElement mixin, ElementContentEditable mixin |
| `Window.idl` | Window, WindowEventHandlers mixin, ScrollOptions, ScrollBehavior |
| `NodeList.idl` | NodeList, HTMLCollection |
| `Attr.idl` | Attr, NamedNodeMap |
| `CharacterData.idl` | CharacterData, Text, CDATASection, Comment, ProcessingInstruction, DocumentType, DocumentFragment, ShadowRoot |

### Generated Binding Files (`FenBrowser.FenEngine/Bindings/Generated/`)
One `*Binding.g.cs` file per IDL interface/dictionary/enum. Auto-generated — do not edit.
Regenerate with:
```
dotnet run --project FenBrowser.WebIdlGen -- \
  --idl FenBrowser.Core/WebIDL/Idl \
  --out FenBrowser.FenEngine/Bindings/Generated
```

## MSBuild Integration
`FenBrowser.FenEngine.csproj` runs `webidlgen` in a `BeforeBuild` target to keep bindings
in sync with IDL sources automatically.

## How to Wire a Real DOM Object

1. At engine startup, register your DOM wrapper:
```csharp
// In your engine initialization code:
NodeBinding.UnwrapImpl = (fenObj) => fenObj.Get("[[NativeRef]]").AsObject() as Node;
NodeBinding.WrapImpl   = (node, fenObj) => fenObj.Set("[[NativeRef]]", FenValue.FromObject(new NativeRefWrapper(node)));
```
2. Call `NodeBinding.CreatePrototype(objectProto)` to get the prototype object.
3. Call `NodeBinding.CreateConstructor(proto)` to get the constructor function.
4. Register both on the global object.
