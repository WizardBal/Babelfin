using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Xunit;
using EnginePreference = Jellyfin.Plugin.Babelfin.Engine.Selection.Preference;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>the show and movie overrides list, the dialog's target, "remove all" and a settings save that keeps the stored overrides.</summary>
public class OverrideListTests
{
    private static readonly Guid Movie = new("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherSeries = new("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid Episode = new("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid OtherEpisode = new("a0000000-0000-0000-0000-000000000004");
    private static readonly Guid Gone = new("a0000000-0000-0000-0000-000000000005");
    private static readonly DateTime Earlier = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Remembered = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    private static readonly User Person = new("Test", "Default", "Default") { Id = Ids.Person };

    private static string Json(object value) => JsonSerializer.Serialize(value, BabelfinJson.Options);

    // How Jellyfin's MVC writes an API answer: PascalCase, enums as names, nulls left out (JsonDefaults.PascalCaseOptions, which AddJellyfinApi copies).
    private static string ApiJson(object value) => JsonSerializer.Serialize(value, JsonDefaults.PascalCaseOptions);

    private static SettingsLayer Format(FormatPreference format) => new() { Format = format };

    private static ManualMemory Manual() => new()
    {
        Audio = new RememberedTrack { Language = "ja", Kind = "normal", Codec = "aac", Channels = 2 },
        SubtitlesOff = true,
        At = Remembered,
        Client = "Jellyfin Web",
    };

    /// <summary>Four stored overrides: settings only, settings and a remembered change, a remembered change only, and an older episode one.</summary>
    private static PersonSettings StoredWithOverrides()
    {
        var doc = Ctx.Person();
        doc.UpdatedAt = Earlier;
        doc.Seed = new SeedRecord { SeededAt = Earlier, Confirmed = true };
        doc.Overrides = new List<ItemOverride>
        {
            Ctx.Override(Ids.Series, OverrideScope.Series, Format(FormatPreference.TextOnly)),
            Ctx.Override(Movie, OverrideScope.Title, Format(FormatPreference.PreferText), Manual()),
            Ctx.Override(OtherSeries, OverrideScope.Series, new SettingsLayer(), Manual()),
            Ctx.Override(Episode, OverrideScope.Title, Format(FormatPreference.TextOnly)),
        };
        doc.Overrides[0].Name = "Skyward Lantern Voyage";
        doc.Overrides[0].UpdatedAt = Earlier;
        doc.Overrides[1].Name = "Paper Moon Ferry";
        doc.Overrides[1].UpdatedAt = Remembered;
        doc.Overrides[2].Name = "Brass Meridian";
        doc.Overrides[2].UpdatedAt = Remembered;
        doc.Overrides[3].Name = "Episode 1";
        doc.Overrides[3].UpdatedAt = Earlier;
        return doc;
    }

    // ----- Settings saves that keep the stored overrides -----

    [Fact]
    public void Merge_KeepOverrides_KeepsTheStoredOverridesExactly()
    {
        var stored = StoredWithOverrides();
        var body = stored.Clone();
        body.Everyday.Understood = new[] { "fr" };
        body.Overrides.RemoveAt(0);
        body.Overrides.RemoveAt(1);
        body.Overrides.Add(Ctx.Override(Gone, OverrideScope.Title, Format(FormatPreference.TextOnly)));
        body.Overrides[0].Settings = Format(FormatPreference.TextOnly);

        var merged = PersonDocuments.Merge(stored, body, takeLocks: false, Person, keepOverrides: true);

        Assert.Equal(Json(stored.Overrides), Json(merged.Overrides));
        Assert.Equal(new[] { "fr" }, merged.Everyday.Understood);
        Assert.Equal(Remembered, merged.Overrides[1].Manual!.At);
        Assert.Equal(Remembered, merged.Overrides[2].UpdatedAt);
        Assert.True(merged.Overrides[2].Settings.IsEmpty);
        Assert.NotNull(merged.Overrides[2].Manual);
    }

    [Fact]
    public void Merge_KeepOverrides_DoesNotLookAtTheBodysOverrides()
    {
        var stored = StoredWithOverrides();
        var body = stored.Clone();
        body.Overrides = new List<ItemOverride> { new() { ItemId = "not an id", Settings = new SettingsLayer() } };

        // With the flag the body's overrides are simply not read.
        var merged = PersonDocuments.Merge(stored, body, takeLocks: false, Person, keepOverrides: true);

        Assert.Equal(Json(stored.Overrides), Json(merged.Overrides));

        // Without it the same body never reaches the merge: the validator, which the save runs first, refuses it and says where. With the flag the save
        // empties the body's overrides before validating, so the body passes.
        var refused = SettingsValidator.ValidatePerson(body, LanguageTable.Default, includeLocks: false);
        Assert.Contains(refused, e => e.Path == "overrides[0].itemId");
        body.Overrides = new List<ItemOverride>();
        Assert.DoesNotContain(SettingsValidator.ValidatePerson(body, LanguageTable.Default, includeLocks: false), e => e.Path.StartsWith("overrides", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_KeepOverrides_ReturnsCopies()
    {
        var stored = StoredWithOverrides();
        var merged = PersonDocuments.Merge(stored, stored.Clone(), takeLocks: false, Person, keepOverrides: true);

        merged.Overrides[0].Settings.Format = FormatPreference.PreferText;
        merged.Overrides[1].Manual!.Audio!.Language = "ko";
        merged.Overrides.RemoveAt(3);

        Assert.Equal(FormatPreference.TextOnly, stored.Overrides[0].Settings.Format);
        Assert.Equal("ja", stored.Overrides[1].Manual!.Audio!.Language);
        Assert.Equal(4, stored.Overrides.Count);
    }

    [Fact]
    public void Merge_KeepOverrides_StillTakesTheOtherPartsFromTheBody()
    {
        var stored = StoredWithOverrides();
        var body = stored.Clone();
        body.Everyday.AudioChain = Ctx.Chain(Ctx.Lang("ja"));
        body.Libraries.Add(Ctx.MyLibrary(Ids.Anime, Format(FormatPreference.TextOnly)));
        body.HandleNextEpisode = true;
        body.Enabled = false;

        var merged = PersonDocuments.Merge(stored, body, takeLocks: false, Person, keepOverrides: true);

        Assert.Equal("ja", merged.Everyday.AudioChain![0].Language);
        Assert.Single(merged.Libraries);
        Assert.True(merged.HandleNextEpisode);
        Assert.False(merged.Enabled);
        Assert.Equal(4, merged.Overrides.Count);
    }

    [Fact]
    public void Merge_WithoutKeepOverrides_TheBodyWinsAsBefore()
    {
        var stored = StoredWithOverrides();
        var body = stored.Clone();
        body.Overrides.RemoveAll(o => PersonSettings.SameId(o.ItemId, Ids.Series) || PersonSettings.SameId(o.ItemId, Episode));
        body.Overrides.Single(o => PersonSettings.SameId(o.ItemId, Movie)).Settings = Format(FormatPreference.TextOnly);
        body.Overrides.Add(Ctx.Override(Gone, OverrideScope.Title, Format(FormatPreference.TextOnly)));

        var merged = PersonDocuments.Merge(stored, body, takeLocks: false, Person);

        Assert.Equal(new[] { Movie, OtherSeries, Gone }.Select(Ids.N), merged.Overrides.Select(o => o.ItemId));
        Assert.Equal(FormatPreference.TextOnly, merged.Overrides[0].Settings.Format);
        Assert.Equal(Remembered, merged.Overrides[0].Manual!.At);
        Assert.True(merged.Overrides[1].Settings.IsEmpty);
        Assert.NotNull(merged.Overrides[1].Manual);
        Assert.Null(merged.Overrides[2].Manual);
    }

    // ----- Remove all, remove one -----

    [Fact]
    public void WithoutOverrides_RemovesEveryOverrideAndNothingElse()
    {
        var stored = StoredWithOverrides();
        stored.BurnedIn.Add(new BurnedInFlag { ItemId = Ids.N(Movie), Scope = OverrideScope.Title, Languages = new List<string> { "en" } });
        var before = Json(stored);

        var copy = PersonDocuments.WithoutOverrides(stored);

        Assert.NotNull(copy);
        Assert.Empty(copy!.Overrides);
        Assert.Single(copy.BurnedIn);
        Assert.Equal(stored.Everyday.Understood, copy.Everyday.Understood);
        Assert.Equal(before, Json(stored));
    }

    [Fact]
    public void WithoutOverrides_ReturnsNullWhenThereIsNothingToRemove()
    {
        Assert.Null(PersonDocuments.WithoutOverrides(null));
        Assert.Null(PersonDocuments.WithoutOverrides(Ctx.Person()));
    }

    [Fact]
    public void WithoutOverride_RemovesTheWholeEntryWithItsRememberedChange()
    {
        var stored = StoredWithOverrides();

        var copy = PersonDocuments.WithoutOverride(stored, Movie, out var removed);

        Assert.NotNull(copy);
        Assert.Equal(new[] { Ids.Series, OtherSeries, Episode }.Select(Ids.N), copy!.Overrides.Select(o => o.ItemId));
        Assert.Equal(Ids.N(Movie), removed!.ItemId);
        Assert.NotNull(removed.Manual);
        Assert.Equal(4, stored.Overrides.Count);
    }

    [Fact]
    public void WithoutOverride_RemovesAnEntryThatHoldsOnlyARememberedChange()
    {
        var copy = PersonDocuments.WithoutOverride(StoredWithOverrides(), OtherSeries, out var removed);

        Assert.DoesNotContain(copy!.Overrides, o => PersonSettings.SameId(o.ItemId, OtherSeries));
        Assert.True(removed!.Settings.IsEmpty);
    }

    [Fact]
    public void WithoutOverride_ReturnsNullWhenThePersonHasNoSuchOverride()
    {
        Assert.Null(PersonDocuments.WithoutOverride(StoredWithOverrides(), Gone, out var removed));
        Assert.Null(removed);
        Assert.Null(PersonDocuments.WithoutOverride(null, Movie, out removed));
        Assert.Null(removed);
    }

    // ----- Saves that meet a removal or a recorded track change: read, changed and written as one step under the store's lock -----

    private static FakePersonStore StoreHolding(PersonSettings doc)
    {
        var store = new FakePersonStore();
        store.Save(doc);
        return store;
    }

    private static string[] IdsOf(PersonSettings doc) => doc.Overrides.Select(o => o.ItemId).ToArray();

    [Fact]
    public void SaveMerged_MergesIntoTheDocumentTheStoreHoldsWhenTheUpdateRuns_NotTheCopyReadEarlier()
    {
        var store = StoreHolding(StoredWithOverrides());
        var readEarlier = store.TryGet(Ids.Person)!.Clone();
        var body = readEarlier.Clone();
        body.Everyday.Understood = new[] { "fr" };

        // The person removes a row (or an admin does) after the save read the document and before it writes.
        Assert.True(store.Update(Ids.Person, current => PersonDocuments.WithoutOverride(current, Movie, out _)));
        var seen = new SeenStore(store);

        var result = PersonDocuments.SaveMerged(seen, readEarlier, body, takeLocks: false, Person, keepOverrides: true);

        Assert.Same(seen.Seen, seen.Latest);
        Assert.DoesNotContain(IdsOf(result.Before), id => PersonSettings.SameId(id, Movie));
        Assert.Equal(new[] { Ids.Series, OtherSeries, Episode }.Select(Ids.N), IdsOf(store.TryGet(Ids.Person)!));
        Assert.Equal(new[] { "fr" }, store.TryGet(Ids.Person)!.Everyday.Understood);
        Assert.Equal(IdsOf(result.Merged), IdsOf(store.TryGet(Ids.Person)!));
    }

    [Fact]
    public void SaveMerged_ARemoveAllMeanwhile_IsNotUndoneByASaveThatKeepsOverrides()
    {
        var store = StoreHolding(StoredWithOverrides());
        var readEarlier = store.TryGet(Ids.Person)!.Clone();
        var body = readEarlier.Clone();
        body.Enabled = false;
        Assert.True(store.Update(Ids.Person, PersonDocuments.WithoutOverrides));

        PersonDocuments.SaveMerged(store, readEarlier, body, takeLocks: false, Person, keepOverrides: true);

        var stored = store.TryGet(Ids.Person)!;
        Assert.Empty(stored.Overrides);
        Assert.False(stored.Enabled);
    }

    // A remembered track change recorded after the read stays, whether or not the body brings overrides: it is in the document the merge starts from.
    [Fact]
    public void SaveMerged_ARememberedChangeRecordedMeanwhile_Stays()
    {
        foreach (var keepOverrides in new[] { true, false })
        {
            var store = StoreHolding(StoredWithOverrides());
            var readEarlier = store.TryGet(Ids.Person)!.Clone();
            var body = readEarlier.Clone();
            Assert.True(store.Update(Ids.Person, current =>
            {
                var changed = current!.Clone();
                changed.Overrides.Add(Ctx.Override(Gone, OverrideScope.Title, new SettingsLayer(), Manual()));
                return changed;
            }));

            SaveBody(store, readEarlier, body, keepOverrides);

            var added = store.TryGet(Ids.Person)!.FindOverride(Gone);
            Assert.NotNull(added);
            Assert.NotNull(added!.Manual);
            Assert.True(added.Settings.IsEmpty);
        }
    }

    private static void SaveBody(IPersonStore store, PersonSettings readEarlier, PersonSettings body, bool keepOverrides)
        => PersonDocuments.SaveMerged(store, readEarlier, body, takeLocks: false, Person, keepOverrides);

    [Fact]
    public void SaveMerged_WhenTheDocumentIsGoneWhenTheUpdateRuns_UsesTheCopyReadEarlier()
    {
        var store = new FakePersonStore();
        var readEarlier = StoredWithOverrides();
        var body = readEarlier.Clone();
        body.Everyday.Understood = new[] { "de" };

        var result = PersonDocuments.SaveMerged(store, readEarlier, body, takeLocks: false, Person, keepOverrides: true);

        Assert.Same(readEarlier, result.Before);
        Assert.Equal(IdsOf(readEarlier), IdsOf(store.TryGet(Ids.Person)!));
        Assert.Equal(new[] { "de" }, store.TryGet(Ids.Person)!.Everyday.Understood);
    }

    [Fact]
    public void SaveMerged_TakesTheLocksFromTheBodyOnlyForTheAdmin()
    {
        var store = StoreHolding(StoredWithOverrides());
        var readEarlier = store.TryGet(Ids.Person)!.Clone();
        var body = readEarlier.Clone();
        body.Locks = new SettingsLayer { Format = FormatPreference.TextOnly };

        PersonDocuments.SaveMerged(store, readEarlier, body, takeLocks: false, Person, keepOverrides: true);
        Assert.Null(store.TryGet(Ids.Person)!.Locks.Format);

        PersonDocuments.SaveMerged(store, readEarlier, body, takeLocks: true, Person, keepOverrides: true);
        Assert.Equal(FormatPreference.TextOnly, store.TryGet(Ids.Person)!.Locks.Format);
    }

    // The documents the merge started from are never changed (the store hands out its cached instance), and the change runs once, inside Update.
    [Fact]
    public void SaveMerged_ChangesNeitherTheStoredInstanceNorTheCopyReadEarlier_AndAsksTheStoreOnce()
    {
        var store = StoreHolding(StoredWithOverrides());
        var cached = store.TryGet(Ids.Person)!;
        var readEarlier = cached.Clone();
        var body = readEarlier.Clone();
        body.Everyday.Understood = new[] { "fr" };
        body.Overrides.Clear();
        var cachedBefore = Json(cached);
        var earlierBefore = Json(readEarlier);
        var seen = new SeenStore(store);

        PersonDocuments.SaveMerged(seen, readEarlier, body, takeLocks: false, Person, keepOverrides: false);

        Assert.Equal(1, seen.Updates);
        Assert.Equal(cachedBefore, Json(cached));
        Assert.Equal(earlierBefore, Json(readEarlier));
    }

    [Fact]
    public void WithOverride_AddsAnEntry_AndLeavesTheStoredDocumentAlone()
    {
        var stored = StoredWithOverrides();
        var before = Json(stored);
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var layer = Format(FormatPreference.TextOnly);

        var copy = PersonDocuments.WithOverride(stored, Gone, OverrideScope.Title, "Copper Kite", layer, now, out var saved);

        Assert.NotNull(copy);
        Assert.Equal(before, Json(stored));
        Assert.Equal(IdsOf(stored).Append(Ids.N(Gone)), IdsOf(copy!));
        Assert.Same(copy!.Overrides[^1], saved);
        Assert.Equal(Ids.N(Gone), saved!.ItemId);
        Assert.Equal(OverrideScope.Title, saved.Scope);
        Assert.Equal("Copper Kite", saved.Name);
        Assert.Equal(now, saved.UpdatedAt);
        Assert.Equal(FormatPreference.TextOnly, saved.Settings.Format);
        Assert.NotSame(layer, saved.Settings);
        Assert.Null(saved.Manual);
    }

    [Fact]
    public void WithOverride_ChangesAnEntryInPlace_KeepingItsRememberedChange()
    {
        var stored = StoredWithOverrides();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        var copy = PersonDocuments.WithOverride(stored, Movie, OverrideScope.Title, "Paper Moon Ferry (2001)", Format(FormatPreference.TextOnly), now, out var saved);

        Assert.Equal(IdsOf(stored), IdsOf(copy!));
        Assert.Equal(FormatPreference.TextOnly, saved!.Settings.Format);
        Assert.Equal("Paper Moon Ferry (2001)", saved.Name);
        Assert.Equal(now, saved.UpdatedAt);
        Assert.Equal(Remembered, saved.Manual!.At);
        Assert.Equal(FormatPreference.PreferText, stored.Overrides[1].Settings.Format);
    }

    [Fact]
    public void WithOverride_ARememberedOnlyEntryBecomesExplicit()
    {
        var copy = PersonDocuments.WithOverride(StoredWithOverrides(), OtherSeries, OverrideScope.Series, "Brass Meridian", Format(FormatPreference.TextOnly), Remembered, out var saved);

        Assert.Equal(4, copy!.Overrides.Count);
        Assert.False(saved!.Settings.IsEmpty);
        Assert.NotNull(saved.Manual);
    }

    // Remembered track changes are not capped, so any number of them leaves room for the 500 explicit overrides.
    [Fact]
    public void WithOverride_RememberedTrackChangesDoNotUseUpTheCap()
    {
        var stored = Ctx.Person();
        for (var i = 0; i < 600; i++)
        {
            stored.Overrides.Add(Ctx.Override(Guid.NewGuid(), OverrideScope.Title, new SettingsLayer(), Manual()));
        }

        var copy = PersonDocuments.WithOverride(stored, Gone, OverrideScope.Title, "Gone", Format(FormatPreference.TextOnly), Remembered, out var saved);

        Assert.NotNull(copy);
        Assert.Equal(601, copy!.Overrides.Count);
        Assert.NotNull(saved);
    }

    [Fact]
    public void WithOverride_AtTheCap_RefusesAnotherExplicitOverride_ButNotAChangeToOneThatExists()
    {
        var stored = Ctx.Person();
        for (var i = 0; i < SettingsValidator.MaxOverrides; i++)
        {
            stored.Overrides.Add(Ctx.Override(Guid.NewGuid(), OverrideScope.Title, Format(FormatPreference.TextOnly)));
        }

        stored.Overrides.Add(Ctx.Override(Movie, OverrideScope.Title, new SettingsLayer(), Manual()));
        var existing = Guid.Parse(stored.Overrides[0].ItemId);

        // A new entry and a remembered-only entry that would become explicit are refused, and nothing is returned or changed.
        Assert.Null(PersonDocuments.WithOverride(stored, Gone, OverrideScope.Title, "Gone", Format(FormatPreference.TextOnly), Remembered, out var refusedNew));
        Assert.Null(refusedNew);
        Assert.Null(PersonDocuments.WithOverride(stored, Movie, OverrideScope.Title, "Movie", Format(FormatPreference.TextOnly), Remembered, out var refusedRemembered));
        Assert.Null(refusedRemembered);
        Assert.Equal(SettingsValidator.MaxOverrides + 1, stored.Overrides.Count);

        // An explicit entry that already exists can still be changed.
        var changed = PersonDocuments.WithOverride(stored, existing, OverrideScope.Title, "Changed", Format(FormatPreference.PreferText), Remembered, out var saved);
        Assert.NotNull(changed);
        Assert.Equal(FormatPreference.PreferText, saved!.Settings.Format);
        Assert.Equal(SettingsValidator.MaxOverrides + 1, changed!.Overrides.Count);
    }

    // The cap check runs on the document the store holds when its lock is taken: the removal that landed after the read both frees room and stays removed.
    [Fact]
    public void WithOverride_InsideAnUpdate_SeesARemovalThatLandedAfterTheRead_AndKeepsIt()
    {
        var store = StoreHolding(StoredWithOverrides());
        var readEarlier = store.TryGet(Ids.Person)!.Clone();
        Assert.True(store.Update(Ids.Person, current => PersonDocuments.WithoutOverride(current, Ids.Series, out _)));
        ItemOverride? saved = null;

        var done = store.Update(Ids.Person, current => PersonDocuments.WithOverride(current ?? readEarlier, Gone, OverrideScope.Title, "Gone", Format(FormatPreference.TextOnly), Remembered, out saved));

        Assert.True(done);
        Assert.NotNull(saved);
        Assert.Equal(new[] { Movie, OtherSeries, Episode, Gone }.Select(Ids.N), IdsOf(store.TryGet(Ids.Person)!));
    }

    /// <summary>A store that notes what its update hands the change.</summary>
    private sealed class SeenStore : IPersonStore
    {
        private readonly FakePersonStore _inner;

        public SeenStore(FakePersonStore inner) => _inner = inner;

        public event EventHandler<PersonStoreChangedEventArgs>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public string DataFolder => _inner.DataFolder;

        /// <summary>Gets the document the last update handed to the change.</summary>
        public PersonSettings? Seen { get; private set; }

        /// <summary>Gets the document the store held when the last update started.</summary>
        public PersonSettings? Latest { get; private set; }

        /// <summary>Gets how many updates ran.</summary>
        public int Updates { get; private set; }

        public PersonSettings? TryGet(Guid userId) => _inner.TryGet(userId);

        public PersonSettings GetOrCreate(Guid userId, Func<PersonSettings> create) => _inner.GetOrCreate(userId, create);

        public void Save(PersonSettings settings) => _inner.Save(settings);

        public bool Update(Guid userId, Func<PersonSettings?, PersonSettings?> change)
        {
            Updates++;
            Latest = _inner.TryGet(userId);
            return _inner.Update(userId, current =>
            {
                Seen = current;
                return change(current);
            });
        }

        public bool Delete(Guid userId) => _inner.Delete(userId);

        public IReadOnlyList<Guid> UserIds() => _inner.UserIds();
    }

    // ----- The dialog's target -----

    [Theory]
    [InlineData(BaseItemKind.Movie, "item", OverrideScope.Title)]
    [InlineData(BaseItemKind.Video, "item", OverrideScope.Title)]
    [InlineData(BaseItemKind.Series, "item", OverrideScope.Series)]
    [InlineData(BaseItemKind.Season, "series", OverrideScope.Series)]
    [InlineData(BaseItemKind.Episode, "series", OverrideScope.Series)]
    public void For_MapsAnItemToItsTarget(BaseItemKind kind, string expected, OverrideScope scope)
    {
        var target = OverrideTargets.For(kind, Movie, Ids.Series);

        Assert.NotNull(target);
        Assert.Equal(expected == "item" ? Movie : Ids.Series, target!.Value.Id);
        Assert.Equal(scope, target.Value.Scope);
        Assert.True(OverrideTargets.IsSupported(kind));
    }

    [Theory]
    [InlineData(BaseItemKind.Audio)]
    [InlineData(BaseItemKind.MusicAlbum)]
    [InlineData(BaseItemKind.MusicVideo)]
    [InlineData(BaseItemKind.Trailer)]
    [InlineData(BaseItemKind.Book)]
    [InlineData(BaseItemKind.BoxSet)]
    [InlineData(BaseItemKind.Folder)]
    [InlineData(BaseItemKind.CollectionFolder)]
    [InlineData(BaseItemKind.Person)]
    [InlineData(BaseItemKind.Photo)]
    public void For_OtherTypesHaveNoTarget(BaseItemKind kind)
    {
        Assert.Null(OverrideTargets.For(kind, Movie, Ids.Series));
        Assert.False(OverrideTargets.IsSupported(kind));
    }

    [Fact]
    public void For_ASeasonOrEpisodeWithNoShowHasNoTarget()
    {
        Assert.Null(OverrideTargets.For(BaseItemKind.Season, Movie, Guid.Empty));
        Assert.Null(OverrideTargets.For(BaseItemKind.Episode, Movie, Guid.Empty));
        Assert.True(OverrideTargets.IsSupported(BaseItemKind.Episode));
    }

    [Fact]
    public void For_ASeasonOrEpisodeStandsForItsShowWhateverItsOwnIdIs()
    {
        Assert.Equal(Ids.Series, OverrideTargets.For(BaseItemKind.Episode, Episode, Ids.Series)!.Value.Id);
        Assert.Equal(Ids.Series, OverrideTargets.For(BaseItemKind.Season, Ids.Season, Ids.Series)!.Value.Id);
        Assert.Equal(Ids.Series, OverrideTargets.For(BaseItemKind.Series, Ids.Series, Guid.Empty)!.Value.Id);
    }

    [Fact]
    public void HasNarrower_AnEpisodeOrSeasonOverrideUnderTheShowCounts()
    {
        var shows = new Dictionary<Guid, Guid> { [Episode] = Ids.Series, [Ids.Season] = Ids.Series, [OtherEpisode] = OtherSeries };
        bool AnyInShow(IReadOnlyList<Guid> ids) => ids.Any(id => shows.TryGetValue(id, out var series) && series == Ids.Series);

        Assert.True(OverrideTargets.HasNarrower(new[] { Ctx.Override(Episode, OverrideScope.Title, Format(FormatPreference.TextOnly)) }, Ids.Series, AnyInShow));
        Assert.True(OverrideTargets.HasNarrower(new[] { Ctx.Override(Ids.Season, OverrideScope.Season, Format(FormatPreference.TextOnly)) }, Ids.Series, AnyInShow));
        Assert.True(OverrideTargets.HasNarrower(new[] { Ctx.Override(Episode, OverrideScope.Title, new SettingsLayer(), Manual()) }, Ids.Series, AnyInShow));
        Assert.False(OverrideTargets.HasNarrower(new[] { Ctx.Override(OtherEpisode, OverrideScope.Title, Format(FormatPreference.TextOnly)) }, Ids.Series, AnyInShow));
    }

    [Fact]
    public void HasNarrower_OnlySeasonAndTitleOverridesAreLookedAt()
    {
        var asked = new List<Guid>();
        bool AnyInShow(IReadOnlyList<Guid> ids)
        {
            asked.AddRange(ids);
            return true;
        }

        var overrides = new[]
        {
            Ctx.Override(Ids.Series, OverrideScope.Series, Format(FormatPreference.TextOnly)),
            Ctx.Override(OtherSeries, OverrideScope.Series, Format(FormatPreference.TextOnly)),
            Ctx.Override(Ids.Cloudvale, OverrideScope.Collection, Format(FormatPreference.TextOnly)),
            Ctx.Override(Ids.Anime, OverrideScope.Library, Format(FormatPreference.TextOnly)),
        };

        Assert.False(OverrideTargets.HasNarrower(overrides, Ids.Series, AnyInShow));
        Assert.Empty(asked);
    }

    [Fact]
    public void HasNarrower_IgnoresBadIdsTheShowItselfAndRepeats_AndAsksOneIdOnce()
    {
        var asked = new List<Guid>();
        bool AnyInShow(IReadOnlyList<Guid> ids)
        {
            asked.AddRange(ids);
            return false;
        }

        var overrides = new List<ItemOverride>
        {
            new() { ItemId = "not an id", Scope = OverrideScope.Title, Settings = Format(FormatPreference.TextOnly) },
            new() { ItemId = string.Empty, Scope = OverrideScope.Title, Settings = Format(FormatPreference.TextOnly) },
            new() { ItemId = Guid.Empty.ToString("N"), Scope = OverrideScope.Season, Settings = Format(FormatPreference.TextOnly) },
            Ctx.Override(Ids.Series, OverrideScope.Title, Format(FormatPreference.TextOnly)),
            Ctx.Override(Movie, OverrideScope.Title, Format(FormatPreference.TextOnly)),
            Ctx.Override(Episode, OverrideScope.Title, Format(FormatPreference.TextOnly)),
            Ctx.Override(Episode, OverrideScope.Title, Format(FormatPreference.PreferText)),
        };

        Assert.False(OverrideTargets.HasNarrower(overrides, Ids.Series, AnyInShow));
        Assert.Equal(new[] { Movie, Episode }, asked);
    }

    // Thousands of stored entries are asked about in a few batches, not one by one, and the asking stops at the first batch that holds a match.
    [Fact]
    public void HasNarrower_AsksInBatches_AndStopsAtTheFirstBatchWithAMatch()
    {
        var ids = Enumerable.Range(1, 1200).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();
        var overrides = ids.Select(id => Ctx.Override(id, OverrideScope.Title, new SettingsLayer(), Manual())).ToList();
        var batches = new List<int>();

        Assert.False(OverrideTargets.HasNarrower(overrides, Ids.Series, batch =>
        {
            batches.Add(batch.Count);
            return false;
        }));
        Assert.Equal(new[] { PersonLookups.BatchSize, PersonLookups.BatchSize, 1200 - (2 * PersonLookups.BatchSize) }, batches);

        batches.Clear();
        var inSecondBatch = ids[PersonLookups.BatchSize + 7];
        Assert.True(OverrideTargets.HasNarrower(overrides, Ids.Series, batch =>
        {
            batches.Add(batch.Count);
            return batch.Contains(inSecondBatch);
        }));
        Assert.Equal(new[] { PersonLookups.BatchSize, PersonLookups.BatchSize }, batches);
    }

    [Fact]
    public void HasNarrower_NothingStoredMeansNone()
        => Assert.False(OverrideTargets.HasNarrower(Array.Empty<ItemOverride>(), Ids.Series, _ => throw new InvalidOperationException("nothing to look up")));

    // ----- What "Default" means in the dialog -----

    // A show as the dialog builds its context: the show itself, in the Anime library (the content type of a show is "other").
    private static ItemContext ShowContext(params (Guid Id, string Name)[] collections)
        => Ctx.Episode("Anime", collections: collections) with { ItemId = Ids.Series, Name = "Skyward Lantern Voyage", Kind = ContentKind.Other, SeasonId = null };

    private static EffectiveProfileDto InheritedFor(PersonSettings person, ItemContext context, OverrideScope scope = OverrideScope.Series, PluginConfiguration? admin = null)
        => OverrideTargets.Inherited(person, admin ?? new PluginConfiguration(), context, (context.ItemId, scope), LanguageTable.Default);

    [Fact]
    public void Inherited_LeavesOutOnlyTheEntryUnderTheTargetsId_EveryOtherLayerStillCounts()
    {
        var person = Ctx.Person();
        person.Overrides.Add(Ctx.Override(Ids.Series, OverrideScope.Series, new SettingsLayer { Format = FormatPreference.TextOnly, AvoidCommentary = false }));
        person.Overrides.Add(Ctx.Override(Ids.Cloudvale, OverrideScope.Collection, new SettingsLayer { PrefersSdh = true }));
        person.Overrides.Add(Ctx.Override(OtherSeries, OverrideScope.Series, new SettingsLayer { Format = FormatPreference.TextOnly, UsesAudioDescription = true }));
        person.Overrides.Add(Ctx.Override(Episode, OverrideScope.Title, new SettingsLayer { AvoidCommentary = false }));
        person.Libraries.Add(Ctx.MyLibrary(Ids.Anime, new SettingsLayer { Sdh = EnginePreference.Avoid }));

        var inherited = InheritedFor(person, ShowContext((Ids.Cloudvale, "Cloudvale")));

        // The show's own override is not what "Default" means.
        Assert.NotEqual("your settings for this show", inherited.Sources[SettingsFields.Format]);
        Assert.NotEqual("your settings for this show", inherited.Sources[SettingsFields.AvoidCommentary]);
        Assert.True(inherited.Profile.AvoidCommentary);
        // The show's collection (another override of the person's) and library layer still count.
        Assert.Equal("your settings for the Cloudvale collection", inherited.Sources[SettingsFields.PrefersSdh]);
        Assert.True(inherited.Profile.PrefersSdh);
        Assert.Equal("your Anime library settings", inherited.Sources[SettingsFields.Sdh]);
        Assert.Equal(EnginePreference.Avoid, inherited.Profile.Sdh);
        // Another show's or another title's override is nobody's business here.
        Assert.False(inherited.Profile.UsesAudioDescription);
        Assert.Equal(FormatPreference.PreferText, inherited.Profile.Format);
        // The stored document is not changed.
        Assert.Equal(4, person.Overrides.Count);
    }

    [Fact]
    public void Inherited_TheAdminsTitleDefaultForTheTargetStillCounts()
    {
        var admin = new PluginConfiguration();
        admin.Titles.Add(Ctx.Title(Ids.Series, OverrideScope.Series, new SettingsLayer { Format = FormatPreference.TextOnly }));
        var person = Ctx.Person();
        person.Overrides.Add(Ctx.Override(Ids.Series, OverrideScope.Series, new SettingsLayer { Format = FormatPreference.PreferText }));

        var inherited = InheritedFor(person, ShowContext(), admin: admin);

        Assert.Equal("the admin's defaults for this show", inherited.Sources[SettingsFields.Format]);
        Assert.Equal(FormatPreference.TextOnly, inherited.Profile.Format);
    }

    // The dialog is opened from an episode, whose own override comes first for that episode alone: "Default" is the show's, so the context is the show's.
    [Fact]
    public void Inherited_AnEpisodesOwnOverrideIsNotPartOfTheShowsDefault()
    {
        var person = Ctx.Person();
        person.Overrides.Add(Ctx.Override(Episode, OverrideScope.Title, new SettingsLayer { Format = FormatPreference.TextOnly }));
        person.Overrides.Add(Ctx.Override(Ids.Season, OverrideScope.Season, new SettingsLayer { PrefersSdh = true }));

        var inherited = InheritedFor(person, ShowContext());

        Assert.NotEqual(FormatPreference.TextOnly, inherited.Profile.Format);
        Assert.False(inherited.Profile.PrefersSdh);
    }

    [Fact]
    public void Inherited_ShowAndEpisodeShareGenreTableChoices_AndGeneralTypeRulesStayInactive()
    {
        var person = Ctx.Person();
        person.SmartRules.Add(Ctx.Rule("shows", new() { Format = FormatPreference.TextOnly }, Ctx.When(ConditionKind.ContentType, "episode")));
        person.SmartRules.Add(Ctx.Rule("films", new() { PrefersSdh = true }, Ctx.When(ConditionKind.ContentType, "movie")));
        person.SmartRules.Add(Ctx.Rule("Anime", new() { WhenAudioNotUnderstood = SubtitleWant.Full }, Ctx.When(ConditionKind.Genre, "Anime")));
        var show = InheritedFor(person, ShowContext() with { Genres = new[] { "Anime" } });
        var episode = Ctx.Resolve(person, null, Ctx.Episode("Anime", genres: new[] { "Anime" }));
        Assert.Equal(episode.Profile.WhenAudioNotUnderstood, show.Profile.WhenAudioNotUnderstood);
        Assert.Equal(SubtitleWant.Full, show.Profile.WhenAudioNotUnderstood);
        Assert.Equal("your Anime genre settings", show.Sources[SettingsFields.WhenAudioNotUnderstood]);
        Assert.Equal(FormatPreference.PreferText, show.Profile.Format);
        Assert.Equal("Babelfin's defaults", show.Sources[SettingsFields.Format]);
        Assert.False(show.Profile.PrefersSdh);
    }

    [Fact]
    public void Inherited_MoviesAndOtherVideosUseGenreRows_WithoutRetiredGeneralTypeRules()
    {
        var person = Ctx.Person();
        person.SmartRules.Add(Ctx.Rule("shows", new() { Format = FormatPreference.TextOnly }, Ctx.When(ConditionKind.ContentType, "episode")));
        person.SmartRules.Add(Ctx.Rule("films", new() { PrefersSdh = true }, Ctx.When(ConditionKind.ContentType, "movie")));
        person.SmartRules.Add(Ctx.Rule("Animation", new() { WhenAudioNotUnderstood = SubtitleWant.Full }, Ctx.When(ConditionKind.Genre, "Animation")));
        var movieContext = Ctx.Movie(genres: new[] { "Animation" }) with { ItemId = Movie };
        var movie = InheritedFor(person, movieContext, OverrideScope.Title);
        var video = InheritedFor(person, movieContext with { Kind = ContentKind.Other }, OverrideScope.Title);
        foreach (var result in new[] { movie, video })
        {
            Assert.False(result.Profile.PrefersSdh);
            Assert.Equal("Babelfin's defaults", result.Sources[SettingsFields.PrefersSdh]);
            Assert.Equal(FormatPreference.PreferText, result.Profile.Format);
            Assert.Equal(SubtitleWant.Full, result.Profile.WhenAudioNotUnderstood);
            Assert.Equal("your Animation genre settings", result.Sources[SettingsFields.WhenAudioNotUnderstood]);
        }
    }

    [Fact]
    public void Inherited_ForAMovie_ItsOwnEntryIsLeftOut()
    {
        var person = Ctx.Person();
        person.Overrides.Add(Ctx.Override(Movie, OverrideScope.Title, new SettingsLayer { Format = FormatPreference.TextOnly }));

        var inherited = InheritedFor(person, Ctx.Movie() with { ItemId = Movie }, OverrideScope.Title);

        Assert.NotEqual(FormatPreference.TextOnly, inherited.Profile.Format);
        Assert.NotNull(person.FindOverride(Movie));
    }

    // ----- Activity text -----

    [Theory]
    [InlineData("Current", "Stored", "Current")]
    [InlineData(null, "Stored", "Stored")]
    [InlineData("", "Stored", "Stored")]
    [InlineData(null, "", "a title")]
    [InlineData("  ", null, "a title")]
    [InlineData(null, null, "a title")]
    public void ActivityNameOf_NeverLeavesTheNameEmpty(string? current, string? stored, string expected)
        => Assert.Equal(expected, OverrideSummaries.ActivityNameOf(current, stored));

    // ----- The list's rows -----

    [Fact]
    public void Compose_AnExistingItemShowsItsCurrentFacts()
    {
        var stored = Ctx.Override(Episode, OverrideScope.Title, Format(FormatPreference.TextOnly), Manual());
        stored.Name = "Old name";
        stored.UpdatedAt = Remembered;
        var facts = new OverrideItemFacts("Episode 1", "Episode", 2015, "Skyward Lantern Voyage", Ids.Anime, "Anime");

        var row = OverrideSummaries.Compose(stored, facts);

        Assert.Equal(Ids.N(Episode), row.ItemId);
        Assert.Equal(OverrideScope.Title, row.Scope);
        Assert.Equal("Episode 1", row.Name);
        Assert.Equal("Episode", row.Type);
        Assert.Equal(2015, row.ProductionYear);
        Assert.Equal("Skyward Lantern Voyage", row.SeriesName);
        Assert.Equal(Ids.N(Ids.Anime), row.LibraryId);
        Assert.Equal("Anime", row.LibraryName);
        Assert.Equal(Json(stored.Settings), Json(row.Settings));
        Assert.Equal(Json(stored.Manual!), Json(row.Manual!));
        Assert.Equal(Remembered, row.UpdatedAt);
    }

    // A row is a copy: nothing done to it reaches the document the store caches.
    [Fact]
    public void Compose_RowsDoNotShareTheStoredSettingsOrRememberedChange()
    {
        var stored = Ctx.Override(Episode, OverrideScope.Title, Format(FormatPreference.TextOnly), Manual());
        var before = Json(stored);

        var row = OverrideSummaries.Compose(stored, null);
        row.Settings.Format = FormatPreference.PreferText;
        row.Manual!.Audio!.Language = "ko";
        row.Manual.SubtitlesOff = false;

        Assert.NotSame(stored.Settings, row.Settings);
        Assert.NotSame(stored.Manual, row.Manual);
        Assert.Equal(before, Json(stored));
    }

    [Fact]
    public void Compose_AnEntryWithNoSettingsGetsEmptyOnes()
    {
        var stored = Ctx.Override(Episode, OverrideScope.Title, new SettingsLayer(), Manual());
        stored.Settings = null!;

        var row = OverrideSummaries.Compose(stored, null);

        Assert.True(row.Settings.IsEmpty);
        Assert.NotNull(row.Manual);
    }

    [Fact]
    public void Compose_AnItemThatIsGoneKeepsTheStoredNameAndHasNoTypeOrLibrary()
    {
        var stored = Ctx.Override(Gone, OverrideScope.Series, Format(FormatPreference.TextOnly));
        stored.Name = "Cancelled Show";

        var row = OverrideSummaries.Compose(stored, null);

        Assert.Equal("Cancelled Show", row.Name);
        Assert.Null(row.Type);
        Assert.Null(row.ProductionYear);
        Assert.Null(row.SeriesName);
        Assert.Null(row.LibraryId);
        Assert.Null(row.LibraryName);
        Assert.Equal(FormatPreference.TextOnly, row.Settings.Format);
    }

    [Fact]
    public void Compose_AnItemInNoLibraryHasNoLibrary()
    {
        var row = OverrideSummaries.Compose(Ctx.Override(Movie, OverrideScope.Title, new SettingsLayer(), Manual()), new OverrideItemFacts("Home Video", "Video", null, null, null, null));

        Assert.Equal("Home Video", row.Name);
        Assert.Equal("Video", row.Type);
        Assert.Null(row.LibraryId);
        Assert.Null(row.LibraryName);
        Assert.NotNull(row.Manual);
        Assert.True(row.Settings.IsEmpty);
    }

    [Fact]
    public void Compose_NormalisesTheIdToTheShortFormat()
    {
        var stored = Ctx.Override(Movie, OverrideScope.Title, Format(FormatPreference.TextOnly));
        stored.ItemId = Movie.ToString("D").ToUpperInvariant();

        Assert.Equal(Ids.N(Movie), OverrideSummaries.Compose(stored, null).ItemId);
    }

    [Theory]
    [InlineData("Current", "Stored", "Current")]
    [InlineData(null, "Stored", "Stored")]
    [InlineData("", "Stored", "Stored")]
    [InlineData("  ", "Stored", "Stored")]
    [InlineData(null, null, "")]
    [InlineData(null, " ", "")]
    public void NameOf_PrefersTheCurrentNameThenTheStoredOne(string? current, string? stored, string expected)
        => Assert.Equal(expected, OverrideSummaries.NameOf(current, stored));

    // What the pages read, with the names Jellyfin's API writes (PascalCase; the pages break on any other spelling).
    [Fact]
    public void Compose_SerializesWithJellyfinsApiJsonSettings()
    {
        var stored = Ctx.Override(Movie, OverrideScope.Title, Format(FormatPreference.TextOnly), Manual());
        var row = OverrideSummaries.Compose(stored, new OverrideItemFacts("Paper Moon Ferry", "Movie", 2001, null, Ids.Movies, "Movies"));

        using var doc = JsonDocument.Parse(ApiJson(row));
        var root = doc.RootElement;

        Assert.Equal(Ids.N(Movie), root.GetProperty("ItemId").GetString());
        Assert.Equal("Title", root.GetProperty("Scope").GetString());
        Assert.Equal("Paper Moon Ferry", root.GetProperty("Name").GetString());
        Assert.Equal("Movie", root.GetProperty("Type").GetString());
        Assert.Equal(2001, root.GetProperty("ProductionYear").GetInt32());
        Assert.Equal(Ids.N(Ids.Movies), root.GetProperty("LibraryId").GetString());
        Assert.Equal("Movies", root.GetProperty("LibraryName").GetString());
        Assert.False(root.TryGetProperty("SeriesName", out _));
        Assert.Equal("TextOnly", root.GetProperty("Settings").GetProperty("Format").GetString());
        Assert.Equal("ja", root.GetProperty("Manual").GetProperty("Audio").GetProperty("Language").GetString());
        Assert.True(root.GetProperty("Manual").GetProperty("SubtitlesOff").GetBoolean());
        Assert.True(root.TryGetProperty("UpdatedAt", out _));
        Assert.False(root.TryGetProperty("itemId", out _));
        Assert.False(root.TryGetProperty("libraryName", out _));
    }

    [Fact]
    public void Target_SerializesWithJellyfinsApiJsonSettings()
    {
        var person = Ctx.Person();
        var stored = Ctx.Override(Ids.Series, OverrideScope.Series, Format(FormatPreference.TextOnly));
        person.Overrides.Add(stored);
        var inherited = InheritedFor(person, ShowContext());
        var target = new OverrideTarget(Ids.N(Ids.Series), OverrideScope.Series, "Skyward Lantern Voyage", "Series", Ids.N(Ids.Anime), "Anime", stored, inherited, HasNarrower: true);

        using var doc = JsonDocument.Parse(ApiJson(target));
        var root = doc.RootElement;

        Assert.Equal(Ids.N(Ids.Series), root.GetProperty("TargetId").GetString());
        Assert.Equal("Series", root.GetProperty("Scope").GetString());
        Assert.Equal("Skyward Lantern Voyage", root.GetProperty("Name").GetString());
        Assert.Equal("Series", root.GetProperty("Type").GetString());
        Assert.Equal(Ids.N(Ids.Anime), root.GetProperty("LibraryId").GetString());
        Assert.Equal("Anime", root.GetProperty("LibraryName").GetString());
        Assert.True(root.GetProperty("HasNarrower").GetBoolean());
        Assert.Equal(Ids.N(Ids.Series), root.GetProperty("Override").GetProperty("ItemId").GetString());
        Assert.Equal("Series", root.GetProperty("Override").GetProperty("Scope").GetString());
        Assert.Equal("TextOnly", root.GetProperty("Override").GetProperty("Settings").GetProperty("Format").GetString());
        var profile = root.GetProperty("Inherited").GetProperty("Profile");
        Assert.Equal(JsonValueKind.Array, profile.GetProperty("AudioChain").ValueKind);
        Assert.Equal(JsonValueKind.Array, profile.GetProperty("SubtitleLanguages").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("Inherited").GetProperty("BurnedInLanguages").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("Inherited").GetProperty("Sources").ValueKind);
        Assert.False(root.TryGetProperty("targetId", out _));
        Assert.False(root.TryGetProperty("inherited", out _));
    }

    [Fact]
    public void Target_WithNoOverrideLeavesOverrideOut()
    {
        var inherited = InheritedFor(Ctx.Person(), ShowContext());
        var target = new OverrideTarget(Ids.N(Movie), OverrideScope.Title, "Paper Moon Ferry", "Movie", null, null, null, inherited, HasNarrower: false);

        using var doc = JsonDocument.Parse(ApiJson(target));

        Assert.False(doc.RootElement.TryGetProperty("Override", out _));
        Assert.False(doc.RootElement.TryGetProperty("LibraryId", out _));
        Assert.False(doc.RootElement.GetProperty("HasNarrower").GetBoolean());
    }

    // ----- The rows of a list -----

    [Fact]
    public void Build_AnEntryWithNoUsableIdGetsNoRow_TheOthersKeepTheirOrder()
    {
        var library = EmptyLibrary.Create();
        var bad = new List<ItemOverride>
        {
            new() { ItemId = string.Empty, Scope = OverrideScope.Title, Name = "Nothing", Manual = Manual() },
            new() { ItemId = "not an id", Scope = OverrideScope.Series, Name = "Junk", Settings = Format(FormatPreference.TextOnly) },
            Ctx.Override(Guid.Empty, OverrideScope.Title, Format(FormatPreference.TextOnly)),
        };
        var doc = Ctx.Person();
        doc.Overrides = bad;

        Assert.Empty(OverrideSummaries.Build(library, Person, doc));
        Assert.Empty(OverrideSummaries.Build(library, Person, null));

        doc.Overrides.Insert(1, Ctx.Override(Movie, OverrideScope.Title, Format(FormatPreference.TextOnly)));
        doc.Overrides.Add(Ctx.Override(OtherSeries, OverrideScope.Series, new SettingsLayer(), Manual()));
        var rows = OverrideSummaries.Build(library, Person, doc);

        Assert.Equal(new[] { Ids.N(Movie), Ids.N(OtherSeries) }, rows.Select(r => r.ItemId));
        Assert.All(rows, r => Assert.NotEmpty(r.ItemId));
    }

    /// <summary>A library manager that holds no item and no library: every query comes back empty.</summary>
    private class EmptyLibrary : DispatchProxy
    {
        public static ILibraryManager Create() => DispatchProxy.Create<ILibraryManager, EmptyLibrary>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod!.ReturnType;
            if (type == typeof(IReadOnlyList<BaseItem>))
            {
                return Array.Empty<BaseItem>();
            }

            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
