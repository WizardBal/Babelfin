using System;
using System.Collections.Generic;
using System.Linq;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class EpisodeSummaryLookupTests
{
    [Fact]
    public void EpisodeRowsCheckSharedAncestryOnceInsteadOfLookingUpEveryEpisode()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Visible show" };
        var season = new Season { Id = Guid.NewGuid(), SeriesId = series.Id, ParentId = series.Id };
        var episodes = Enumerable.Range(0, 1200).Select(i => new Episode
        {
            Id = Guid.NewGuid(), Name = "Episode " + i, SeriesId = series.Id, SeriesName = series.Name, SeasonId = season.Id, ParentId = season.Id,
        }).ToArray();
        var itemLookups = 0;
        var queries = new List<Guid[]>();
        var libraryLookups = 0;
        var folder = new CollectionFolder { Id = Guid.NewGuid(), Name = "Visible library" };
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetItemList")
            {
                var query = (InternalItemsQuery)args[0]!;
                queries.Add(query.ItemIds);
                return episodes.Cast<BaseItem>().Append(series).Append(season).Where(item => query.ItemIds.Contains(item.Id)).ToArray();
            }
            if (method.Name == "GetItemById") { itemLookups++; return (Guid)args[0]! == season.Id ? season : series; }
            if (method.Name == "GetCollectionFolders") { libraryLookups++; return new List<Folder> { folder }; }
            if (method.Name == "GetVirtualFolders") return new List<VirtualFolderInfo>();
            return null;
        }).Proxy;
        var previous = BaseItem.LibraryManager;
        var previousRecordings = Video.RecordingsManager;
        try
        {
            BaseItem.LibraryManager = library;
            Video.RecordingsManager = CallProxy.Create<IRecordingsManager>().Proxy;
            var doc = new PersonSettings
            {
                Overrides = episodes.Select(e => new ItemOverride { ItemId = e.Id.ToString("N"), Manual = new ManualMemory { SubtitlesOff = true } }).ToList(),
            };
            var user = ApiSafetyHost.Person();
            user.SetPermission(PermissionKind.EnableAllFolders, true);
            var rows = OverrideSummaries.Build(library, user, doc);
            Assert.Equal(episodes.Length, rows.Count);
            Assert.InRange(itemLookups, 0, 8);
            Assert.Equal(series.Name, Assert.Single(rows.Select(r => r.SeriesName).Distinct()));
            Assert.InRange(libraryLookups, 1, 2);
            Assert.Equal(4, queries.Count);
            Assert.All(queries, batch => Assert.InRange(batch.Length, 1, PersonLookups.BatchSize));
        }
        finally { BaseItem.LibraryManager = previous; Video.RecordingsManager = previousRecordings; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserFilteredItemsAndHiddenParentsNeverExposeEpisodeFacts(bool hiddenParent)
    {
        var user = ApiSafetyHost.Person();
        user.MaxParentalRatingScore = 10;
        var episode = new Episode { Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Name = "Restricted current name" };
        var calls = 0;
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name != "GetItemList") throw new InvalidOperationException("A hidden item must not trigger facts or library lookups");
            var query = (InternalItemsQuery)args[0]!;
            Assert.Same(user, query.User);
            Assert.Equal(10, query.MaxParentalRating!.Score);
            calls++;
            // The real Jellyfin user query applies these item restrictions; parent queries must use the same person.
            return hiddenParent && query.ItemIds.Contains(episode.Id) ? new BaseItem[] { episode } : Array.Empty<BaseItem>();
        }).Proxy;
        var previousRecordings = Video.RecordingsManager;
        try
        {
            Video.RecordingsManager = CallProxy.Create<IRecordingsManager>().Proxy;
            var row = Assert.Single(OverrideSummaries.Build(library, user, new PersonSettings
            {
                Overrides = new List<ItemOverride> { new() { ItemId = episode.Id.ToString("N"), Name = "Stored name" } },
            }));
            Assert.Equal("Stored name", row.Name);
            Assert.Null(row.Type);
            Assert.Null(row.LibraryId);
            Assert.Equal(hiddenParent ? 2 : 1, calls);
        }
        finally { Video.RecordingsManager = previousRecordings; }
    }

}
