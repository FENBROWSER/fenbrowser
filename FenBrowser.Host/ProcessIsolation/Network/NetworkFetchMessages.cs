using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using FenBrowser.Core.Network.Handlers;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    /// <summary>
    /// Conversions between <see cref="HttpRequestMessage"/>/<see cref="HttpResponseMessage"/>
    /// and the fetch payloads carried over IPC. The same shapes travel two hops -
    /// renderer to broker and broker to network child - so both ends of both hops
    /// share one definition of what a header set, a fetch mode or a response head
    /// looks like on the wire.
    /// </summary>
    internal static class NetworkFetchMessages
    {
        public static NetworkFetchRequestPayload BuildFetchPayload(
            HttpRequestMessage request,
            string initiatorOrigin,
            NetworkBodyPipe bodyPipe)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Raw values, joined with each header's own separator. Re-joining the parsed
            // values with ", " turned User-Agent into "Mozilla/5.0, (Windows NT 10.0; ...)".
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers.NonValidated)
            {
                headers[h.Key] = h.Value.ToString();
            }
            if (request.Content != null)
            {
                // Reading ContentLength computes it for a sized body (form fields, strings,
                // bytes) and records it among the content headers. Without it the body was
                // re-sent from the pipe with Transfer-Encoding: chunked, which servers
                // commonly refuse for a form post. A length the body does not match fails
                // the request when HttpClient sends it.
                _ = request.Content.Headers.ContentLength;
                foreach (var h in request.Content.Headers.NonValidated)
                {
                    headers[h.Key] = h.Value.ToString();
                }
            }

            return new NetworkFetchRequestPayload
            {
                Url = request.RequestUri?.AbsoluteUri ?? string.Empty,
                Method = request.Method.Method,
                Headers = headers,
                HasBody = request.Content != null,
                BodyPipeName = bodyPipe?.PipeName,
                BodyPipeToken = bodyPipe?.AuthenticationToken,
                Mode = GetFetchMode(request),
                Credentials = CorsHandler.GetCredentialsMode(request),
                InitiatorOrigin = initiatorOrigin ?? string.Empty,
            };
        }

        public static HttpRequestMessage BuildRequest(NetworkFetchRequestPayload payload, Stream requestBody)
        {
            ArgumentNullException.ThrowIfNull(payload);

            var request = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrWhiteSpace(payload.Method) ? "GET" : payload.Method),
                payload.Url);

            if (payload.HasBody)
            {
                request.Content = new StreamContent(
                    requestBody ?? throw new InvalidDataException("Request body stream was unavailable."));
            }

            if (payload.Headers != null)
            {
                foreach (var header in payload.Headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                        request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            return request;
        }

        public static NetworkFetchResponseHeadPayload BuildResponseHead(
            HttpResponseMessage response,
            string requestId,
            string fetchMode,
            string fallbackUrl)
        {
            ArgumentNullException.ThrowIfNull(response);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            return new NetworkFetchResponseHeadPayload
            {
                RequestId = requestId,
                StatusCode = (int)response.StatusCode,
                StatusText = response.ReasonPhrase ?? string.Empty,
                Headers = headers,
                Url = response.RequestMessage?.RequestUri?.AbsoluteUri ?? fallbackUrl,
                ResponseType = "basic",
                Cors = string.Equals(fetchMode, "cors", StringComparison.OrdinalIgnoreCase),
                Opaque = string.Equals(fetchMode, "no-cors", StringComparison.OrdinalIgnoreCase),
                ContentLength = response.Content.Headers.ContentLength ?? -1
            };
        }

        public static HttpResponseMessage BuildHttpResponse(
            NetworkFetchResponseHeadPayload head,
            Stream bodyStream,
            HttpRequestMessage request)
        {
            ArgumentNullException.ThrowIfNull(head);

            var response = new HttpResponseMessage((HttpStatusCode)head.StatusCode)
            {
                ReasonPhrase = head.StatusText ?? string.Empty,
                RequestMessage = request,
                Content = new StreamContent(bodyStream),
            };

            if (head.Headers != null)
            {
                foreach (var kv in head.Headers)
                {
                    if (!response.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                    {
                        response.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }
            }

            return response;
        }

        public static string GetFetchMode(HttpRequestMessage request)
        {
            if (request != null && request.Headers.TryGetValues("Sec-Fetch-Mode", out var values))
            {
                foreach (var value in values)
                {
                    var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
                    if (normalized is "cors" or "no-cors" or "same-origin" or "navigate")
                    {
                        return normalized;
                    }
                }
            }

            return "cors";
        }

        /// <summary>Only web schemes may leave the process boundary; everything else is answered in-engine.</summary>
        public static bool IsRelayableUrl(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return false;
            }

            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }
    }
}
