using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Babelfin.Web;

/// <summary>
/// Adds Babelfin's script tag to the web client's index.html. It runs outermost, calls the
/// rest of the pipeline (Jellyfin's static files, or another plugin that serves the page
/// itself), buffers that page, inserts one script tag before &lt;/body&gt;, and answers with its own ETag,
/// 304 and compression. Every other request passes straight through, unbuffered.
/// </summary>
public sealed class BabelfinMiddleware
{
    private readonly RequestDelegate _next;
    private readonly InjectedPageCache _pages = new();
    private readonly ILogger<BabelfinMiddleware> _logger;
    private readonly bool _enabled;
    private readonly bool _redirectsToHttps;
    private readonly string _webRoot;
    private readonly string _baseUrl;

    /// <summary>
    /// Initializes a new instance of the <see cref="BabelfinMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="host">Server settings read at startup.</param>
    /// <param name="configuration">The app configuration (for Jellyfin's "hostwebclient" switch).</param>
    public BabelfinMiddleware(RequestDelegate next, ILogger<BabelfinMiddleware> logger, HostSettings host, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _redirectsToHttps = host.RedirectsToHttps;
        _baseUrl = host.BaseUrl;
        _webRoot = host.BaseUrl + "/web/";

        // Same switch Jellyfin's Startup.Configure uses to decide whether it serves /web at all.
        var hostsWebClient = configuration.GetValue<bool>("hostwebclient");
        _enabled = host.Resolved && hostsWebClient;
        if (!_enabled)
        {
            _logger.LogInformation("[Babelfin] Web client injection not active (web client hosted: {Hosted}, server settings read: {Resolved})", hostsWebClient, host.Resolved);
        }
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        // Jellyfin serves the embedded configuration HTML itself, outside Babelfin's asset writer.
        if (request.Path.Equals(_baseUrl + "/web/ConfigurationPage", StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Query["name"].ToString(), "Babelfin", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                ResponseWriter.RestrictFraming(context.Response);
                return Task.CompletedTask;
            });
            await _next(context).ConfigureAwait(false);
            if (!context.Response.HasStarted) ResponseWriter.RestrictFraming(context.Response);
            return;
        }
        if (!_enabled
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || (_redirectsToHttps && !request.IsHttps))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = request.Path.Value ?? string.Empty;
        if (!path.Equals(_webRoot, StringComparison.OrdinalIgnoreCase)
            && !path.Equals(_webRoot + "index.html", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        await InjectAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks br or gzip from an Accept-Encoding header, honouring q=0.
    /// </summary>
    /// <param name="header">The header value.</param>
    /// <returns>"br", "gzip" or null.</returns>
    internal static string? PickEncoding(string? header) => ResponseWriter.PickEncoding(header);

    /// <summary>
    /// Inserts the script tag before the last &lt;/body&gt; unless the page already loads it.
    /// </summary>
    /// <param name="html">The page.</param>
    /// <param name="baseUrl">The server's base URL ("" or "/prefix").</param>
    /// <param name="version">The plugin version.</param>
    /// <returns>The page with the tag, or null when there is no &lt;/body&gt;.</returns>
    internal static string? Inject(string html, string baseUrl, string version)
    {
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < 0)
        {
            return null;
        }

        if (html.Contains("/Babelfin/client.js", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var tag = "\n<script src=\"" + baseUrl + "/Babelfin/client.js?v=" + Uri.EscapeDataString(version) + "\" defer></script>\n";
        return html.Insert(bodyEnd, tag);
    }

    private async Task InjectAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var isHead = HttpMethods.IsHead(request.Method);

        // Remember what the browser asked for, then make the inner pipeline answer with a full, plain 200.
        var acceptEncoding = request.Headers.AcceptEncoding;
        var ifNoneMatch = request.Headers.IfNoneMatch;
        var ifModifiedSince = request.Headers.IfModifiedSince;
        request.Headers.Remove("Accept-Encoding");
        request.Headers.Remove("If-None-Match");
        request.Headers.Remove("If-Modified-Since");

        // A HEAD must carry the same headers as the GET, so fetch the page as a GET and drop the body at the end.
        var originalMethod = request.Method;
        if (isHead)
        {
            request.Method = HttpMethods.Get;
        }

        var originalBody = response.Body;
        await using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = originalBody;
            request.Method = originalMethod;
            RestoreHeader(request, "Accept-Encoding", acceptEncoding);
            RestoreHeader(request, "If-None-Match", ifNoneMatch);
            RestoreHeader(request, "If-Modified-Since", ifModifiedSince);
        }

        byte[] inner = buffer.ToArray();

        try
        {
            if (response.HasStarted)
            {
                // Someone already sent headers; all we can do is pass the body on.
                await WriteBodyAsync(context, inner, isHead).ConfigureAwait(false);
                return;
            }

            var contentType = response.ContentType ?? string.Empty;
            var innerEncoding = response.Headers.ContentEncoding.ToString();
            InjectedPage? page = null;
            if (response.StatusCode == StatusCodes.Status200OK
                && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(innerEncoding))
            {
                page = _pages.Get(inner, contentType, _baseUrl, Plugin.VersionString);
            }

            if (page is null)
            {
                await PassThroughAsync(context, inner, isHead).ConfigureAwait(false);
                return;
            }

            response.Headers.Remove("ETag");
            response.Headers.Remove("Content-Encoding");
            response.Headers.Remove("Last-Modified");
            response.Headers.Remove("Accept-Ranges");
            response.Headers.ContentLength = null;
            await ResponseWriter.WriteAsync(context, page.Payload, page.ETag, "no-cache", acceptEncoding.ToString(), ifNoneMatch.ToString()).ConfigureAwait(false);
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser went away mid-response; nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't add Babelfin to {Path}; sending the page unchanged", request.Path.Value);
            if (response.HasStarted)
            {
                return;
            }

            if (inner.Length > 0)
            {
                await PassThroughAsync(context, inner, isHead).ConfigureAwait(false);
                return;
            }

            // Nothing buffered: let Jellyfin answer the original request directly.
            RestoreHeader(request, "Accept-Encoding", acceptEncoding);
            RestoreHeader(request, "If-None-Match", ifNoneMatch);
            RestoreHeader(request, "If-Modified-Since", ifModifiedSince);
            await _next(context).ConfigureAwait(false);
        }
    }

    private static async Task PassThroughAsync(HttpContext context, byte[] inner, bool isHead)
    {
        if (context.Response.Headers.ContentLength.HasValue)
        {
            context.Response.Headers.ContentLength = inner.Length;
        }

        await WriteBodyAsync(context, inner, isHead).ConfigureAwait(false);
    }

    private static async Task WriteBodyAsync(HttpContext context, byte[] inner, bool isHead)
    {
        if (!isHead && inner.Length > 0)
        {
            await context.Response.Body.WriteAsync(inner, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static void RestoreHeader(HttpRequest request, string name, StringValues value)
    {
        if (!StringValues.IsNullOrEmpty(value))
        {
            request.Headers[name] = value;
        }
    }
}

/// <summary>Only page bytes are reused; every request still runs the inner pipeline and keeps its current headers.</summary>
internal sealed record InjectedPage(Payload Payload, string ETag);

/// <summary>A small LRU bounded by entry count, uncompressed page size, and actual bytes of all three representations.</summary>
internal sealed class InjectedPageCache
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Hash, string ContentType, string Base, string Version), LinkedListNode<(InjectedPage Page, int Size)>> _entries = new();
    private readonly LinkedList<(InjectedPage Page, int Size)> _lru = new();
    private readonly int _maxEntries;
    private readonly int _maxBytes;
    private readonly int _maxPageBytes;
    private int _bytes;

    internal InjectedPageCache(int maxEntries = 8, int maxBytes = 8 * 1024 * 1024, int maxPageBytes = 1024 * 1024)
    {
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _maxPageBytes = maxPageBytes;
    }

    internal int Count { get { lock (_gate) { return _entries.Count; } } }

    internal int Size { get { lock (_gate) { return _bytes; } } }

    internal InjectedPage? Get(byte[] inner, string contentType, string baseUrl, string version)
    {
        var key = (Convert.ToHexString(SHA256.HashData(inner)), contentType, baseUrl, version);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddLast(hit);
                return hit.Value.Page;
            }

            var injected = BabelfinMiddleware.Inject(Encoding.UTF8.GetString(inner), baseUrl, version);
            if (injected is null)
            {
                return null;
            }

            var raw = Encoding.UTF8.GetBytes(injected);
            var page = new InjectedPage(new Payload(raw, contentType, compressible: true),
                "bf-" + Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
            if (raw.Length > _maxPageBytes || _maxEntries <= 0)
            {
                return page;
            }

            // Populate both compressed copies once while holding the miss lock. Accounting stays exact,
            // and concurrent misses for the same HTML cannot repeat injection or compression.
            var size = raw.Length + page.Payload.Brotli.Length + page.Payload.Gzip.Length;
            if (size > _maxBytes)
            {
                return page;
            }

            while (_entries.Count >= _maxEntries || _bytes + size > _maxBytes)
            {
                var oldest = _lru.First!;
                foreach (var entry in _entries)
                {
                    if (ReferenceEquals(entry.Value, oldest))
                    {
                        _entries.Remove(entry.Key);
                        break;
                    }
                }

                _lru.RemoveFirst();
                _bytes -= oldest.Value.Size;
            }

            _entries.Add(key, _lru.AddLast((page, size)));
            _bytes += size;
            return page;
        }
    }
}
