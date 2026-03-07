using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    public static class BinaryDataApi
    {
        private sealed class BlobRecord
        {
            public byte[] Bytes { get; init; } = Array.Empty<byte>();
            public string ContentType { get; init; } = string.Empty;
        }

        private static readonly ConcurrentDictionary<string, BlobRecord> BlobUrls = new(StringComparer.Ordinal);

        public static void Register(IExecutionContext context)
        {
            var global = context.Environment;
            global.Set("Blob", FenValue.FromFunction(new FenFunction("Blob", (args, thisVal) =>
                FenValue.FromObject(new JsBlob(args)))));
            global.Set("FormData", FenValue.FromFunction(new FenFunction("FormData", (args, thisVal) =>
                FenValue.FromObject(new JsFormData()))));
            global.Set("FileReader", FenValue.FromFunction(new FenFunction("FileReader", (args, thisVal) =>
                FenValue.FromObject(new JsFileReader(context)))));

            FenObject urlObject;
            var existing = global.Get("URL");
            if ((existing.IsObject || existing.IsFunction) && existing.AsObject() is FenObject existingObject)
            {
                urlObject = existingObject;
            }
            else
            {
                urlObject = new FenObject();
            }

            urlObject.Set("createObjectURL", FenValue.FromFunction(new FenFunction("createObjectURL", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject || args[0].AsObject() is not JsBlob blob)
                {
                    throw new FenTypeError("TypeError: URL.createObjectURL requires a Blob");
                }

                var token = "blob:fen/" + Guid.NewGuid().ToString("N");
                BlobUrls[token] = new BlobRecord
                {
                    Bytes = blob.GetBytes(),
                    ContentType = blob.ContentType
                };
                return FenValue.FromString(token);
            })));

            urlObject.Set("revokeObjectURL", FenValue.FromFunction(new FenFunction("revokeObjectURL", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    BlobUrls.TryRemove(args[0].ToString(), out _);
                }

                return FenValue.Undefined;
            })));

            global.Set("URL", FenValue.FromObject(urlObject));
        }

        public static bool TryResolveBlobUrl(string url, out HttpResponseMessage response)
        {
            response = null;
            if (string.IsNullOrWhiteSpace(url) || !BlobUrls.TryGetValue(url, out var blob))
            {
                return false;
            }

            response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, url),
                Content = new ByteArrayContent(blob.Bytes)
            };

            if (!string.IsNullOrWhiteSpace(blob.ContentType))
            {
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(blob.ContentType);
            }

            return true;
        }

        public static bool TryCreateHttpContent(FenValue bodyValue, out HttpContent content)
        {
            content = null;
            if (bodyValue.IsUndefined || bodyValue.IsNull)
            {
                return false;
            }

            if (!bodyValue.IsObject)
            {
                content = new StringContent(bodyValue.ToString());
                return true;
            }

            var bodyObject = bodyValue.AsObject();
            if (bodyObject is JsBlob blob)
            {
                content = new ByteArrayContent(blob.GetBytes());
                if (!string.IsNullOrWhiteSpace(blob.ContentType))
                {
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(blob.ContentType);
                }
                return true;
            }

            if (bodyObject is JsFormData formData)
            {
                content = new StringContent(formData.ToUrlEncodedString(), Encoding.UTF8, "application/x-www-form-urlencoded");
                return true;
            }

            if (bodyObject is JsArrayBuffer buffer)
            {
                content = new ByteArrayContent(buffer.Data.ToArray());
                return true;
            }

            if (TryCreateTypedArrayContent(bodyObject, out content))
            {
                return true;
            }

            content = new StringContent(bodyValue.ToString());
            return true;
        }

        public static JsBlob CreateBlob(byte[] bytes, string contentType)
        {
            return new JsBlob(bytes ?? Array.Empty<byte>(), contentType ?? string.Empty);
        }

        public static JsFormData ParseFormData(byte[] bytes, string contentType)
        {
            var formData = new JsFormData();
            var payload = bytes == null || bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return formData;
            }

            var isUrlEncoded = string.IsNullOrWhiteSpace(contentType) ||
                               contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
            if (!isUrlEncoded)
            {
                return formData;
            }

            foreach (var pair in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0].Replace("+", " "));
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace("+", " ")) : string.Empty;
                formData.Append(key, value);
            }

            return formData;
        }

        private static bool TryCreateTypedArrayContent(IObject bodyObject, out HttpContent content)
        {
            content = null;
            if (!bodyObject.Has("buffer"))
            {
                return false;
            }

            var bufferValue = bodyObject.Get("buffer");
            if (!bufferValue.IsObject || bufferValue.AsObject() is not JsArrayBuffer arrayBuffer)
            {
                return false;
            }

            var byteOffset = bodyObject.Has("byteOffset") ? (int)bodyObject.Get("byteOffset").ToNumber() : 0;
            var byteLength = bodyObject.Has("byteLength") ? (int)bodyObject.Get("byteLength").ToNumber() : arrayBuffer.Data.Length - byteOffset;
            byteOffset = Math.Max(0, Math.Min(byteOffset, arrayBuffer.Data.Length));
            byteLength = Math.Max(0, Math.Min(byteLength, arrayBuffer.Data.Length - byteOffset));

            var bytes = new byte[byteLength];
            Array.Copy(arrayBuffer.Data, byteOffset, bytes, 0, byteLength);
            content = new ByteArrayContent(bytes);
            return true;
        }
    }

    public sealed class JsBlob : FenObject
    {
        private readonly byte[] _bytes;

        public string ContentType { get; }

        public JsBlob(FenValue[] args)
            : this(ReadParts(args.Length > 0 ? args[0] : FenValue.Undefined), ReadBlobType(args.Length > 1 ? args[1] : FenValue.Undefined))
        {
        }

        public JsBlob(byte[] bytes, string contentType)
        {
            _bytes = bytes ?? Array.Empty<byte>();
            ContentType = contentType ?? string.Empty;

            Set("size", FenValue.FromNumber(_bytes.Length));
            Set("type", FenValue.FromString(ContentType));
            Set("arrayBuffer", FenValue.FromFunction(new FenFunction("arrayBuffer", (args, thisVal) =>
                CreatePromise(async () =>
                {
                    var buffer = new JsArrayBuffer(_bytes.Length);
                    Array.Copy(_bytes, buffer.Data, _bytes.Length);
                    return FenValue.FromObject(buffer);
                }))));
            Set("text", FenValue.FromFunction(new FenFunction("text", (args, thisVal) =>
                CreatePromise(async () => FenValue.FromString(Encoding.UTF8.GetString(_bytes))))));
        }

        public byte[] GetBytes() => _bytes.ToArray();

        private static byte[] ReadParts(FenValue partsValue)
        {
            if (partsValue.IsUndefined || partsValue.IsNull)
            {
                return Array.Empty<byte>();
            }

            var chunks = new List<byte>();
            if (partsValue.IsObject && partsValue.AsObject() is FenObject partsObject && partsObject.Has("length"))
            {
                var length = (int)partsObject.Get("length").ToNumber();
                for (var i = 0; i < length; i++)
                {
                    AppendPartBytes(chunks, partsObject.Get(i.ToString()));
                }
            }
            else
            {
                AppendPartBytes(chunks, partsValue);
            }

            return chunks.ToArray();
        }

        private static string ReadBlobType(FenValue optionsValue)
        {
            if (!optionsValue.IsObject)
            {
                return string.Empty;
            }

            var options = optionsValue.AsObject();
            return options.Has("type") ? options.Get("type").ToString().ToLowerInvariant() : string.Empty;
        }

        private static void AppendPartBytes(List<byte> bytes, FenValue part)
        {
            if (part.IsUndefined || part.IsNull)
            {
                return;
            }

            if (!part.IsObject)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(part.ToString()));
                return;
            }

            var partObject = part.AsObject();
            if (partObject is JsBlob blob)
            {
                bytes.AddRange(blob._bytes);
                return;
            }

            if (partObject is JsArrayBuffer buffer)
            {
                bytes.AddRange(buffer.Data);
                return;
            }

            if (partObject.Has("buffer"))
            {
                var bufferValue = partObject.Get("buffer");
                if (bufferValue.IsObject && bufferValue.AsObject() is JsArrayBuffer sourceBuffer)
                {
                    var byteOffset = partObject.Has("byteOffset") ? (int)partObject.Get("byteOffset").ToNumber() : 0;
                    var byteLength = partObject.Has("byteLength") ? (int)partObject.Get("byteLength").ToNumber() : sourceBuffer.Data.Length - byteOffset;
                    byteOffset = Math.Max(0, Math.Min(byteOffset, sourceBuffer.Data.Length));
                    byteLength = Math.Max(0, Math.Min(byteLength, sourceBuffer.Data.Length - byteOffset));
                    for (var i = 0; i < byteLength; i++)
                    {
                        bytes.Add(sourceBuffer.Data[byteOffset + i]);
                    }
                    return;
                }
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(part.ToString()));
        }

        private static FenValue CreatePromise(Func<Task<FenValue>> factory)
        {
            var executor = new FenFunction("executor", (executorArgs, executorThis) =>
            {
                var resolve = executorArgs[0].AsFunction();
                var reject = executorArgs[1].AsFunction();
                _ = FetchApi.RunDetachedAsync(async () =>
                {
                    try
                    {
                        var result = await factory().ConfigureAwait(false);
                        resolve.Invoke(new[] { result }, null);
                    }
                    catch (Exception ex)
                    {
                        reject.Invoke(new[] { FenValue.FromString(ex.Message) }, null);
                    }
                });
                return FenValue.Undefined;
            });

            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(executor), null));
        }
    }

    public sealed class JsFormData : FenObject
    {
        private readonly List<KeyValuePair<string, string>> _entries = new();

        public JsFormData()
        {
            Set("append", FenValue.FromFunction(new FenFunction("append", (args, thisVal) =>
            {
                if (args.Length >= 1)
                {
                    Append(args[0].ToString(), args.Length > 1 ? args[1].ToString() : string.Empty);
                }
                return FenValue.Undefined;
            })));
            Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                if (args.Length == 0)
                {
                    return FenValue.Null;
                }

                var found = _entries.FirstOrDefault(entry => string.Equals(entry.Key, args[0].ToString(), StringComparison.Ordinal));
                return string.IsNullOrEmpty(found.Key) && !_entries.Any(entry => string.Equals(entry.Key, args[0].ToString(), StringComparison.Ordinal))
                    ? FenValue.Null
                    : FenValue.FromString(found.Value);
            })));
            Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
                FenValue.FromBoolean(args.Length > 0 && _entries.Any(entry => string.Equals(entry.Key, args[0].ToString(), StringComparison.Ordinal))))));
            Set("delete", FenValue.FromFunction(new FenFunction("delete", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    _entries.RemoveAll(entry => string.Equals(entry.Key, args[0].ToString(), StringComparison.Ordinal));
                }
                return FenValue.Undefined;
            })));
        }

        public void Append(string name, string value)
        {
            _entries.Add(new KeyValuePair<string, string>(name ?? string.Empty, value ?? string.Empty));
        }

        public string ToUrlEncodedString()
        {
            return string.Join("&", _entries.Select(entry =>
                Uri.EscapeDataString(entry.Key) + "=" + Uri.EscapeDataString(entry.Value)));
        }
    }

    public sealed class JsFileReader : FenObject
    {
        private readonly IExecutionContext _context;

        public JsFileReader(IExecutionContext context)
        {
            _context = context;
            Set("EMPTY", FenValue.FromNumber(0));
            Set("LOADING", FenValue.FromNumber(1));
            Set("DONE", FenValue.FromNumber(2));
            Set("readyState", FenValue.FromNumber(0));
            Set("result", FenValue.Null);
            Set("error", FenValue.Null);
            Set("onload", FenValue.Null);
            Set("onerror", FenValue.Null);
            Set("readAsText", FenValue.FromFunction(new FenFunction("readAsText", (args, thisVal) =>
            {
                StartRead(args, bytes => FenValue.FromString(Encoding.UTF8.GetString(bytes)));
                return FenValue.Undefined;
            })));
            Set("readAsArrayBuffer", FenValue.FromFunction(new FenFunction("readAsArrayBuffer", (args, thisVal) =>
            {
                StartRead(args, bytes =>
                {
                    var buffer = new JsArrayBuffer(bytes.Length);
                    Array.Copy(bytes, buffer.Data, bytes.Length);
                    return FenValue.FromObject(buffer);
                });
                return FenValue.Undefined;
            })));
        }

        private void StartRead(FenValue[] args, Func<byte[], FenValue> projector)
        {
            if (args.Length == 0 || !args[0].IsObject || args[0].AsObject() is not JsBlob blob)
            {
                throw new FenTypeError("TypeError: FileReader requires a Blob");
            }

            Set("readyState", FenValue.FromNumber(1));
            _ = FetchApi.RunDetachedAsync(async () =>
            {
                try
                {
                    await Task.Yield();
                    var result = projector(blob.GetBytes());
                    Set("result", result);
                    Set("readyState", FenValue.FromNumber(2));
                    var onload = Get("onload");
                    if (onload.IsFunction)
                    {
                        onload.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(this));
                    }
                }
                catch (Exception ex)
                {
                    Set("error", FenValue.FromString(ex.Message));
                    Set("readyState", FenValue.FromNumber(2));
                    var onerror = Get("onerror");
                    if (onerror.IsFunction)
                    {
                        onerror.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, _context, FenValue.FromObject(this));
                    }
                }
            });
        }
    }
}
