using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Babelfin.Web;

/// <summary>A response body with compressed copies built once, including concurrent requests.</summary>
public sealed class Payload
{
    private readonly Lazy<byte[]> _brotli;
    private readonly Lazy<byte[]> _gzip;

    /// <summary>Initializes a new instance of the <see cref="Payload"/> class.</summary>
    /// <param name="raw">The uncompressed bytes.</param>
    /// <param name="contentType">The Content-Type.</param>
    /// <param name="compressible">Whether to compress for clients that accept it.</param>
    public Payload(byte[] raw, string contentType, bool compressible)
    {
        Raw = raw;
        ContentType = contentType;
        Compressible = compressible;
        _brotli = new Lazy<byte[]>(() => Compress(true), LazyThreadSafetyMode.ExecutionAndPublication);
        _gzip = new Lazy<byte[]>(() => Compress(false), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets the uncompressed bytes.</summary>
    public byte[] Raw { get; }

    /// <summary>Gets the Content-Type.</summary>
    public string ContentType { get; }

    /// <summary>Gets a value indicating whether the body may be compressed.</summary>
    public bool Compressible { get; }

    /// <summary>Gets the Brotli-compressed bytes.</summary>
    public byte[] Brotli => _brotli.Value;

    /// <summary>Gets the gzip-compressed bytes.</summary>
    public byte[] Gzip => _gzip.Value;

    private byte[] Compress(bool brotli)
    {
        using var output = new MemoryStream();
        using (Stream compressor = brotli
            ? new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)
            : new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(Raw);
        }

        return output.ToArray();
    }
}

/// <summary>Writes Babelfin's responses outside Jellyfin's compression middleware.</summary>
public static class ResponseWriter
{
    private static readonly ConcurrentDictionary<string, Payload> ResourceCache = new(StringComparer.Ordinal);

    /// <summary>Writes a payload with compression, ETag validation, and caching headers.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="payload">The body.</param>
    /// <param name="etagBase">The ETag without quotes or encoding suffix.</param>
    /// <param name="cacheControl">The Cache-Control value.</param>
    /// <param name="acceptEncoding">The original Accept-Encoding (null = request header).</param>
    /// <param name="ifNoneMatch">The original If-None-Match (null = request header).</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(HttpContext context, Payload payload, string etagBase, string cacheControl, string? acceptEncoding = null, string? ifNoneMatch = null)
    {
        var request = context.Request;
        var response = context.Response;
        if (payload.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            RestrictFraming(response);
        string? encoding = null;
        if (payload.Compressible)
        {
            // Inner plugins may already vary on cookies, languages, or other request headers.
            var vary = response.Headers.Vary.ToString();
            if (!Array.Exists(vary.Split(',', StringSplitOptions.TrimEntries), v => v == "*" || v.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
            {
                response.Headers.Append("Vary", "Accept-Encoding");
            }

            var header = acceptEncoding ?? request.Headers.AcceptEncoding.ToString();
            encoding = PickEncoding(header);
            if (encoding is null && Quality(header, "identity", 1) <= 0)
            {
                response.StatusCode = StatusCodes.Status406NotAcceptable;
                response.Headers.Remove("Content-Encoding");
                response.Headers.Remove("ETag");
                response.ContentLength = 0;
                return;
            }
        }

        var etag = "\"" + etagBase + (encoding is null ? string.Empty : "-" + encoding) + "\"";
        response.Headers.CacheControl = cacheControl;
        response.Headers.ETag = etag;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        var match = ifNoneMatch ?? request.Headers.IfNoneMatch.ToString();
        if (EntityTagHeaderValue.TryParseList(new[] { match }, out var tags)
            && tags is not null && tags.Any(tag => tag.Tag.Value == "*" || string.Equals(tag.Tag.Value, etag, StringComparison.Ordinal)))
        {
            response.Headers.Remove("Content-Encoding");
            response.ContentLength = null;
            response.StatusCode = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                ? StatusCodes.Status304NotModified : StatusCodes.Status412PreconditionFailed;
            return;
        }

        // Conditional responses don't need to build a compressed body.
        var body = encoding == "br" ? payload.Brotli : encoding == "gzip" ? payload.Gzip : payload.Raw;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = payload.ContentType;
        if (encoding is not null)
        {
            response.Headers.ContentEncoding = encoding;
        }
        else
        {
            response.Headers.Remove("Content-Encoding");
        }

        response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(request.Method))
        {
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    internal static void RestrictFraming(HttpResponse response)
    {
        var restricted = response.Headers.ContentSecurityPolicy.Any(policy => policy is not null
            && policy.Split(',', StringSplitOptions.TrimEntries).Any(group => group.Split(';', StringSplitOptions.TrimEntries)
                .Any(directive => directive.Equals("frame-ancestors 'self'", StringComparison.OrdinalIgnoreCase))));
        if (!restricted) response.Headers.Append("Content-Security-Policy", "frame-ancestors 'self'");
    }

    /// <summary>Picks the supported encoding with the highest acceptable quality; Brotli wins ties.</summary>
    internal static string? PickEncoding(string? header)
    {
        var br = Quality(header, "br", 0);
        var gzip = Quality(header, "gzip", 0);
        // An explicit identity preference participates in selection; an implicit one remains the fallback.
        var identity = Quality(header, "identity", 0);
        if (br > 0 && br >= gzip && br >= identity)
        {
            return "br";
        }

        return gzip > 0 && gzip >= identity ? "gzip" : null;
    }

    private static double Quality(string? header, string encoding, double fallback)
    {
        double? explicitQuality = null, wildcard = null;
        foreach (var part in (header ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(';', StringSplitOptions.TrimEntries);
            var q = 1.0;
            for (var i = 1; i < pieces.Length; i++)
            {
                if (pieces[i].StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                {
                    q = double.TryParse(pieces[i][2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        && double.IsFinite(parsed) && parsed >= 0 && parsed <= 1 ? parsed : 0;
                }
            }

            if (pieces[0].Equals(encoding, StringComparison.OrdinalIgnoreCase))
            {
                explicitQuality = q;
            }
            else if (pieces[0] == "*")
            {
                wildcard = q;
            }
        }

        // '*' only excludes identity at q=0; it doesn't assign identity a positive preference.
        return explicitQuality ?? (encoding == "identity" ? wildcard == 0 ? 0 : fallback : wildcard ?? fallback);
    }

    /// <summary>Reads an embedded resource once and keeps it.</summary>
    /// <param name="resource">The resource's logical name.</param>
    /// <param name="contentType">The Content-Type.</param>
    /// <param name="compressible">Whether to compress it.</param>
    /// <returns>The payload.</returns>
    public static Payload ReadResource(string resource, string contentType, bool compressible)
    {
        return ResourceCache.GetOrAdd(resource, name =>
        {
            using var stream = typeof(ResponseWriter).Assembly.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException("Missing embedded resource " + name);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return new Payload(memory.ToArray(), contentType, compressible);
        });
    }
}
