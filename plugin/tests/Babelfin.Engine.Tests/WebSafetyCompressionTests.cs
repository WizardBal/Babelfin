using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests;

public sealed class WebSafetyCompressionTests
{
    private const string Html = "<html><body><script src=\"/companion-fixture/client.js\"></script>inner plugin</body></html>";

    [Fact]
    public void CacheReusesPlainBrotliAndGzipAndInvalidatesEveryInput()
    {
        var cache = new InjectedPageCache();
        var raw = Encoding.UTF8.GetBytes(Html);
        var first = cache.Get(raw, "text/html", "/jellyfin", "1")!;
        var same = cache.Get(raw.ToArray(), "text/html", "/jellyfin", "1")!;
        Assert.Same(first.Payload, same.Payload);
        Assert.Same(first.Payload.Raw, same.Payload.Raw);
        Assert.Same(first.Payload.Brotli, same.Payload.Brotli);
        Assert.Same(first.Payload.Gzip, same.Payload.Gzip);
        Assert.Contains("/companion-fixture/client.js", Encoding.UTF8.GetString(first.Payload.Raw));
        Assert.Contains("/jellyfin/Babelfin/client.js?v=1", Encoding.UTF8.GetString(first.Payload.Raw));
        Assert.Equal(first.Payload.Raw, Decompress(first.Payload.Brotli, true));
        Assert.Equal(first.Payload.Raw, Decompress(first.Payload.Gzip, false));
        Assert.NotSame(first, cache.Get(Encoding.UTF8.GetBytes(Html.Replace("inner plugin", "new inner plugin")), "text/html", "/jellyfin", "1"));
        Assert.NotSame(first, cache.Get(raw, "text/html; charset=utf-8", "/jellyfin", "1"));
        Assert.NotSame(first, cache.Get(raw, "text/html", "/other", "1"));
        Assert.NotSame(first, cache.Get(raw, "text/html", "/jellyfin", "2"));
    }

