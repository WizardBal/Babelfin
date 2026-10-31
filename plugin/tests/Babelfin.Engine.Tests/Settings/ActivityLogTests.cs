using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>A <see cref="DispatchProxy"/> whose calls go to a handler (fakes for large Jellyfin interfaces).</summary>
public class CallProxy : DispatchProxy
{
    /// <summary>Gets or sets the handler: (method, arguments) → result. Unhandled calls return the default value (a completed task for tasks).</summary>
    public Func<MethodInfo, object?[], object?>? Handler { get; set; }

    /// <summary>Gets the names of the methods called, in order.</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Creates a proxy for an interface.</summary>
    /// <typeparam name="T">The interface.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <returns>The proxy and its recorder.</returns>
    public static (T Proxy, CallProxy Recorder) Create<T>(Func<MethodInfo, object?[], object?>? handler = null)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, CallProxy>();
        var recorder = (CallProxy)(object)proxy;
        recorder.Handler = handler;
        return (proxy, recorder);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        lock (Calls)
        {
            Calls.Add(targetMethod.Name);
        }

        if (Handler is not null)
        {
            var result = Handler(targetMethod, args ?? Array.Empty<object?>());
            if (result is not null)
            {
                return result;
            }
        }

        var type = targetMethod.ReturnType;
        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>In-memory activity behavior.</summary>
public sealed class ActivityLogTests
{
    private static ActivityEntry Entry(string text, Guid? user = null, string kind = "test") => new(default, kind, user, null, text);

    [Fact]
    public void RingKeepsNewestAndFilters()
    {
        var log = new ActivityLog(); var user = Guid.NewGuid();
        for (var i = 0; i < ActivityLog.RingSize + 3; i++) log.Add(Entry(i.ToString(), user));
        log.Add(Entry("other", null, "another"));
        var entries = log.Query(int.MaxValue);
        Assert.Equal(ActivityLog.RingSize, entries.Count);
        Assert.Equal("other", entries[0].Text);
        Assert.Equal("5002", Assert.Single(log.Query(1, "TEST", user)).Text);
        Assert.Empty(log.Query(0));
    }

    [Fact]
    public void RemovedUserIsBlockedUntilAllowed()
    {
        var log = new ActivityLog(); var user = Guid.NewGuid();
        log.Add(Entry("old", user)); log.Add(Entry("server"));
        log.RemoveUser(user); log.Add(Entry("blocked", user));
        Assert.Equal("server", Assert.Single(log.Query(10)).Text);
        log.AllowUser(user); log.Add(Entry("new", user));
        Assert.Equal("new", Assert.Single(log.Query(10, userId: user)).Text);
    }

    [Fact]
    public void AddNormalizesTimeKindAndText()
    {
        var log = new ActivityLog();
        log.Add(new ActivityEntry(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), " ", null, null, null!));
        var entry = Assert.Single(log.Query(1));
        Assert.Equal(DateTimeKind.Utc, entry.At.Kind);
        Assert.Equal("other", entry.Kind);
        Assert.Equal(string.Empty, entry.Text);
        Assert.Null(Record.Exception(() => log.Add(null!)));
    }

    [Fact]
    public void ConcurrentAddsAndQueriesAreSafe()
    {
        var log = new ActivityLog();
        Parallel.For(0, 10000, i => { log.Add(Entry(i.ToString())); _ = log.Query(10); });
        Assert.Equal(ActivityLog.RingSize, log.Query(int.MaxValue).Count);
    }
}
