// FenValue — standalone JS value struct used by browser integration (BrowserApi.cs).
// Stores primitive values and object references directly. No longer depends on the
// legacy FenRuntime; complex ToPrimitive/coercion paths were only used by the legacy
// interpreter and are not needed by the WebDriver/browser API layer.
using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.Core
{
    public struct FenValue : IValue
    {
        private Interfaces.ValueType _type;
        private double _numberValue;
        private object _refValue;

        // Static singletons
        public static readonly FenValue Undefined = new() { _type = Interfaces.ValueType.Undefined };
        public static readonly FenValue Null = new() { _type = Interfaces.ValueType.Null };
        public static readonly FenValue True = new() { _type = Interfaces.ValueType.Boolean, _numberValue = 1.0 };
        public static readonly FenValue False = new() { _type = Interfaces.ValueType.Boolean, _numberValue = 0.0 };

        public Interfaces.ValueType Type { readonly get => _type; set => _type = value; }

        // Static factories
        public static FenValue FromBoolean(bool value) =>
            new() { _type = Interfaces.ValueType.Boolean, _numberValue = value ? 1.0 : 0.0 };

        public static FenValue FromNumber(double value) =>
            new() { _type = Interfaces.ValueType.Number, _numberValue = value };

        public static FenValue FromString(string value) =>
            new() { _type = Interfaces.ValueType.String, _refValue = value ?? string.Empty };

        public static FenValue FromObject(IObject obj) =>
            new() { _type = Interfaces.ValueType.Object, _refValue = obj };

        public static FenValue FromFunction(FenFunction func) =>
            new() { _type = Interfaces.ValueType.Function, _refValue = func };

        public static FenValue FromSymbol(JsSymbol symbol) =>
            new() { _type = Interfaces.ValueType.Symbol, _refValue = symbol };

        public static FenValue FromBigInt(JsBigInt bigInt) =>
            new() { _type = Interfaces.ValueType.BigInt, _refValue = bigInt };

        public static FenValue FromException(Exception ex) =>
            new() { _type = Interfaces.ValueType.Error, _refValue = ex };

        // Property accessors
        public bool IsUndefined => _type == Interfaces.ValueType.Undefined;
        public bool IsNull => _type == Interfaces.ValueType.Null;
        public bool IsBoolean => _type == Interfaces.ValueType.Boolean;
        public bool IsNumber => _type == Interfaces.ValueType.Number;
        public bool IsString => _type == Interfaces.ValueType.String;
        public bool IsObject => _type == Interfaces.ValueType.Object;
        public bool IsFunction => _type == Interfaces.ValueType.Function;

        // Conversion
        public bool ToBoolean() => AsBoolean();
        public double ToNumber() => AsNumber();
        public string ToString2() => AsString();
        public override string ToString() => AsString();
        public IObject ToObject() => AsObject();

        public bool AsBoolean() => _type switch
        {
            Interfaces.ValueType.Undefined or Interfaces.ValueType.Null => false,
            Interfaces.ValueType.Boolean => _numberValue != 0.0,
            Interfaces.ValueType.Number => _numberValue != 0.0 && !double.IsNaN(_numberValue),
            Interfaces.ValueType.String => !string.IsNullOrEmpty(_refValue as string),
            Interfaces.ValueType.Object or Interfaces.ValueType.Function => true,
            _ => false,
        };

        public double AsNumber(IExecutionContext context = null) => _type switch
        {
            Interfaces.ValueType.Undefined => double.NaN,
            Interfaces.ValueType.Null => 0.0,
            Interfaces.ValueType.Boolean => _numberValue,
            Interfaces.ValueType.Number => _numberValue,
            Interfaces.ValueType.String => double.TryParse(_refValue as string ?? "", out var d) ? d : double.NaN,
            _ => double.NaN,
        };

        public string AsString(IExecutionContext context = null) => _type switch
        {
            Interfaces.ValueType.Undefined => "undefined",
            Interfaces.ValueType.Null => "null",
            Interfaces.ValueType.Boolean => _numberValue != 0.0 ? "true" : "false",
            Interfaces.ValueType.Number => _numberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Interfaces.ValueType.String => _refValue as string ?? string.Empty,
            Interfaces.ValueType.Symbol => (_refValue as JsSymbol)?.ToString() ?? "Symbol()",
            Interfaces.ValueType.BigInt => (_refValue as JsBigInt)?.ToString() ?? "0",
            Interfaces.ValueType.Object => "[object Object]",
            Interfaces.ValueType.Function => "function(){}",
            Interfaces.ValueType.Error => (_refValue as Exception)?.Message ?? "Error",
            _ => string.Empty,
        };

        public IObject AsObject() => _type switch
        {
            Interfaces.ValueType.Object or Interfaces.ValueType.Function => _refValue as IObject,
            _ => null,
        };

        public FenFunction AsFunction() => _refValue as FenFunction;
        public JsSymbol AsSymbol() => _refValue as JsSymbol;
        public JsBigInt AsBigInt() => _refValue as JsBigInt;
        public Exception AsException() => _refValue as Exception;

        // Forward Set/Get to wrapped object (used by BrowserApi.cs for event objects)
        public void Set(string key, FenValue value)
        {
            if (_refValue is FenObject fenObj)
                fenObj.Set(key, value);
            else if (_refValue is IObject iobj)
                iobj.Set(key, value);
        }

        public FenValue Get(string key)
        {
            if (_refValue is FenObject fenObj)
                return (FenValue)fenObj.Get(key);
            if (_refValue is IObject iobj)
                return (FenValue)iobj.Get(key);
            return Undefined;
        }
    }
}