    [Fact]
    public async Task ConcurrentMissesReuseOnePayloadAndCompressedCopies()
    {
        var cache = new InjectedPageCache();
        var pages = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => cache.Get(Encoding.UTF8.GetBytes(Html), "text/html", "", "1")!)));
        Assert.All(pages, p => Assert.Same(pages[0], p));
        Assert.Equal(1, cache.Count);
        Assert.Equal(pages[0].Payload.Raw.Length + pages[0].Payload.Brotli.Length + pages[0].Payload.Gzip.Length, cache.Size);
    }

    [Fact]
    public void CacheEvictsLeastRecentlyUsedAndBoundsBytesAndOversizedPages()
    {
        var cache = new InjectedPageCache(maxEntries: 2);
        InjectedPage Page(string version) => cache.Get(Encoding.UTF8.GetBytes(Html), "text/html", "", version)!;
        var one = Page("1");
        var two = Page("2");
        Assert.Same(one, Page("1"));
        Page("3");
        Assert.Equal(2, cache.Count);
        Assert.Same(one, Page("1"));
        Assert.NotSame(two, Page("2"));
        var bytes = new InjectedPageCache(maxBytes: 1);
        Assert.NotNull(bytes.Get(Encoding.UTF8.GetBytes(Html), "text/html", "", "1"));
        Assert.Equal(0, bytes.Count);
        Assert.Equal(0, bytes.Size);
        var large = new InjectedPageCache(maxPageBytes: 1);
        Assert.NotNull(large.Get(Encoding.UTF8.GetBytes(Html), "text/html", "", "1"));
        Assert.Equal(0, large.Count);
        Assert.Null(cache.Get(Encoding.UTF8.GetBytes("no body tag"), "text/html", "", "1"));
    }

    [Fact]
    public void ByteBudgetEvictsEvenBeforeEntryLimit()
    {
        var probe = new InjectedPageCache();
        var raw = Encoding.UTF8.GetBytes(Html);
        probe.Get(raw, "text/html", "", "1");
        var limit = probe.Size + 20;
        var cache = new InjectedPageCache(maxEntries: 8, maxBytes: limit);
        var first = cache.Get(raw, "text/html", "", "1")!;
        cache.Get(raw, "text/html", "", "2");
        Assert.Equal(1, cache.Count);
        Assert.InRange(cache.Size, 1, limit);
        Assert.NotSame(first, cache.Get(raw, "text/html", "", "1"));
    }

    [Theory]
    [InlineData(200, "text/plain", "no HTML")]
    [InlineData(404, "text/html", "<html><body>not found</body></html>")]
    [InlineData(200, "text/html", "no body tag")]
    public async Task UninjectableInnerResponsePassesThrough(int status, string contentType, string body)
    {
        var middleware = Middleware(async context =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(body);
            context.Response.Headers.Vary = "Cookie";
            await context.Response.WriteAsync(body);
        });
        var get = Context();
        await middleware.InvokeAsync(get);
        Assert.Equal(status, get.Response.StatusCode);
        Assert.Equal(body, Encoding.UTF8.GetString(((MemoryStream)get.Response.Body).ToArray()));
        Assert.Equal("Cookie", get.Response.Headers.Vary.ToString());
        var head = Context(method: "HEAD");
        await middleware.InvokeAsync(head);
        Assert.Equal(get.Response.ContentLength, head.Response.ContentLength);
        Assert.Equal(0, head.Response.Body.Length);
    }

    [Theory]
    [InlineData("br, gzip", "br")]
    [InlineData("br;q=0, gzip", "gzip")]
    [InlineData("gzip;q=1, br;q=0.2", "gzip")]
    [InlineData("gzip;q=0.5, br;q=0.8", "br")]
    [InlineData("br;q=0, gzip;q=0", "")]
    [InlineData("*;q=0.5, br;q=0", "gzip")]
    [InlineData("*;q=0, gzip;q=0.5", "gzip")]
    [InlineData("BR;Q=0.5, GZIP;Q=0.5", "br")]
    [InlineData("identity;q=1, br;q=0.5", "")]
    [InlineData("br;q=NaN, gzip;q=2", "")]
    [InlineData("", "")]
    public async Task NegotiatesEncodingAndPreservesVary(string accept, string encoding)
    {
        var context = Context(accept: accept);
        context.Response.Headers.Vary = "Cookie, Accept-Language";
        var payload = new Payload(Encoding.UTF8.GetBytes(Html), "text/html", true);
        await ResponseWriter.WriteAsync(context, payload, "test", "no-cache");
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(encoding, context.Response.Headers.ContentEncoding.ToString());
        Assert.Contains("Cookie", context.Response.Headers.Vary.ToString());
        Assert.Contains("Accept-Language", context.Response.Headers.Vary.ToString());
        Assert.Contains("Accept-Encoding", context.Response.Headers.Vary.ToString());
        var sent = ((MemoryStream)context.Response.Body).ToArray();
        Assert.Equal(payload.Raw, encoding.Length == 0 ? sent : Decompress(sent, encoding == "br"));
        Assert.Equal(sent.Length, context.Response.ContentLength);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("br;q=0, gzip;q=0, identity;q=0")]
    public async Task RejectsNoAcceptableRepresentation(string accept)
    {
        // A bare wildcard is supported. q=0 excludes every representation.
        var context = Context(accept: accept == "*" ? "*;q=0" : accept);
        await ResponseWriter.WriteAsync(context, new Payload(Encoding.UTF8.GetBytes(Html), "text/html", true), "test", "no-cache");
        Assert.Equal(406, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData("\"test-br\"", 304)]
    [InlineData("W/\"test-br\"", 304)]
    [InlineData("\"other\", W/\"test-br\"", 304)]
    [InlineData("*", 304)]
    [InlineData("\"test\"", 200)]
    [InlineData("\"prefix-\\\"test-br\\\"\"", 200)]
    public async Task ConditionalRequestsMatchWholeRepresentationTags(string match, int status)
    {
        var context = Context(accept: "br", match: match);
        await ResponseWriter.WriteAsync(context, new Payload(Encoding.UTF8.GetBytes(Html), "text/html", true), "test", "no-cache");
        Assert.Equal(status, context.Response.StatusCode);
        if (status == 304)
        {
            Assert.Equal(0, context.Response.Body.Length);
            Assert.Null(context.Response.ContentLength);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task MiddlewareKeepsInnerPluginContentAndHeadHeadersAndRestoresRequest(string accept)
    {
        var calls = 0;
        var html = Html;
        var middleware = Middleware(async context =>
        {
            calls++;
            Assert.Equal("GET", context.Request.Method);
            Assert.Equal(string.Empty, context.Request.Headers.AcceptEncoding.ToString());
            Assert.Equal(string.Empty, context.Request.Headers.IfNoneMatch.ToString());
            Assert.Equal(string.Empty, context.Request.Headers.IfModifiedSince.ToString());
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.Vary = "Cookie";
            context.Response.Headers.ETag = "\"inner\"";
            context.Response.Headers.LastModified = "Thu, 01 Jan 1970 00:00:00 GMT";
            await context.Response.WriteAsync(html);
        }, "/jf");
        var get = Context(accept: accept, path: "/jf/web/index.html");
        await middleware.InvokeAsync(get);
        var etag = get.Response.Headers.ETag.ToString();
        var head = Context(method: "HEAD", accept: accept, path: "/jf/web/index.html");
        await middleware.InvokeAsync(head);
        Assert.Equal(get.Response.ContentLength, head.Response.ContentLength);
        Assert.Equal(get.Response.Headers.ContentEncoding, head.Response.Headers.ContentEncoding);
        Assert.Equal(etag, head.Response.Headers.ETag.ToString());
        Assert.Equal(0, head.Response.Body.Length);
        Assert.Equal("HEAD", head.Request.Method);
        var conditional = Context(accept: accept, path: "/jf/web/index.html", match: "W/" + etag);
        conditional.Request.Headers.IfModifiedSince = "Thu, 01 Jan 2099 00:00:00 GMT";
        await middleware.InvokeAsync(conditional);
        Assert.Equal(304, conditional.Response.StatusCode);
        Assert.Equal(accept, conditional.Request.Headers.AcceptEncoding.ToString());
        Assert.Equal("W/" + etag, conditional.Request.Headers.IfNoneMatch.ToString());
        Assert.NotEqual(string.Empty, conditional.Request.Headers.IfModifiedSince.ToString());
        html = Html.Replace("inner plugin", "changed inner plugin");
        var changed = Context(accept: accept, path: "/jf/web/index.html", match: etag);
        await middleware.InvokeAsync(changed);
        Assert.Equal(4, calls);
        Assert.Equal(200, changed.Response.StatusCode);
        Assert.NotEqual(etag, changed.Response.Headers.ETag.ToString());
        var sent = ((MemoryStream)changed.Response.Body).ToArray();
        var plain = Encoding.UTF8.GetString(accept.Length == 0 ? sent : Decompress(sent, accept == "br"));
        Assert.Contains("changed inner plugin", plain);
        Assert.Contains("/companion-fixture/client.js", plain);
        Assert.Contains("/jf/Babelfin/client.js", plain);
        Assert.Contains("Cookie", changed.Response.Headers.Vary.ToString());
        Assert.Equal(string.Empty, changed.Response.Headers.LastModified.ToString());
    }

    [Fact]
    public async Task VaryWildcardAndExistingEncodingAreNotDuplicated()
    {
        foreach (var vary in new[] { "*", "Cookie, accept-encoding" })
        {
            var context = Context(accept: "br");
            context.Response.Headers.Vary = vary;
            await ResponseWriter.WriteAsync(context, new Payload(Encoding.UTF8.GetBytes(Html), "text/html", true), "test", "no-cache");
            Assert.Equal(vary, context.Response.Headers.Vary.ToString());
        }
    }

    [Fact]
    public async Task RestoresRequestAndBodyWhenInnerPipelineThrows()
    {
        var context = Context(method: "HEAD", accept: "br", match: "\"old\"");
        var originalBody = context.Response.Body;
        var middleware = Middleware(_ => throw new InvalidOperationException("inner failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Same(originalBody, context.Response.Body);
        Assert.Equal("HEAD", context.Request.Method);
        Assert.Equal("br", context.Request.Headers.AcceptEncoding.ToString());
        Assert.Equal("\"old\"", context.Request.Headers.IfNoneMatch.ToString());
    }

    private static BabelfinMiddleware Middleware(RequestDelegate inner, string baseUrl = "") => new(inner,
        NullLogger<BabelfinMiddleware>.Instance, new HostSettings { Resolved = true, BaseUrl = baseUrl },
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["hostwebclient"] = "true" }).Build());

    private static DefaultHttpContext Context(string method = "GET", string accept = "", string path = "/web/", string match = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.AcceptEncoding = accept;
        context.Request.Headers.IfNoneMatch = match;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] Decompress(byte[] bytes, bool br)
    {
        using var input = new MemoryStream(bytes);
        using Stream stream = br ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
