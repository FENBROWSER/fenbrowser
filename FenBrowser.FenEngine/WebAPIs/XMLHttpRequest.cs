using System;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.WebAPIs
{
    public class XMLHttpRequest : FenObject
    {
        private const int UNSENT = 0;
        private const int OPENED = 1;
        private const int HEADERS_RECEIVED = 2;
        private const int LOADING = 3;
        private const int DONE = 4;

        private int _readyState = UNSENT;
        private string _method;
        private string _url;
        private bool _async = true;
        private readonly Dictionary<string, string> _requestHeaders = new Dictionary<string, string>();
        private HttpResponseMessage _response;
        private readonly IExecutionContext _context;
        
        public XMLHttpRequest(IExecutionContext context)
        {
            _context = context;
            Set("UNSENT", FenValue.FromNumber(UNSENT));
            Set("OPENED", FenValue.FromNumber(OPENED));
            Set("HEADERS_RECEIVED", FenValue.FromNumber(HEADERS_RECEIVED));
            Set("LOADING", FenValue.FromNumber(LOADING));
            Set("DONE", FenValue.FromNumber(DONE));
            
            Set("readyState", FenValue.FromNumber(_readyState));
            Set("status", FenValue.FromNumber(0));
            Set("statusText", FenValue.FromString(""));
            Set("responseText", FenValue.FromString(""));
            Set("responseURL", FenValue.FromString(""));
            
            // Methods
            Set("open", FenValue.FromFunction(new FenFunction("open", Open)));
            Set("send", FenValue.FromFunction(new FenFunction("send", Send)));
            Set("setRequestHeader", FenValue.FromFunction(new FenFunction("setRequestHeader", SetRequestHeader)));
            Set("getAllResponseHeaders", FenValue.FromFunction(new FenFunction("getAllResponseHeaders", GetAllResponseHeaders)));
            Set("getResponseHeader", FenValue.FromFunction(new FenFunction("getResponseHeader", GetResponseHeader)));
        }

        private void SetReadyState(int state)
        {
            _readyState = state;
            Set("readyState", FenValue.FromNumber(_readyState));
            
            // Dispatch onreadystatechange
            if (Get("onreadystatechange") is FenValue fn && fn.IsFunction)
            {
                 _context.ScheduleCallback(() => {
                     try { fn.AsFunction().Invoke(Array.Empty<FenValue>(), _context); } catch {}
                 }, 0);
            }
        }

        private FenValue Open(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.FromError("TypeError: Not enough arguments");
            _method = args[0].ToString();
            _url = args[1].ToString();
            _async = args.Length > 2 ? args[2].ToBoolean() : true;
            
            SetReadyState(OPENED);
            return FenValue.Undefined;
        }

        private FenValue SetRequestHeader(FenValue[] args, FenValue thisVal)
        {
            if (_readyState != OPENED) return FenValue.FromError("InvalidStateError");
            if (args.Length < 2) return FenValue.Undefined;
            _requestHeaders[args[0].ToString()] = args[1].ToString();
            return FenValue.Undefined;
        }

        private FenValue Send(FenValue[] args, FenValue thisVal)
        {
            if (_readyState != OPENED) return FenValue.FromError("InvalidStateError");
            
            string body = args.Length > 0 ? args[0].ToString() : null;
            
            // Execute request
            Task.Run(async () =>
            {
                try
                {
                    using (var client = new HttpClient())
                    {
                        var req = new HttpRequestMessage(new HttpMethod(_method), _url);
                        foreach(var h in _requestHeaders)
                        {
                            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                        }
                        
                        if (body != null && (_method == "POST" || _method == "PUT"))
                        {
                            req.Content = new StringContent(body); 
                            if (_requestHeaders.TryGetValue("Content-Type", out var ct))
                            {
                                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ct);
                            }
                        }

                        // Send
                        _response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        
                        _context.ScheduleCallback(() => {
                            Set("status", FenValue.FromNumber((int)_response.StatusCode));
                            Set("statusText", FenValue.FromString(_response.ReasonPhrase));
                            Set("responseURL", FenValue.FromString(_response.RequestMessage.RequestUri.ToString()));
                            SetReadyState(HEADERS_RECEIVED);
                        }, 0);

                        var content = await _response.Content.ReadAsStringAsync();
                        
                        _context.ScheduleCallback(() => {
                             SetReadyState(LOADING); 
                             Set("responseText", FenValue.FromString(content));
                             SetReadyState(DONE);
                             
                             // onload
                             if (Get("onload") is FenValue fn && fn.IsFunction)
                                 fn.AsFunction().Invoke(Array.Empty<FenValue>(), _context);
                        }, 0);
                    }
                }
                catch (Exception ex)
                {
                     _context.ScheduleCallback(() => {
                         // Error handling
                         if (Get("onerror") is FenValue fn && fn.IsFunction)
                             fn.AsFunction().Invoke(Array.Empty<FenValue>(), _context);
                     }, 0);
                }
            });

            return FenValue.Undefined;
        }
        
        private FenValue GetAllResponseHeaders(FenValue[] args, FenValue thisVal)
        {
            if (_readyState < HEADERS_RECEIVED) return FenValue.FromString("");
            return FenValue.FromString(_response?.Headers.ToString() ?? "");
        }
        
        private FenValue GetResponseHeader(FenValue[] args, FenValue thisVal)
        {
             if (_readyState < HEADERS_RECEIVED || args.Length < 1) return FenValue.Null;
             var name = args[0].ToString();
             if (_response != null && _response.Headers.TryGetValues(name, out var vals))
             {
                 return FenValue.FromString(string.Join(", ", vals));
             }
             return FenValue.Null;
        }
    }
}
