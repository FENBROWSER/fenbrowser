#!/bin/bash
# Apply annexB fixes to FenBrowser.Js
set -e

REPO="C:/Users/udayk/Videos/fenbrowser-test"

echo "=== Reverting any pre-existing changes ==="
cd "$REPO"
git checkout -- FenBrowser.Js/Builtins/TemporalStub.cs 2>/dev/null || true

echo "=== Building baseline ==="
dotnet build FenBrowser.Js/FenBrowser.Js.csproj -c Release

echo "=== Fix 1: trimLeft/trimRight in StringBuiltin.cs ==="
# Replace the trim section to add trimLeft/trimRight aliases
python3 -c "
import re
with open('FenBrowser.Js/Builtins/StringBuiltin.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old = '''        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, \"trim\", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).Trim()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, \"trimStart\", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).TrimStart()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, \"trimEnd\", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).TrimEnd()));'''

new = '''        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, \"trim\", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).Trim()));

        // trimStart / trimLeft and trimEnd / trimRight: Annex B.2.2.1 aliases
        // sharing the same function object per spec.
        var trimStartFn = new NativeFunctionObject(\"trimStart\",
            (thisValue, _) => JsValue.FromString(RequireString(capturedCtx, thisValue).TrimStart()), length: 0);
        var trimStartFnHandle = heap.AllocateObject(trimStartFn, AllocationSite.Current());
        trimStartFn.SetPrototype(capturedCtx.GetObjectPrototype());
        var callHandle = capturedCtx.GetFunctionCallMethod();
        trimStartFn.SetProperty(\"call\", JsValue.FromObject(callHandle));
        heap.WriteBarrier(trimStartFnHandle, callHandle);
        protoObj.DefineOwnProperty(\"trimStart\", new JsPropertyDescriptor(JsValue.FromObject(trimStartFnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, trimStartFnHandle);
        protoObj.DefineOwnProperty(\"trimLeft\", new JsPropertyDescriptor(JsValue.FromObject(trimStartFnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, trimStartFnHandle);

        // trimEnd / trimRight
        var trimEndFn = new NativeFunctionObject(\"trimEnd\",
            (thisValue, _) => JsValue.FromString(RequireString(capturedCtx, thisValue).TrimEnd()), length: 0);
        var trimEndFnHandle = heap.AllocateObject(trimEndFn, AllocationSite.Current());
        trimEndFn.SetPrototype(capturedCtx.GetObjectPrototype());
        trimEndFn.SetProperty(\"call\", JsValue.FromObject(callHandle));
        heap.WriteBarrier(trimEndFnHandle, callHandle);
        protoObj.DefineOwnProperty(\"trimEnd\", new JsPropertyDescriptor(JsValue.FromObject(trimEndFnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, trimEndFnHandle);
        protoObj.DefineOwnProperty(\"trimRight\", new JsPropertyDescriptor(JsValue.FromObject(trimEndFnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, trimEndFnHandle);'''

assert old in content, 'StringBuiltin.cs: old pattern not found'
content = content.replace(old, new)
with open('FenBrowser.Js/Builtins/StringBuiltin.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print('StringBuiltin.cs: trimLeft/trimRight added')
"

echo "=== Building after fix 1 ==="
dotnet build FenBrowser.Js/FenBrowser.Js.csproj -c Release 2>&1 | tail -5

echo "Done!"
