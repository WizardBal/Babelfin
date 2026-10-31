using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Library;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class NativeSeriesInheritanceTests
{
    [Theory]
    [InlineData("ItemUpdated")]
    [InlineData("ItemRemoved")]
    public async Task MetadataEventDuringBlockedLookupCannotReinsertAStaleSeries(string eventName)
    {
        var old = new Series { Id = Guid.NewGuid(), Name = "Before", OriginalLanguage = "ja" };
        Series? current = old; var calls = 0;
        EventHandler<ItemChangeEventArgs>? changed = null;
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "add_" + eventName) changed += (EventHandler<ItemChangeEventArgs>)args[0]!;
            if (method.Name == "remove_" + eventName) changed -= (EventHandler<ItemChangeEventArgs>)args[0]!;
            if (method.Name == "GetCollectionFolders") return new List<Folder>();
            if (method.Name != "GetItemById") return null;
            var snapshot = current;
            if (Interlocked.Increment(ref calls) == 1) { entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); }
            return snapshot;
        }).Proxy;
        var previous = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = library;
            using var builder = new ItemContextBuilder(library, CallProxy.Create<ICollectionManager>().Proxy, new FakeLibraryFacts());
            var episode = new Episode { Id = Guid.NewGuid(), SeriesId = old.Id };
            var inflight = Task.Run(() => builder.Build(episode, ApiSafetyHost.Person()));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                current = eventName == "ItemRemoved" ? null : new Series { Id = old.Id, Name = "After", OriginalLanguage = "fr" };
                changed?.Invoke(null, new ItemChangeEventArgs { Item = current ?? old });
            }
            finally { resume.Set(); }
            await inflight;
            var next = builder.Build(episode, ApiSafetyHost.Person());
            Assert.Equal(current?.Name, next.SeriesName);
            Assert.Equal(current?.OriginalLanguage, next.OriginalLanguage);
            Assert.Equal(2, calls);
        }
        finally { resume.Set(); BaseItem.LibraryManager = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EpisodesWithUnstoredSeriesNameShareTheActualNativeLookup(bool seasons)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Shared series", OriginalLanguage = "ja" };
        var calls = 0;
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => Lookup(), "GetCollectionFolders" => new List<Folder>(), _ => null,
        }).Proxy;
        BaseItem Lookup() { calls++; return series; }
        var previous = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = library;
            using var builder = new ItemContextBuilder(library, CallProxy.Create<ICollectionManager>().Proxy, new FakeLibraryFacts());
            for (var i = 0; i < 20; i++)
            {
                BaseItem item = seasons ? new Season { Id = Guid.NewGuid(), SeriesId = series.Id } : new Episode { Id = Guid.NewGuid(), SeriesId = series.Id };
                var result = builder.Build(item, ApiSafetyHost.Person());
                Assert.Equal(series.Name, result.SeriesName);
                Assert.Equal("ja", result.OriginalLanguage);
            }
            Assert.Equal(1, calls);
        }
        finally { BaseItem.LibraryManager = previous; }
    }
    [Fact]
    public void NativeLibraryParentFallbackIsSharedAcrossEpisodesAndInvalidatedOnAMove()
    {
        var root = new AggregateFolder { Id = Guid.NewGuid() };
        var first = new CollectionFolder { Id = Guid.NewGuid(), ParentId = root.Id, Name = "First library" };
        var second = new CollectionFolder { Id = Guid.NewGuid(), ParentId = root.Id, Name = "Second library" };
        var series = new Series { Id = Guid.NewGuid(), ParentId = first.Id, OriginalLanguage = "ja" };
        var season = new Season { Id = Guid.NewGuid(), SeriesId = series.Id, ParentId = series.Id };
        var seriesCalls = 0;
        EventHandler<ItemChangeEventArgs>? changed = null;
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "add_ItemUpdated") changed += (EventHandler<ItemChangeEventArgs>)args[0]!;
            if (method.Name == "remove_ItemUpdated") changed -= (EventHandler<ItemChangeEventArgs>)args[0]!;
            if (method.Name == "GetCollectionFolders") return new List<Folder>();
            if (method.Name != "GetItemById") return null;
            var id = (Guid)args[0]!; if (id == series.Id) seriesCalls++;
            foreach (var item in new BaseItem[] { root, first, second, series, season }) if (item.Id == id) return item;
            return null;
        }).Proxy;
        var previous = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = library;
            Episode Episode() => new() { Id = Guid.NewGuid(), ParentId = season.Id, SeasonId = season.Id, SeriesId = series.Id };
            Assert.Equal(first.Id, Episode().GetTopParent()!.Id);
            var nativeSeriesCost = seriesCalls; seriesCalls = 0;
            using var builder = new ItemContextBuilder(library, CallProxy.Create<ICollectionManager>().Proxy, new FakeLibraryFacts());
            for (var i = 0; i < 20; i++) Assert.Equal(first.Id, Assert.Single(builder.Build(Episode(), ApiSafetyHost.Person()).LibraryIds));
            Assert.InRange(seriesCalls, 1, nativeSeriesCost + 1);
            series.ParentId = second.Id; changed?.Invoke(null, new ItemChangeEventArgs { Item = series });
            Assert.Equal(second.Id, Assert.Single(builder.Build(Episode(), ApiSafetyHost.Person()).LibraryIds));
        }
        finally { BaseItem.LibraryManager = previous; }
    }

}
