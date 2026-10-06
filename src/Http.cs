using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Capsule
{
    public sealed class HttpResult
    {
        public int Status;               // 0 when no response arrived
        public string Body = "";
        public long RetryAfterSeconds;   // from a Retry-After header; 0 if none
        public string Error = "";        // why no response arrived, e.g. "Timeout" or "NameResolutionFailure"
        public bool Truncated;           // the body was longer than the caller's limit, and was cut there
    }

    // HTTP requests that never throw. HTTPS goes through Windows (SChannel), so it trusts what Windows trusts,
    // including the root certificate of an antivirus that intercepts HTTPS (Norton's, for one).
    public static class Http
    {
        const int MaxBodyChars = 2 * 1024 * 1024;

        public static HttpResult Get(string url, IDictionary<string, string> headers, int timeoutMs)
        {
            return Send("GET", url, headers, null, timeoutMs);
        }

        // A GET whose body may be up to maxBodyChars long (a calendar feed, say); longer, it is cut there and Truncated set.
        public static HttpResult Get(string url, IDictionary<string, string> headers, int timeoutMs, int maxBodyChars)
        {
            return Send("GET", url, headers, null, timeoutMs, maxBodyChars);
        }

        // body: sent as UTF-8 JSON unless headers name another Content-Type; null for none.
        public static HttpResult Send(string method, string url, IDictionary<string, string> headers, string body, int timeoutMs)
        {
            return Send(method, url, headers, body, timeoutMs, MaxBodyChars);
        }

        public static HttpResult Send(string method, string url, IDictionary<string, string> headers, string body, int timeoutMs, int maxBodyChars)
        {
            var result = new HttpResult();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.UserAgent = "Capsule/0.1 (Windows)";
                request.Accept = "application/json";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.AllowAutoRedirect = false;   // only ever the endpoints Capsule names, never wherever a redirect points
                foreach (KeyValuePair<string, string> header in headers)
                {
                    if (header.Key == "Accept") request.Accept = header.Value;
                    else if (header.Key == "User-Agent") request.UserAgent = header.Value;
                    else if (header.Key == "Content-Type") request.ContentType = header.Value;
                    else request.Headers[header.Key] = header.Value;
                }
                if (body != null)
                {
                    // No Expect: 100-continue handshake: it costs a round trip for a small body, and HTTPS-intercepting
                    // antivirus (Norton, for one) can stall on it.
                    request.ServicePoint.Expect100Continue = false;
                    byte[] bytes = Files.Utf8.GetBytes(body);
                    if (string.IsNullOrEmpty(request.ContentType)) request.ContentType = "application/json; charset=utf-8";
                    request.ContentLength = bytes.Length;
                    using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    result.Status = (int)response.StatusCode;
                    result.Body = ReadBody(response, maxBodyChars, result);
                }
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    {
                        result.Status = (int)response.StatusCode;
                        result.RetryAfterSeconds = ParseRetryAfter(response.Headers["Retry-After"]);
                        // Defensive: HttpWebRequest already buffers error bodies inside GetResponse, so this read
                        // shouldn't fail today; if it ever does, the status and Retry-After read above must survive.
                        try { result.Body = ReadBody(response, maxBodyChars, result); }
                        catch (Exception) { }
                    }
                }
                else result.Error = e.Status.ToString();
            }
            catch (Exception e) { result.Error = e.GetType().Name; }
            return result;
        }

        // Retry-After as seconds, whether it is given as a number or an HTTP date. 0 when absent or unreadable.
        public static long ParseRetryAfter(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            long seconds;
            if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) return Math.Max(0, seconds);
            DateTime when;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                return Math.Max(0, (long)(when - DateTime.UtcNow).TotalSeconds);
            return 0;
        }

        // At most max characters; when there was more, Truncated is set on the result.
        static string ReadBody(HttpWebResponse response, int max, HttpResult result)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Files.Utf8))
            {
                var sb = new StringBuilder();
                var buffer = new char[8192];
                int n;
                while (sb.Length < max && (n = reader.Read(buffer, 0, Math.Min(buffer.Length, max - sb.Length))) > 0) sb.Append(buffer, 0, n);
                if (sb.Length >= max && reader.Read() >= 0) result.Truncated = true;   // not Peek: on a network stream it can say "no more" while more is on its way
                return sb.ToString();
            }
        }
    }
}
