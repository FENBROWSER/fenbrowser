using System.Collections;
using System.Reflection;
using System.Text.Json;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.DevTools.Domains;

/// <summary>
/// Handler for the Runtime domain (Console, evaluating scripts).
/// </summary>
public class RuntimeDomain : IProtocolHandler
{
    public string Domain => "Runtime";

    private readonly IDevToolsHost _host;
    private readonly Dictionary<string, object?> _remoteObjects = new(StringComparer.Ordinal);
    private readonly Queue<string> _remoteObjectOrder = new();
    private readonly object _remoteObjectsLock = new();
    private long _nextRemoteObjectId;
    private bool _enabled;
    private const int MaxRetainedRemoteObjects = 512;

    public RuntimeDomain(IDevToolsHost host)
    {
        _host = host;
    }

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "enable" => EnableAsync(request),
            "disable" => DisableAsync(request),
            "evaluate" => EvaluateAsync(request),
            "getProperties" => GetPropertiesAsync(request),
            "releaseObject" => ReleaseObjectAsync(request),
            "releaseObjectGroup" => ReleaseObjectGroupAsync(request),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: Runtime.{method}"))
        };
    }

    private Task<ProtocolResponse> EnableAsync(ProtocolRequest request)
    {
        _enabled = true;
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private Task<ProtocolResponse> DisableAsync(ProtocolRequest request)
    {
        _enabled = false;
        lock (_remoteObjectsLock)
        {
            _remoteObjects.Clear();
            _remoteObjectOrder.Clear();
        }
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private async Task<ProtocolResponse> EvaluateAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return ProtocolResponse.Failure(request.Id, "Params required");
        }

        try
        {
            var expression = request.Params.Value.GetProperty("expression").GetString();
            if (string.IsNullOrEmpty(expression))
            {
                return ProtocolResponse.Failure(request.Id, "Expression required");
            }

            var result = await _host.EvaluateScriptAsync(expression).ConfigureAwait(false);
            var remoteObject = CreateRemoteObject(result);

            return ProtocolResponse.Success(request.Id, new EvaluateResult
            {
                Result = remoteObject
            });
        }
        catch (Exception ex)
        {
            return ProtocolResponse.Failure(request.Id, $"Eval error: {ex.Message}");
        }
    }

    private Task<ProtocolResponse> GetPropertiesAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }

        try
        {
            var objectId = request.Params.Value.GetProperty("objectId").GetString();
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "objectId required"));
            }

            object? target;
            lock (_remoteObjectsLock)
            {
                if (!_remoteObjects.TryGetValue(objectId, out target))
                {
                    return Task.FromResult(ProtocolResponse.Failure(request.Id, "Remote object not found"));
                }
            }

            var properties = EnumerateProperties(target)
                .Select(property => new RuntimePropertyDescriptor
                {
                    Name = property.Name,
                    Value = CreateRemoteObject(property.Value),
                    Writable = property.Writable,
                    Configurable = property.Configurable,
                    Enumerable = property.Enumerable,
                    IsOwn = property.IsOwn
                })
                .ToArray();

            return Task.FromResult(ProtocolResponse.Success(request.Id, new GetPropertiesResult
            {
                Result = properties
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Runtime error: {ex.Message}"));
        }
    }

    private Task<ProtocolResponse> ReleaseObjectAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }

        try
        {
            var objectId = request.Params.Value.GetProperty("objectId").GetString();
            if (string.IsNullOrWhiteSpace(objectId))
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "objectId required"));
            }

            lock (_remoteObjectsLock)
            {
                _remoteObjects.Remove(objectId);
            }

            return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Runtime error: {ex.Message}"));
        }
    }

    private Task<ProtocolResponse> ReleaseObjectGroupAsync(ProtocolRequest request)
    {
        lock (_remoteObjectsLock)
        {
            _remoteObjects.Clear();
            _remoteObjectOrder.Clear();
        }

        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private RemoteObject CreateRemoteObject(object? value)
    {
        if (value is FenValue fenValue)
        {
            return CreateRemoteObjectFromFenValue(fenValue);
        }

        if (value == null)
        {
            return new RemoteObject
            {
                Type = "undefined",
                Description = "undefined"
            };
        }

        if (value is string text)
        {
            return new RemoteObject
            {
                Type = "string",
                Value = text,
                Description = text
            };
        }

        if (value is bool boolean)
        {
            return new RemoteObject
            {
                Type = "boolean",
                Value = boolean,
                Description = boolean ? "true" : "false"
            };
        }

        if (IsNumber(value))
        {
            return new RemoteObject
            {
                Type = "number",
                Value = value,
                Description = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        if (value is JsonElement jsonElement)
        {
            return CreateRemoteObjectFromJson(jsonElement);
        }

        if (value is IDictionary dictionary)
        {
            return CreateObjectRemoteObject(dictionary, "object", null, "Object");
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            return CreateObjectRemoteObject(value, "object", "array", DescribeEnumerable(enumerable));
        }

        return CreateObjectRemoteObject(value, "object", null, value.GetType().Name);
    }

    private RemoteObject CreateRemoteObjectFromFenValue(FenValue value)
    {
        return value.Type switch
        {
            FenValueType.Undefined => new RemoteObject
            {
                Type = "undefined",
                Description = "undefined"
            },
            FenValueType.Null => new RemoteObject
            {
                Type = "object",
                Subtype = "null",
                Value = null,
                Description = "null"
            },
            FenValueType.Boolean => new RemoteObject
            {
                Type = "boolean",
                Value = value.AsBoolean(),
                Description = value.AsBoolean() ? "true" : "false"
            },
            FenValueType.Number => new RemoteObject
            {
                Type = "number",
                Value = value.AsNumber(),
                Description = value.AsString()
            },
            FenValueType.String => new RemoteObject
            {
                Type = "string",
                Value = value.AsString(),
                Description = value.AsString()
            },
            FenValueType.Symbol => new RemoteObject
            {
                Type = "symbol",
                Description = value.AsString()
            },
            FenValueType.BigInt => new RemoteObject
            {
                Type = "bigint",
                Description = value.AsString()
            },
            FenValueType.Function => CreateObjectRemoteObject(value.AsObject(), "function", null, "Function"),
            FenValueType.Object => CreateObjectRemoteObject(
                value.AsObject(),
                "object",
                IsFenArray(value.AsObject()) ? "array" : null,
                DescribeFenObject(value.AsObject())),
            FenValueType.Throw => CreateRemoteObject(value.GetThrownValue()),
            FenValueType.ReturnValue => CreateRemoteObject(value.GetReturnValue()),
            _ => new RemoteObject
            {
                Type = "object",
                Description = value.AsString()
            }
        };
    }

    private RemoteObject CreateRemoteObjectFromJson(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => new RemoteObject
            {
                Type = "object",
                Subtype = "null",
                Value = null,
                Description = "null"
            },
            JsonValueKind.String => new RemoteObject
            {
                Type = "string",
                Value = value.GetString(),
                Description = value.GetString()
            },
            JsonValueKind.True or JsonValueKind.False => new RemoteObject
            {
                Type = "boolean",
                Value = value.GetBoolean(),
                Description = value.GetBoolean() ? "true" : "false"
            },
            JsonValueKind.Number => new RemoteObject
            {
                Type = "number",
                Value = value.GetDouble(),
                Description = value.ToString()
            },
            JsonValueKind.Array => CreateObjectRemoteObject(value, "object", "array", $"Array({value.GetArrayLength()})"),
            JsonValueKind.Object => CreateObjectRemoteObject(value, "object", null, "Object"),
            _ => new RemoteObject
            {
                Type = "undefined",
                Description = "undefined"
            }
        };
    }

    private RemoteObject CreateObjectRemoteObject(object? value, string type, string? subtype, string description)
    {
        var objectId = StoreRemoteObject(value);
        return new RemoteObject
        {
            Type = type,
            Subtype = subtype,
            ClassName = value?.GetType().Name,
            Description = description,
            ObjectId = objectId
        };
    }

    private string StoreRemoteObject(object? value)
    {
        var objectId = $"fen:{Interlocked.Increment(ref _nextRemoteObjectId)}";
        lock (_remoteObjectsLock)
        {
            _remoteObjects[objectId] = value;
            _remoteObjectOrder.Enqueue(objectId);
            while (_remoteObjectOrder.Count > MaxRetainedRemoteObjects)
            {
                var expiredObjectId = _remoteObjectOrder.Dequeue();
                _remoteObjects.Remove(expiredObjectId);
            }
        }

        return objectId;
    }

    private IEnumerable<RuntimePropertyValue> EnumerateProperties(object? target)
    {
        if (target == null)
        {
            return Enumerable.Empty<RuntimePropertyValue>();
        }

        if (target is FenValue fenValue)
        {
            return EnumerateProperties(UnwrapFenValue(fenValue));
        }

        if (target is JsonElement jsonElement)
        {
            return EnumerateJsonProperties(jsonElement);
        }

        if (target is IObject jsObject)
        {
            return EnumerateJsObjectProperties(jsObject);
        }

        if (target is IDictionary dictionary)
        {
            return dictionary.Keys.Cast<object>()
                .Select(key => new RuntimePropertyValue(
                    Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    dictionary[key],
                    true,
                    true,
                    true,
                    true));
        }

        if (target is IEnumerable enumerable && target is not string)
        {
            return EnumerateEnumerableProperties(enumerable);
        }

        return EnumerateReflectedProperties(target);
    }

    private IEnumerable<RuntimePropertyValue> EnumerateJsObjectProperties(IObject jsObject)
    {
        var names = jsObject.GetOwnPropertyNames()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Select(name =>
        {
            var descriptor = jsObject.GetOwnPropertyDescriptor(name);
            return new RuntimePropertyValue(
                name,
                jsObject.Get(name),
                descriptor?.Writable,
                descriptor?.Configurable ?? true,
                descriptor?.Enumerable ?? true,
                true);
        });
    }

    private IEnumerable<RuntimePropertyValue> EnumerateJsonProperties(JsonElement jsonElement)
    {
        if (jsonElement.ValueKind == JsonValueKind.Object)
        {
            return jsonElement.EnumerateObject()
                .Select(property => new RuntimePropertyValue(property.Name, property.Value, true, true, true, true));
        }

        if (jsonElement.ValueKind == JsonValueKind.Array)
        {
            var values = new List<RuntimePropertyValue>();
            var index = 0;
            foreach (var item in jsonElement.EnumerateArray())
            {
                values.Add(new RuntimePropertyValue(index.ToString(), item, true, true, true, true));
                index++;
            }

            values.Add(new RuntimePropertyValue("length", index, false, true, false, true));
            return values;
        }

        return Enumerable.Empty<RuntimePropertyValue>();
    }

    private IEnumerable<RuntimePropertyValue> EnumerateEnumerableProperties(IEnumerable enumerable)
    {
        var values = new List<RuntimePropertyValue>();
        var index = 0;

        foreach (var item in enumerable)
        {
            values.Add(new RuntimePropertyValue(index.ToString(), item, true, true, true, true));
            index++;
        }

        values.Add(new RuntimePropertyValue("length", index, false, true, false, true));
        return values;
    }

    private IEnumerable<RuntimePropertyValue> EnumerateReflectedProperties(object target)
    {
        var type = target.GetType();
        var properties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod != null)
            .Select(property => new RuntimePropertyValue(
                property.Name,
                SafeRead(() => property.GetValue(target)),
                property.SetMethod != null,
                true,
                true,
                true));

        var fields = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Select(field => new RuntimePropertyValue(
                field.Name,
                SafeRead(() => field.GetValue(target)),
                !field.IsInitOnly,
                true,
                true,
                true));

        return properties.Concat(fields);
    }

    private static object? SafeRead(Func<object?> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    private static object? UnwrapFenValue(FenValue value)
    {
        return value.Type switch
        {
            FenValueType.Throw => UnwrapFenValue(value.GetThrownValue()),
            FenValueType.ReturnValue => UnwrapFenValue(value.GetReturnValue()),
            FenValueType.Object or FenValueType.Function => value.AsObject(),
            _ => value.ToNativeObject()
        };
    }

    private static bool IsFenArray(IObject? jsObject)
    {
        if (jsObject == null)
        {
            return false;
        }

        var length = jsObject.Get("length");
        return length.Type == FenValueType.Number;
    }

    private static string DescribeFenObject(IObject? jsObject)
    {
        if (jsObject == null)
        {
            return "Object";
        }

        if (IsFenArray(jsObject))
        {
            var length = jsObject.Get("length").AsNumber();
            return $"Array({length:0})";
        }

        return jsObject.GetType().Name;
    }

    private static string DescribeEnumerable(IEnumerable enumerable)
    {
        if (enumerable is ICollection collection)
        {
            return $"Array({collection.Count})";
        }

        return "Array";
    }

    private static bool IsNumber(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private sealed record RuntimePropertyValue(
        string Name,
        object? Value,
        bool? Writable,
        bool Configurable,
        bool Enumerable,
        bool IsOwn);
}
