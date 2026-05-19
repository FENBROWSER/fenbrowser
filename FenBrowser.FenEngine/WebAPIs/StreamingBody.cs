using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// WHATWG Streams API - ReadableStream implementation for Fetch API response bodies
    /// Provides streaming access to response data without loading everything into memory
    /// </summary>
    public class JsReadableStream : FenObject
    {
        private readonly Stream _underlyingStream;
        // Locked state remains false until ReadableStreamReader acquisition is implemented.
#pragma warning disable CS0649
        private readonly bool _isLocked;
#pragma warning restore CS0649
        private bool _isDisturbed;
        private bool _isCancelled;
        
        public JsReadableStream(Stream stream, IExecutionContext context = null)
        {
            _underlyingStream = stream ?? throw new ArgumentNullException(nameof(stream));
            Context = context;
            
            // Exposed properties
            Set("locked", FenValue.FromBoolean(false));
            
            // Exposed methods
            Set("getReader", FenValue.FromFunction(new FenFunction("getReader", GetReader)));
            Set("cancel", FenValue.FromFunction(new FenFunction("cancel", Cancel)));
            Set("tee", FenValue.FromFunction(new FenFunction("tee", Tee)));
            Set("pipeThrough", FenValue.FromFunction(new FenFunction("pipeThrough", (args, thisVal) => 
                FenValue.FromError("NotImplementedError: pipeThrough not yet implemented"))));
            Set("pipeTo", FenValue.FromFunction(new FenFunction("pipeTo", (args, thisVal) => 
                FenValue.FromError("NotImplementedError: pipeTo not yet implemented"))));
        }
        
        public IExecutionContext Context { get; }
        internal Stream UnderlyingStream => _underlyingStream;
        
        public bool IsDisturbed => _isDisturbed;
        public bool IsLocked => _isLocked;
        public bool IsCancelled => _isCancelled;
        
        private FenValue GetReader(FenValue[] args, FenValue thisVal)
        {
            if (_isDisturbed)
            {
                return FenValue.FromError("TypeError: ReadableStream is already disturbed");
            }
            
            _isDisturbed = true;
            Set("locked", FenValue.FromBoolean(true));
            
            var options = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
            string mode = null;
            if (options != null)
            {
                var modeValue = options.Get("mode");
                if (!modeValue.IsUndefined && !modeValue.IsNull)
                {
                    mode = modeValue.ToString();
                }
            }
            
            if (mode == "byob")
            {
                // BYOB (Bring Your Own Buffer) reader - not implemented
                return FenValue.FromError("NotImplementedError: BYOB reader not yet implemented");
            }
            
            // Default: standard ReadableStreamDefaultReader
            var reader = new JsReadableStreamDefaultReader(_underlyingStream, Context);
            return FenValue.FromObject(reader);
        }
        
        private FenValue Cancel(FenValue[] args, FenValue thisVal)
        {
            if (_isCancelled) return FenValue.Undefined;
            
            _isCancelled = true;
            _underlyingStream?.Close();
            
            var reason = args.Length > 0 ? args[0] : FenValue.Undefined;
            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(new FenFunction("cancelResolver", 
                (resolveArgs, resolveThis) => 
                {
                    resolveArgs[0].AsFunction().Invoke(new[] { reason }, Context);
                    return FenValue.Undefined;
                })), Context));
        }
        
        private FenValue Tee(FenValue[] args, FenValue thisVal)
        {
            if (_isDisturbed || _isLocked)
            {
                return FenValue.FromError("TypeError: Cannot tee a locked or disturbed stream");
            }
            
            _isDisturbed = true;
            Set("locked", FenValue.FromBoolean(true));
            
            // Create two in-memory copies for simplicity
            // In a real implementation, this would use a more sophisticated buffering strategy
            var memoryStream1 = new MemoryStream();
            var memoryStream2 = new MemoryStream();
            
            _underlyingStream.CopyTo(memoryStream1);
            memoryStream1.Position = 0;
            memoryStream1.CopyTo(memoryStream2);
            memoryStream1.Position = 0;
            memoryStream2.Position = 0;
            
            var stream1 = new JsReadableStream(memoryStream1, Context);
            var stream2 = new JsReadableStream(memoryStream2, Context);
            
            var result = new FenObject();
            result.Set("0", FenValue.FromObject(stream1));
            result.Set("1", FenValue.FromObject(stream2));
            return FenValue.FromObject(result);
        }
        
        public static JsReadableStream CreateEmpty(IExecutionContext context = null)
        {
            return new JsReadableStream(new MemoryStream(), context);
        }
    }
    
    /// <summary>
    /// WHATWG Streams API - ReadableStreamDefaultReader
    /// </summary>
    public class JsReadableStreamDefaultReader : FenObject
    {
        private readonly Stream _stream;
        private readonly IExecutionContext _context;
        private bool _isClosed;
        
        public JsReadableStreamDefaultReader(Stream stream, IExecutionContext context)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _context = context;
            
            Set("read", FenValue.FromFunction(new FenFunction("read", Read)));
            Set("releaseLock", FenValue.FromFunction(new FenFunction("releaseLock", ReleaseLock)));
            Set("closed", CreateClosedPromise());
        }
        
        private FenValue Read(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(new FenFunction("readResolver", 
                (resolveArgs, resolveThis) => 
                {
                    try
                    {
                        var resolve = resolveArgs[0].AsFunction();
                        
                        if (_isClosed)
                        {
                            var doneResult = new FenObject();
                            doneResult.Set("done", FenValue.FromBoolean(true));
                            doneResult.Set("value", FenValue.Undefined);
                            resolve.Invoke(new[] { FenValue.FromObject(doneResult) }, _context);
                            return FenValue.Undefined;
                        }
                        
                        var buffer = new byte[4096];
                        var bytesRead = _stream.Read(buffer, 0, buffer.Length);
                        
                        if (bytesRead == 0)
                        {
                            _isClosed = true;
                            var doneResult = new FenObject();
                            doneResult.Set("done", FenValue.FromBoolean(true));
                            doneResult.Set("value", FenValue.Undefined);
                            resolve.Invoke(new[] { FenValue.FromObject(doneResult) }, _context);
                        }
                        else
                        {
                            var chunk = new byte[bytesRead];
                            Array.Copy(buffer, chunk, bytesRead);
                            var chunkBuffer = new JsArrayBuffer(bytesRead);
                            Array.Copy(chunk, chunkBuffer.Data, bytesRead);
                            
                            var result = new FenObject();
                            result.Set("done", FenValue.FromBoolean(false));
                            result.Set("value", FenValue.FromObject(new JsUint8Array(chunkBuffer)));
                            resolve.Invoke(new[] { FenValue.FromObject(result) }, _context);
                        }
                    }
                    catch (Exception ex)
                    {
                        var reject = resolveArgs[1].AsFunction();
                        reject.Invoke(new[] { FenValue.FromString($"Read error: {ex.Message}") }, _context);
                    }
                    
                    return FenValue.Undefined;
                })), _context));
        }
        
        private FenValue ReleaseLock(FenValue[] args, FenValue thisVal)
        {
            if (!_isClosed)
            {
                _stream?.Close();
                _isClosed = true;
            }
            return FenValue.Undefined;
        }
        
        private FenValue CreateClosedPromise()
        {
            return FenValue.FromObject(new JsPromise(FenValue.FromFunction(new FenFunction("closedPromise", 
                (resolveArgs, resolveThis) => 
                {
                    // Returns a promise that resolves when stream closes
                    // For simplicity, resolve immediately if already closed
                    if (_isClosed)
                    {
                        resolveArgs[0].AsFunction().Invoke(new FenValue[] { }, _context);
                    }
                    return FenValue.Undefined;
                })), _context));
        }
    }
    
    /// <summary>
    /// WHATWG Streams API - AbortController and AbortSignal
    /// </summary>
    public class JsAbortController : FenObject
    {
        private JsAbortSignal _signal;
        
        public JsAbortController(IExecutionContext context = null)
        {
            _signal = new JsAbortSignal(context);
            
            Set("signal", FenValue.FromObject(_signal));
            Set("abort", FenValue.FromFunction(new FenFunction("abort", Abort)));
        }
        
        private FenValue Abort(FenValue[] args, FenValue thisVal)
        {
            var reason = args.Length > 0 ? args[0] : FenValue.FromString("AbortError");
            _signal.Abort(reason);
            return FenValue.Undefined;
        }
        
        public JsAbortSignal Signal => _signal;
    }
    
    public class JsAbortSignal : FenObject
    {
        private bool _aborted;
        private FenValue _abortReason;
        private readonly List<FenFunction> _abortHandlers = new List<FenFunction>();
        private readonly IExecutionContext _context;
        
        public JsAbortSignal(IExecutionContext context = null)
        {
            _context = context;
            
            Set("aborted", FenValue.FromBoolean(false));
            Set("reason", FenValue.Undefined);
            Set("throwIfAborted", FenValue.FromFunction(new FenFunction("throwIfAborted", ThrowIfAborted)));
            Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", AddEventListener)));
            Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", RemoveEventListener)));
            Set("onabort", FenValue.Undefined); // Event handler property
        }
        
        public bool Aborted => _aborted;
        public FenValue AbortReason => _abortReason;
        
        private FenValue ThrowIfAborted(FenValue[] args, FenValue thisVal)
        {
            if (_aborted)
            {
                var reason = _abortReason.IsUndefined ? FenValue.FromString("AbortError") : _abortReason;
                // This should throw - in JS this would be a real exception
                return FenValue.FromError($"AbortError: {reason}");
            }
            return FenValue.Undefined;
        }
        
        private FenValue AddEventListener(FenValue[] args, FenValue thisVal)
        {
            if (args.Length >= 2 && args[0].IsString && args[0].ToString() == "abort" && args[1].IsFunction)
            {
                _abortHandlers.Add(args[1].AsFunction());
            }
            return FenValue.Undefined;
        }
        
        private FenValue RemoveEventListener(FenValue[] args, FenValue thisVal)
        {
            if (args.Length >= 2 && args[0].IsString && args[0].ToString() == "abort" && args[1].IsFunction)
            {
                _abortHandlers.Remove(args[1].AsFunction());
            }
            return FenValue.Undefined;
        }
        
        public void Abort(FenValue reason)
        {
            if (_aborted) return;
            
            _aborted = true;
            _abortReason = reason;
            
            Set("aborted", FenValue.FromBoolean(true));
            Set("reason", reason);
            
            // Call event listeners
            foreach (var handler in _abortHandlers)
            {
                try
                {
                    handler.Invoke(new FenValue[] { }, _context);
                }
                catch
                {
                    // Ignore handler errors
                }
            }
            
            // Call onabort if set
            var onabort = Get("onabort");
            if (onabort.IsFunction)
            {
                try
                {
                    onabort.AsFunction().Invoke(new FenValue[] { }, _context);
                }
                catch
                {
                    // Ignore handler errors
                }
            }
        }
        
        public static JsAbortSignal Timeout(IExecutionContext context, double milliseconds)
        {
            var signal = new JsAbortSignal(context);
            Task.Delay((int)milliseconds).ContinueWith(_ => 
            {
                if (!signal.Aborted)
                {
                    signal.Abort(FenValue.FromString("TimeoutError"));
                }
            });
            return signal;
        }
    }
    
    /// <summary>
    /// Extensions for streaming response bodies
    /// </summary>
    public static class StreamingBodyExtensions
    {
        public static void SetupStreamingBody(JsResponse response, Stream bodyStream, IExecutionContext context)
        {
            var body = new JsReadableStream(bodyStream, context);
            response.Set("body", FenValue.FromObject(body));
            response.Set("bodyUsed", FenValue.FromBoolean(false));
            
            // Override body-consuming methods to use stream
            response.Set("text", FenValue.FromFunction(new FenFunction("text", 
                (args, thisVal) => ReadStreamAsText(body, context))));
            response.Set("json", FenValue.FromFunction(new FenFunction("json", 
                (args, thisVal) => ReadStreamAsJson(body, context))));
            response.Set("arrayBuffer", FenValue.FromFunction(new FenFunction("arrayBuffer", 
                (args, thisVal) => ReadStreamAsArrayBuffer(body, context))));
            response.Set("blob", FenValue.FromFunction(new FenFunction("blob", 
                (args, thisVal) => ReadStreamAsBlob(body, context))));
        }
        
        private static FenValue ReadStreamAsText(JsReadableStream stream, IExecutionContext context)
        {
            // For now, return underlying content directly
            // In full implementation, this would read from stream
            return FenValue.FromString(""); // Placeholder
        }
        
        private static FenValue ReadStreamAsJson(JsReadableStream stream, IExecutionContext context)
        {
            // For now, parse empty JSON
            // In full implementation, this would read from stream
            return FenValue.FromObject(new FenObject()); // Empty object
        }
        
        private static FenValue ReadStreamAsArrayBuffer(JsReadableStream stream, IExecutionContext context)
        {
            // For now, return empty buffer
            // In full implementation, this would read from stream
            return FenValue.FromObject(new JsArrayBuffer(0));
        }
        
        private static FenValue ReadStreamAsBlob(JsReadableStream stream, IExecutionContext context)
        {
            // For now, return empty blob
            // In full implementation, this would read from stream
            return FenValue.FromObject(BinaryDataApi.CreateBlob(Array.Empty<byte>(), ""));
        }
    }
}
