using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class ItemLookupCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectionEventDuringLookupCannotCacheTheOldMembership(bool profiles)
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var member = false;
        f.Collections = (_, _) =>
        {
            var snapshot = member;
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return snapshot ? new[] { f.Collection } : Array.Empty<BoxSet>();
        };
        var item = new Movie { Id = Guid.NewGuid(), Name = "Test film" };
        var person = Ctx.Person(new[] { "en" }, new[] { "en" });
        person.Overrides.Add(Ctx.Override(f.Collection.Id, OverrideScope.Collection, Ctx.Marked("fr")));
        var store = new FakePersonStore(); store.Save(person);
        using var resolver = new CachedProfileResolver(store, f.Builder, new FakeLibraryFacts(), f.Library, null, NullLogger<CachedProfileResolver>.Instance);
        var inflight = Task.Run(() => profiles ? resolver.Resolve(f.User, item).Context : f.Builder.Build(item, f.User));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            member = true;
            f.Changed(f.Collection);
        }
        finally { release.Set(); }
        await inflight;
        if (profiles)
        {
            var next = resolver.Resolve(f.User, item);
            Assert.Equal(f.Collection.Id, Assert.Single(next.Context.CollectionIds));
            Assert.Contains("fr", next.Profile.SubtitleLanguages);
        }
        else Assert.Equal(f.Collection.Id, Assert.Single(f.Builder.Build(item, f.User).CollectionIds));
    }

    [Fact]
    public void SeriesCollectionLookupsAreSharedAcrossEpisodesButIsolatedByPerson()
    {
        using var f = new Fixture();
        var series = Enumerable.Range(0, 10).Select(_ => new Series { Id = Guid.NewGuid() }).ToArray();
        foreach (var show in series) f.Items.Add(show.Id, show);
        var seriesCalls = 0;
        f.Collections = (person, id) =>
        {
            if (f.Items.ContainsKey(id)) seriesCalls++;
            return person.Id == f.User.Id ? new[] { f.Collection } : Array.Empty<BoxSet>();
        };
        var episodes = Enumerable.Range(0, 1000).Select(i => new Episode { Id = Guid.NewGuid(), SeriesId = series[i % 10].Id }).ToArray();
        foreach (var episode in episodes) Assert.Single(f.Builder.Build(episode, f.User).CollectionIds);
        Assert.Equal(10, seriesCalls);
        var other = new User("Other person", "Default", "Default") { Id = Guid.NewGuid() };
        Assert.Empty(f.Builder.Build(episodes[0], other).CollectionIds);
        Assert.Equal(11, seriesCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeriesAndSeasonUpdatesRetainOtherSeriesLookups(bool season)
    {
        using var f = new Fixture();
        var a = new Series { Id = Guid.NewGuid() }; var b = new Series { Id = Guid.NewGuid() };
        f.Items.Add(a.Id, a); f.Items.Add(b.Id, b);
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = a.Id };
        var second = new Episode { Id = Guid.NewGuid(), SeriesId = b.Id };
        var calls = new Dictionary<Guid, int>(); var member = false;
        f.Collections = (_, id) =>
        {
            calls[id] = calls.GetValueOrDefault(id) + 1;
            return member && id == a.Id ? new[] { f.Collection } : Array.Empty<BoxSet>();
        };
        f.Builder.Build(first, f.User); f.Builder.Build(second, f.User);
        var before = calls[second.Id];
        member = true;
        f.Changed(season ? new Season { Id = Guid.NewGuid(), SeriesId = a.Id } : a);
        Assert.Single(f.Builder.Build(first, f.User).CollectionIds);
        Assert.Empty(f.Builder.Build(second, f.User).CollectionIds);
        Assert.Equal(before, calls[second.Id]);
        Assert.Equal(1, calls[b.Id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledProfilesKeepOtherSeriesCachedWhenOneSeriesChanges(bool season)
    {
        using var f = new Fixture();
        var a = new Series { Id = Guid.NewGuid() }; var b = new Series { Id = Guid.NewGuid() };
        f.Items.Add(a.Id, a); f.Items.Add(b.Id, b);
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = a.Id };
        var second = new Episode { Id = Guid.NewGuid(), SeriesId = b.Id };
        var calls = new Dictionary<Guid, int>();
        var builder = CallProxy.Create<IItemContextBuilder>((_, args) =>
        {
            var item = (BaseItem)args[0]!;
            calls[item.Id] = calls.GetValueOrDefault(item.Id) + 1;
            return f.Builder.Build(item, (User)args[1]!);
        }).Proxy;
        var store = new FakePersonStore(); store.Save(Ctx.Person(new[] { "en" }, new[] { "en" }));
        using var resolver = new CachedProfileResolver(store, builder, new FakeLibraryFacts(), f.Library, null, NullLogger<CachedProfileResolver>.Instance);
        resolver.Resolve(f.User, first); resolver.Resolve(f.User, second);
        f.Changed(season ? new Season { Id = Guid.NewGuid(), SeriesId = a.Id } : a);
        resolver.Resolve(f.User, first); resolver.Resolve(f.User, second);
        Assert.Equal(2, calls[first.Id]);
        Assert.Equal(1, calls[second.Id]);
    }

    [Fact]
    public void SeriesCollectionCacheHasABoundedSize()
    {
        using var f = new Fixture();
        var first = new Series { Id = Guid.NewGuid() }; f.Items.Add(first.Id, first);
        var firstCalls = 0;
        f.Collections = (_, id) => { if (id == first.Id) firstCalls++; return Array.Empty<BoxSet>(); };
        void Build(Series series) => f.Builder.Build(new Episode { Id = Guid.NewGuid(), SeriesId = series.Id }, f.User);
        Build(first);
        for (var i = 0; i < ItemContextBuilder.MaxCacheEntries; i++)
        {
            var series = new Series { Id = Guid.NewGuid() }; f.Items.Add(series.Id, series); Build(series);
        }
        Build(first);
        Assert.Equal(2, firstCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesCollectionEventCannotReinsertAnInflightResult(bool disposal)
    {
        using var f = new Fixture();
        var series = new Series { Id = Guid.NewGuid() }; f.Items.Add(series.Id, series);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var member = false; var seriesCalls = 0;
        f.Collections = (_, id) =>
        {
            var snapshot = member;
            if (id == series.Id && ++seriesCalls == 1) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            return snapshot && id == series.Id ? new[] { f.Collection } : Array.Empty<BoxSet>();
        };
        var inflight = Task.Run(() => f.Builder.Build(new Episode { Id = Guid.NewGuid(), SeriesId = series.Id }, f.User));
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); member = true; if (disposal) f.Builder.Dispose(); else f.Changed(series); }
        finally { release.Set(); }
        await inflight;
        Assert.Single(f.Builder.Build(new Episode { Id = Guid.NewGuid(), SeriesId = series.Id }, f.User).CollectionIds);
        Assert.Equal(2, seriesCalls);
    }

    [Theory]
    [InlineData("series", false)]
    [InlineData("series", true)]
    [InlineData("owner", false)]
    [InlineData("owner", true)]
    [InlineData("folders", false)]
    [InlineData("folders", true)]
    [InlineData("parent", false)]
    [InlineData("parent", true)]
    public void LibraryReadFailuresMapToTemporaryUnavailabilityAndPlaybackPassesThrough(string target, bool io)
    {
        using var f = new Fixture();
        var seriesId = Guid.NewGuid();
        BaseItem item = target == "series" ? new Episode { Id = Guid.NewGuid(), SeriesId = seriesId }
            : new Movie { Id = Guid.NewGuid(), OwnerId = seriesId, ExtraType = target == "owner" ? MediaBrowser.Model.Entities.ExtraType.Trailer : null };
        f.LookupFailure = io ? new IOException("Library unavailable") : new InvalidOperationException("Library unavailable");
        f.FailureTarget = target;
        var error = Assert.ThrowsAny<Exception>(() => f.Builder.Build(item, f.User));
        var http = new DefaultHttpContext();
        var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ControllerActionDescriptor(), new ModelStateDictionary()), new List<IFilterMetadata>()) { Exception = error };
        new StorageApiErrorsAttribute().OnException(context);
        Assert.True(context.ExceptionHandled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(context.Result).StatusCode);
        Assert.Contains("library is temporarily unavailable", error.Message);
        var source = new MediaSourceInfo { Id = "test-source", DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = -1 };
        var inner = CallProxy.Create<IMediaSourceManager>((m, _) => m.Name == "GetStaticMediaSources" ? new[] { source } : null).Proxy;
        var decider = CallProxy.Create<ITrackDecider>((m, _) => { f.Builder.Build(item, f.User); return null; }).Proxy;
        var stats = new DecoratorStats();
        var wrapper = new BabelfinMediaSourceManager(inner, decider, stats, NullLogger<BabelfinMediaSourceManager>.Instance);
        var result = wrapper.GetStaticMediaSources(item, true, f.User);
        Assert.Equal(1, result[0].DefaultAudioStreamIndex);
        Assert.Equal(-1, result[0].DefaultSubtitleStreamIndex);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ILibraryManager _previous = BaseItem.LibraryManager;
        private EventHandler<ItemChangeEventArgs>? _changed;
        internal readonly Dictionary<Guid, BaseItem> Items = new();
        internal readonly User User = ApiSafetyHost.Person();
        internal readonly BoxSet Collection = new() { Id = Guid.NewGuid(), Name = "Test collection" };
        internal Func<User, Guid, IEnumerable<BoxSet>> Collections = (_, _) => Array.Empty<BoxSet>();
        internal Exception? LookupFailure;
        internal string? FailureTarget;
        internal readonly ILibraryManager Library;
        internal readonly ItemContextBuilder Builder;

        internal Fixture()
        {
            Library = CallProxy.Create<ILibraryManager>((m, args) =>
            {
                if (m.Name == "add_ItemUpdated") _changed += (EventHandler<ItemChangeEventArgs>)args[0]!;
                if (m.Name == "remove_ItemUpdated") _changed -= (EventHandler<ItemChangeEventArgs>)args[0]!;
                if (m.Name == "GetItemById")
                {
                    if (LookupFailure is not null && FailureTarget is "series" or "owner" or "parent") throw LookupFailure;
                    return Items.GetValueOrDefault((Guid)args[0]!);
                }
                if (m.Name == "GetCollectionFolders")
                {
                    if (LookupFailure is not null && FailureTarget == "folders") throw LookupFailure;
                    return new List<Folder>();
                }
                return null;
            }).Proxy;
            BaseItem.LibraryManager = Library;
            var collectionManager = CallProxy.Create<ICollectionManager>((m, args) => m.Name == "GetCollectionsContainingItem" ? Collections((User)args[0]!, (Guid)args[1]!) : null).Proxy;
            Builder = new ItemContextBuilder(Library, collectionManager, new FakeLibraryFacts());
        }

        internal void Changed(BaseItem item) => _changed?.Invoke(null, new ItemChangeEventArgs { Item = item });
        public void Dispose() { Builder.Dispose(); BaseItem.LibraryManager = _previous; }
    }
}
