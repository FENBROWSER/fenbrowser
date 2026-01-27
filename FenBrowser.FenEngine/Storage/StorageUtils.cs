using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Storage
{
    public static class StorageUtils
    {
        public static object ToSerializable(FenValue value)
        {
            if (value.IsNull || value.IsUndefined) return null;
            if (value.IsBoolean) return value.ToBoolean();
            if (value.IsNumber) return value.ToNumber();
            if (value.IsString) return value.ToString();
            
            if (value.IsObject)
            {
                var obj = value.AsObject();
                if (obj == null) return null;

                // Handle Arrays
                var lengthVal = obj.Get("length");
                if (lengthVal != null && lengthVal.IsNumber && obj.Has("0")) // Heuristic for array
                {
                    int len = (int)lengthVal.ToNumber();
                    var list = new List<object>(len);
                    for (int i = 0; i < len; i++)
                    {
                        list.Add(ToSerializable(obj.Get(i.ToString())));
                    }
                    return list;
                }

                // Handle plain objects
                var dict = new Dictionary<string, object>();
                foreach (var key in obj.Keys())
                {
                    dict[key] = ToSerializable(obj.Get(key));
                }
                return dict;
            }

            return value.ToString();
        }

        public static FenValue FromSerializable(object obj)
        {
            if (obj == null) return FenValue.Null;
            if (obj is bool b) return FenValue.FromBoolean(b);
            if (obj is string s) return FenValue.FromString(s);
            if (obj is double d) return FenValue.FromNumber(d);
            if (obj is int i) return FenValue.FromNumber(i);
            if (obj is long l) return FenValue.FromNumber(l);

            if (obj is JsonElement element)
            {
                return ConvertJsonElement(element);
            }

            if (obj is IList<object> list)
            {
                var arr = new FenObject();
                for (int j = 0; j < list.Count; j++)
                {
                    arr.Set(j.ToString(), FromSerializable(list[j]));
                }
                arr.Set("length", FenValue.FromNumber(list.Count));
                return FenValue.FromObject(arr);
            }

            if (obj is IDictionary<string, object> dict)
            {
                var fObj = new FenObject();
                foreach (var kvp in dict)
                {
                    fObj.Set(kvp.Key, FromSerializable(kvp.Value));
                }
                return FenValue.FromObject(fObj);
            }

            return FenValue.FromString(obj.ToString());
        }

        private static FenValue ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new FenObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        obj.Set(prop.Name, ConvertJsonElement(prop.Value));
                    }
                    return FenValue.FromObject(obj);
                case JsonValueKind.Array:
                    var arr = new FenObject();
                    int i = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        arr.Set(i.ToString(), ConvertJsonElement(item));
                        i++;
                    }
                    arr.Set("length", FenValue.FromNumber(i));
                    return FenValue.FromObject(arr);
                case JsonValueKind.String:
                    return FenValue.FromString(element.GetString());
                case JsonValueKind.Number:
                    return FenValue.FromNumber(element.GetDouble());
                case JsonValueKind.True:
                    return FenValue.FromBoolean(true);
                case JsonValueKind.False:
                    return FenValue.FromBoolean(false);
                case JsonValueKind.Null:
                default:
                    return FenValue.Null;
            }
        }
    }
}
