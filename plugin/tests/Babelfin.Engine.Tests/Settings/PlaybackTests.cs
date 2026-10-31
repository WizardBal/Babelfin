using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>Playback behavior: the play registry, the change recorder, the PlaybackInfo table and the monitor's report logic.</summary>
public class PlaybackTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);

    // ── Play registry ──

    [Fact]
    public void Registry_SessionAdoptsThePendingEntryOfItsDeviceClientAndItem()
    {
        var registry = new PlayRegistry(() => T0);
        registry.Update("s1", r => { r.LastStoppedItemId = Ids.Season; r.LastStoppedAt = T0; });
        registry.UpdatePending("dev", "Jellyfin Web", Ids.Item, r => { r.HasExpected = true; r.ExpectedAudio = 2; r.ExpectedSubtitle = 4; r.Playing = true; });

        Assert.Null(registry.Adopt("s1", "dev", "Jellyfin Web", Ids.Series));
        var adopted = registry.Adopt("s1", "dev", "jellyfin web", Ids.Item);

        Assert.NotNull(adopted);
        Assert.Equal("s1", adopted!.SessionId);
        Assert.Equal(Ids.Item, adopted.ItemId);
        Assert.Equal(2, adopted.ExpectedAudio);
        Assert.Equal(4, adopted.ExpectedSubtitle);
        Assert.Equal(Ids.Season, adopted.LastStoppedItemId);
        Assert.Null(registry.GetPending("dev", "Jellyfin Web", Ids.Item));
        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal("s1", entry.SessionId);
        Assert.Equal(2, entry.ExpectedAudio);
    }

    [Fact]
    public void Registry_SweepDropsEntriesUntouchedForSixHours()
    {
        var now = T0;
        var registry = new PlayRegistry(() => now);
        registry.Update("old", r => r.ItemId = Ids.Item);
        registry.UpdatePending("dev", "app", Ids.Item, r => r.Playing = true);
        now = T0.AddHours(5);
        registry.Update("fresh", r => r.ItemId = Ids.Item);
        now = T0.AddHours(6).AddMinutes(1);

        Assert.Equal(2, registry.Sweep());
        Assert.Null(registry.Get("old"));
        Assert.NotNull(registry.Get("fresh"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Registry_IsBoundedAndDropsTheOldest()
    {
        var now = T0;
        var registry = new PlayRegistry(() => now);
        for (var i = 0; i < PlayRegistry.MaxEntries + 100; i++)
        {
            now = T0.AddSeconds(i);
            registry.Update("s" + i, r => r.ItemId = Ids.Item);
        }

        Assert.Equal(PlayRegistry.MaxEntries, registry.Count);
        Assert.Null(registry.Get("s0"));
        Assert.Null(registry.Get("s99"));
        Assert.NotNull(registry.Get("s100"));
        Assert.NotNull(registry.Get("s" + (PlayRegistry.MaxEntries + 99)));
    }

    [Fact]
    public void Registry_HandsOutCopies_AndUpdatesOnlyExistingEntries()
    {
        var registry = new PlayRegistry(() => T0);
        var copy = registry.Update("s1", r => r.ExpectedAudio = 2);
        copy.ExpectedAudio = 9;
        Assert.Equal(2, registry.Get("s1")!.ExpectedAudio);
        Assert.True(registry.Remove("s1"));
        Assert.Null(registry.UpdateExisting("s1", r => r.ExpectedAudio = 3));

    }

    // ── Track change recorder ──

    [Fact]
    public void Recorder_RestOfShow_StoresUnderTheSeries_WithDescriptors()
    {
        var (recorder, store, activity) = Recorder(Person());
        var manual = recorder.Record(TestUser(), Episode(), Source(), 2, 5, 1, 4, "Jellyfin Web");

        Assert.NotNull(manual);
        var o = Assert.Single(store.TryGet(Ids.Person)!.Overrides);
        Assert.Equal(Ids.N(Ids.Series), o.ItemId);
        Assert.Equal(OverrideScope.Series, o.Scope);
        Assert.Equal("Skyward Lantern Voyage", o.Name);
        Assert.Equal("ja", o.Manual!.Audio!.Language);
        Assert.Equal("normal", o.Manual.Audio.Kind);
        Assert.Equal("Surround", o.Manual.Audio.Title);
        Assert.Equal("en", o.Manual.Subtitle!.Language);
        Assert.Equal("partial", o.Manual.Subtitle.Kind);
        Assert.Equal("Signs & Songs", o.Manual.Subtitle.Title);
        Assert.False(o.Manual.SubtitlesOff);
        Assert.Equal("Jellyfin Web", o.Manual.Client);
        var entry = Assert.Single(activity.Entries);
        Assert.Equal(ActivityKinds.OverrideCreated, entry.Kind);
        Assert.Equal(Ids.Person, entry.UserId);
        Assert.Contains("the show Skyward Lantern Voyage", entry.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MemoryScope.Season, "22222222222222222222222222222222", OverrideScope.Season)]
    [InlineData(MemoryScope.TitleOnly, "11111111111111111111111111111111", OverrideScope.Title)]
    [InlineData(MemoryScope.RestOfShow, "33333333333333333333333333333333", OverrideScope.Series)]
    public void Recorder_EachMemoryScope_StoresUnderTheRightId(MemoryScope memory, string id, OverrideScope scope)
    {
        var person = Person();
        person.Everyday.Memory = memory;
        var (recorder, store, _) = Recorder(person);
        recorder.Record(TestUser(), Episode(), Source(), 2, 4, 1, 4, null);

        var o = Assert.Single(store.TryGet(Ids.Person)!.Overrides);
        Assert.Equal(id, o.ItemId);
        Assert.Equal(scope, o.Scope);
        Assert.Equal("ja", o.Manual!.Audio!.Language);
        Assert.Null(o.Manual.Subtitle);
    }

    [Fact]
    public void Recorder_MovieRestOfShow_StoresUnderTheMovie()
    {
        var (recorder, store, _) = Recorder(Person(), Ctx.Movie());
        var movie = new Movie { Id = Ids.Item, Name = "Paper Moon Ferry" };
        recorder.Record(TestUser(), movie, Source(), 2, 4, 1, 4, null);

        var o = Assert.Single(store.TryGet(Ids.Person)!.Overrides);
        Assert.Equal(Ids.N(Ids.Item), o.ItemId);
        Assert.Equal(OverrideScope.Title, o.Scope);
        Assert.Equal("Paper Moon Ferry", o.Name);
    }

    [Fact]
    public void Recorder_SubtitlesTurnedOff_StoresSubtitlesOff_AndLeavesAudioAsStored()
    {
        var person = Person();
        var keep = new SettingsLayer { Format = FormatPreference.TextOnly };
        person.Overrides.Add(Ctx.Override(Ids.Series, OverrideScope.Series, keep, new ManualMemory
        {
            Audio = new RememberedTrack { Language = "ja", Kind = "normal", Title = "Surround" },
            Subtitle = new RememberedTrack { Language = "en", Kind = "full" },
        }));
        var (recorder, store, _) = Recorder(person);
        var manual = recorder.Record(TestUser(), Episode(), Source(), 2, -1, 2, 4, "Jellyfin Web");

        Assert.NotNull(manual);
        var o = Assert.Single(store.TryGet(Ids.Person)!.Overrides);
        Assert.True(o.Manual!.SubtitlesOff);
        Assert.Null(o.Manual.Subtitle);
        Assert.Equal("ja", o.Manual.Audio!.Language);
        Assert.Equal(FormatPreference.TextOnly, o.Settings.Format);
    }

    [Fact]
    public void Recorder_DontRemember_RecordsNothing()
    {
        var person = Person();
        person.Everyday.Memory = MemoryScope.DontRemember;
        var (recorder, store, activity) = Recorder(person);

        Assert.Null(recorder.Record(TestUser(), Episode(), Source(), 2, -1, 1, 4, null));
        Assert.Empty(store.TryGet(Ids.Person)!.Overrides);
        Assert.Empty(activity.Entries);
    }

    [Fact]
    public void Recorder_NoChange_OrUnknownStream_RecordsNothing()
    {
        var (recorder, store, activity) = Recorder(Person());
        Assert.Null(recorder.Record(TestUser(), Episode(), Source(), 2, 4, 2, 4, null));
        Assert.Null(recorder.Record(TestUser(), Episode(), Source(), 42, 4, 2, 4, null));
        Assert.Empty(store.TryGet(Ids.Person)!.Overrides);
        Assert.Empty(activity.Entries);
    }

    [Fact]
    public void Recorder_PersonSwitchedOff_RecordsNothing()
    {
        var person = Person();
        person.Enabled = false;
        var (recorder, store, _) = Recorder(person);
        Assert.Null(recorder.Record(TestUser(), Episode(), Source(), 2, 4, 1, 4, null));
        Assert.Empty(store.TryGet(Ids.Person)!.Overrides);
    }

    // A "remove all" that lands between the recorder reading the document and storing its change must not be undone by it.
    [Fact]
    public void Recorder_AfterARemoveAllMeanwhile_DoesNotBringBackWhatWasRemoved()
    {
        var person = Person();
        var removedLater = Guid.NewGuid();
        person.Overrides.Add(Ctx.Override(removedLater, OverrideScope.Title, new SettingsLayer { Format = FormatPreference.TextOnly }, new ManualMemory { SubtitlesOff = true }));
        var (recorder, store, racing) = RacingRecorder(person);
        racing.AfterRead = () => Assert.True(store.Update(Ids.Person, current => PersonDocuments.WithoutOverrides(current)));

        var manual = recorder.Record(TestUser(), Episode(), Source(), 2, 4, 1, 4, "Jellyfin Web");

        Assert.NotNull(manual);
        var o = Assert.Single(store.TryGet(Ids.Person)!.Overrides);
        Assert.Equal(Ids.N(Ids.Series), o.ItemId);
        Assert.Equal("ja", o.Manual!.Audio!.Language);
    }

    // A save that lands in that window keeps what it stored, and the change is added to it.
    [Fact]
    public void Recorder_AfterASaveMeanwhile_KeepsWhatTheSaveStored()
    {
        var elsewhere = Guid.NewGuid();
        var (recorder, store, racing) = RacingRecorder(Person());
        racing.AfterRead = () =>
        {
            var changed = store.TryGet(Ids.Person)!.Clone();
            changed.Overrides.Add(Ctx.Override(elsewhere, OverrideScope.Title, new SettingsLayer { Format = FormatPreference.TextOnly }));
            changed.Everyday.Understood = new[] { "fr" };
            store.Save(changed);
        };

        var manual = recorder.Record(TestUser(), Episode(), Source(), 2, 4, 1, 4, null);

        Assert.NotNull(manual);
        var stored = store.TryGet(Ids.Person)!;
        Assert.Equal(new[] { Ids.N(elsewhere), Ids.N(Ids.Series) }, stored.Overrides.Select(o => o.ItemId));
        Assert.Equal(new[] { "fr" }, stored.Everyday.Understood);
    }

    // The same remembered change is merged into the entry as it is when the store is updated: a second track change keeps the first one's other half.
    [Fact]
    public void Recorder_MergesWithTheEntryStoredMeanwhile()
    {
        var (recorder, store, racing) = RacingRecorder(Person());
        racing.AfterRead = () =>
        {
            var changed = store.TryGet(Ids.Person)!.Clone();
            changed.Overrides.Add(Ctx.Override(Ids.Series, OverrideScope.Series, new SettingsLayer(), new ManualMemory { Audio = new RememberedTrack { Language = "ko", Kind = "normal" }, At = T0 }));
            store.Save(changed);
        };

        recorder.Record(TestUser(), Episode(), Source(), 1, -1, 1, 4, null);

        var o = Assert.Single(store.TryGet(Ids.Person)!.Overrides);
        Assert.True(o.Manual!.SubtitlesOff);
        Assert.Equal("ko", o.Manual.Audio!.Language);
    }

    // A person whose file is gone by the time the change is stored gets nothing recorded (and no activity entry).
    [Fact]
    public void Recorder_PersonDeletedMeanwhile_RecordsNothing()
    {
        var (recorder, store, racing) = RacingRecorder(Person());
        racing.AfterRead = () => store.Delete(Ids.Person);

        Assert.Null(recorder.Record(TestUser(), Episode(), Source(), 2, 4, 1, 4, null));
        Assert.Null(store.TryGet(Ids.Person));
        Assert.Empty(racing.Activity.Entries);
    }

    [Fact]
    public void MemoryFor_ReadsResolvedAdvancedProvenance_AndIgnoresGeneralAndGenreMemory()
    {
        var context = Ctx.Episode();
        var admin = new PluginConfiguration();
        admin.Everyday.Memory = MemoryScope.TitleOnly;
        var person = Person();
        Assert.Equal(MemoryScope.TitleOnly, TrackChangeRecorder.MemoryFor(person, admin, Ctx.Resolve(person, admin, context)));

        person.Everyday.Memory = MemoryScope.Season;
        Assert.Equal(MemoryScope.Season, TrackChangeRecorder.MemoryFor(person, admin, Ctx.Resolve(person, admin, context)));

        person.Overrides.Add(Ctx.Override(Ids.Series, OverrideScope.Series, new SettingsLayer { Memory = MemoryScope.DontRemember }));
        Assert.Equal(MemoryScope.DontRemember, TrackChangeRecorder.MemoryFor(person, admin, Ctx.Resolve(person, admin, context)));

        person.Locks.Memory = MemoryScope.RestOfShow;
        Assert.Equal(MemoryScope.RestOfShow, TrackChangeRecorder.MemoryFor(person, admin, Ctx.Resolve(person, admin, context)));

        var plain = Person();
        Assert.Equal(MemoryScope.RestOfShow, TrackChangeRecorder.MemoryFor(plain, new PluginConfiguration(), Ctx.Resolve(plain, null, context)));

        var ruled = Person();
        ruled.SmartRules.Add(Ctx.Rule("shows", new SettingsLayer { Memory = MemoryScope.TitleOnly }, Ctx.When(ConditionKind.ContentType, "episode")));
        ruled.SmartRules.Add(Ctx.Rule("Drama", new SettingsLayer { Memory = MemoryScope.DontRemember,
            AudioChain = Ctx.Chain(Ctx.Lang("ja")) }, Ctx.When(ConditionKind.Genre, "Drama")));
        var genreContext = context with { Genres = new[] { "Drama" } };
        var genre = Ctx.Resolve(ruled, null, genreContext);
        Assert.Equal("ja", genre.Profile.AudioChain[0].Language);
        Assert.Equal("your Drama genre settings", genre.Sources[SettingsFields.AudioChain]);
        Assert.Equal("Babelfin's defaults", genre.Sources[SettingsFields.Memory]);
        Assert.Equal(MemoryScope.RestOfShow, TrackChangeRecorder.MemoryFor(ruled, new PluginConfiguration(), genre));
        ruled.Everyday.Memory = MemoryScope.Season;
        var visible = Ctx.Resolve(ruled, null, genreContext);
        Assert.Equal("your everyday settings", visible.Sources[SettingsFields.Memory]);
        Assert.Equal(MemoryScope.Season, TrackChangeRecorder.MemoryFor(ruled, new PluginConfiguration(), visible));
    }

    // ── The PlaybackInfo decision table ──

    [Theory]
    [InlineData(false, false, false, false, false, AppModeKind.ReplaceUnlessManual, false, PlaybackInfoOutcome.Trust)]
    [InlineData(true, true, true, false, true, AppModeKind.ReplaceUnlessManual, false, PlaybackInfoOutcome.Manual)]
    [InlineData(true, true, false, true, true, AppModeKind.ReplaceUnlessManual, true, PlaybackInfoOutcome.Trust)]
    [InlineData(true, false, false, false, false, AppModeKind.ReplaceUnlessManual, false, PlaybackInfoOutcome.Trust)]
    [InlineData(true, false, false, true, false, AppModeKind.Trust, false, PlaybackInfoOutcome.Trust)]
    [InlineData(true, false, false, true, true, AppModeKind.Trust, false, PlaybackInfoOutcome.Manual)]
    [InlineData(true, false, false, true, true, AppModeKind.ReplaceUnlessManual, false, PlaybackInfoOutcome.Manual)]
    [InlineData(true, false, false, false, true, AppModeKind.ReplaceUnlessManual, false, PlaybackInfoOutcome.Replace)]
    [InlineData(true, false, false, false, true, AppModeKind.Trust, true, PlaybackInfoOutcome.Trust)]
    [InlineData(true, false, false, false, true, AppModeKind.ReplaceOnAutoplay, false, PlaybackInfoOutcome.Trust)]
    [InlineData(true, false, false, false, true, AppModeKind.ReplaceOnAutoplay, true, PlaybackInfoOutcome.Replace)]
    [InlineData(true, false, false, false, false, AppModeKind.ReplaceOnAutoplay, true, PlaybackInfoOutcome.Trust)]
    [InlineData(true, false, false, true, true, AppModeKind.ReplaceOnAutoplay, true, PlaybackInfoOutcome.Replace)]
    [InlineData(true, false, false, true, true, AppModeKind.ReplaceOnAutoplay, false, PlaybackInfoOutcome.Manual)]
    [InlineData(true, false, false, false, true, AppModeKind.ReplaceUnlessManual, true, PlaybackInfoOutcome.Replace)]
    public void Filter_DecisionTable(bool explicitIndices, bool repeat, bool differsExpected, bool differsServed, bool differsChoice, AppModeKind mode, bool autoplay, PlaybackInfoOutcome expected)
    {
        var facts = new PlaybackInfoFacts(explicitIndices, repeat, differsExpected, differsServed, differsChoice, mode, autoplay);
        Assert.Equal(expected, PlaybackInfoFilter.Decide(facts));
    }

    [Fact]
    public void Filter_Differs_ComparesOnlyTheIndicesTheRequestNames()
    {
        Assert.False(PlaybackInfoFilter.Differs(2, null, 2, 4));
        Assert.True(PlaybackInfoFilter.Differs(1, null, 2, 4));
        Assert.False(PlaybackInfoFilter.Differs(null, -1, 2, null));
        Assert.True(PlaybackInfoFilter.Differs(null, 3, 2, 4));
        Assert.False(PlaybackInfoFilter.Differs(null, null, 2, 4));
    }

    // ── The monitor's report logic ──

    [Fact]
    public void Monitor_FirstReportOffExpected_CorrectsOnce_WithAdvertisedCommandsOnly()
    {
        var r = Expected(2, 4);
        var plan = PlaybackMonitor.Evaluate(r, 1, 3, T0, CorrectAfterStartMode.Auto, 5, false, new[] { GeneralCommandType.SetAudioStreamIndex });
        Assert.Equal(2, plan.SendAudio);
        Assert.Null(plan.SendSubtitle);
        Assert.False(plan.Manual);
        Assert.Equal(T0, r.CorrectionSentAt);

        var again = PlaybackMonitor.Evaluate(r, 1, 3, T0.AddSeconds(10), CorrectAfterStartMode.Auto, 5, false, new[] { GeneralCommandType.SetAudioStreamIndex });
        Assert.False(again.HasCommands);
        Assert.False(again.Manual);

        // The commanded track arriving later is not a manual change.
        var switched = PlaybackMonitor.Evaluate(r, 2, 3, T0.AddSeconds(20), CorrectAfterStartMode.Auto, 5, false, null);
        Assert.False(switched.Manual);
    }

    [Fact]
    public void Monitor_Correction_RespectsTrustOffAndUnknownCapabilities()
    {
        var trusted = Expected(2, 4);
        trusted.Trusted = true;
        Assert.False(PlaybackMonitor.Evaluate(trusted, 1, 3, T0, CorrectAfterStartMode.On, 5, false, null).HasCommands);

        Assert.False(PlaybackMonitor.Evaluate(Expected(2, 4), 1, 3, T0, CorrectAfterStartMode.Off, 5, false, new[] { GeneralCommandType.SetAudioStreamIndex }).HasCommands);
        Assert.False(PlaybackMonitor.Evaluate(Expected(2, 4), 1, 3, T0, CorrectAfterStartMode.Auto, 5, false, Array.Empty<GeneralCommandType>()).HasCommands);
        Assert.False(PlaybackMonitor.Evaluate(Expected(2, 4), 1, 3, T0, CorrectAfterStartMode.Auto, 5, true, new[] { GeneralCommandType.SetAudioStreamIndex }).HasCommands);

        var on = PlaybackMonitor.Evaluate(Expected(2, 4), 1, -1, T0, CorrectAfterStartMode.On, 5, false, null);
        Assert.Equal(2, on.SendAudio);
        Assert.Equal(4, on.SendSubtitle); // The requested subtitle is corrected even if the app initially reports Off.

        var off = PlaybackMonitor.Evaluate(Expected(2, -1), 2, 4, T0, CorrectAfterStartMode.Auto, 5, false, new[] { GeneralCommandType.SetSubtitleStreamIndex });
        Assert.Equal(-1, off.SendSubtitle);
    }

    [Fact]
    public void Monitor_ChangeAfterTheWindow_IsManual_WithinTheWindow_IsNot()
    {
        var late = Expected(2, 4);
        late.StartedAt = T0;
        Assert.False(PlaybackMonitor.Evaluate(late, 2, 4, T0, CorrectAfterStartMode.Auto, 5, false, null).Manual);
        var plan = PlaybackMonitor.Evaluate(late, 2, -1, T0.AddSeconds(6), CorrectAfterStartMode.Auto, 5, false, null);
        Assert.True(plan.Manual);
        Assert.Equal(2, plan.ManualAudio);
        Assert.Equal(2, plan.PreviousAudio);
        Assert.Equal(-1, plan.ManualSubtitle);
        Assert.Equal(4, plan.PreviousSubtitle);
        Assert.True(late.ManualChanged);
        Assert.Equal(-1, late.ExpectedSubtitle);

        var early = Expected(2, 4);
        early.StartedAt = T0;
        Assert.False(PlaybackMonitor.Evaluate(early, 2, 4, T0, CorrectAfterStartMode.Auto, 5, false, null).Manual);
        Assert.False(PlaybackMonitor.Evaluate(early, 2, -1, T0.AddSeconds(2), CorrectAfterStartMode.Auto, 5, false, null).Manual);

        // An unreported index is not "off".
        var silent = Expected(2, 4);
        silent.StartedAt = T0;
        PlaybackMonitor.Evaluate(silent, 2, 4, T0, CorrectAfterStartMode.Auto, 5, false, null);
        Assert.False(PlaybackMonitor.Evaluate(silent, 2, null, T0.AddSeconds(30), CorrectAfterStartMode.Auto, 5, false, null).Manual);

        // A dry run never records.
        var dry = Expected(2, 4);
        dry.StartedAt = T0;
        PlaybackMonitor.Evaluate(dry, 2, 4, T0, CorrectAfterStartMode.Auto, 5, true, null);
        Assert.False(PlaybackMonitor.Evaluate(dry, 1, 4, T0.AddSeconds(30), CorrectAfterStartMode.Auto, 5, true, null).Manual);
    }

    [Fact]
    public void Monitor_AChangeSettlesOnceOnAReportOneSettlePeriodLater()
    {
        var r = Expected(2, 4);
        r.StartedAt = T0;
        PlaybackMonitor.Evaluate(r, 2, 4, T0, CorrectAfterStartMode.Auto, 5, false, null);
        var changedAt = T0.AddSeconds(30);
        var change = PlaybackMonitor.Evaluate(r, 2, -1, changedAt, CorrectAfterStartMode.Auto, 5, false, null);
        Assert.True(change.Manual);
        Assert.True(change.Held);
        Assert.Null(change.Settled);
        Assert.Equal(-1, r.ExpectedSubtitle);

        Assert.Null(PlaybackMonitor.Evaluate(r, 2, -1, changedAt + PlaybackMonitor.SettlePeriod - TimeSpan.FromTicks(1), CorrectAfterStartMode.Auto, 5, false, null).Settled);
        Assert.Null(PlaybackMonitor.Evaluate(r, 2, null, changedAt + PlaybackMonitor.SettlePeriod, CorrectAfterStartMode.Auto, 5, false, null).Settled);
        var settled = PlaybackMonitor.Evaluate(r, 2, -1, changedAt + PlaybackMonitor.SettlePeriod, CorrectAfterStartMode.Auto, 5, false, null).Settled;
        Assert.NotNull(settled);
        Assert.Null(settled!.Audio);
        Assert.Equal(-1, settled.Subtitle!.Index);
        Assert.Equal(4, settled.Subtitle.Previous);
        Assert.Equal(changedAt, settled.Subtitle.Since);
        Assert.Null(PlaybackMonitor.Evaluate(r, 2, -1, changedAt.AddMinutes(5), CorrectAfterStartMode.Auto, 5, false, null).Settled);
    }

    [Fact]
    public void Monitor_AChangeBackWithinTheSettlePeriodDropsItAndAnotherTrackKeepsTheFirstStart()
    {
        var r = Expected(2, 4);
        r.StartedAt = T0;
        PlaybackMonitor.Evaluate(r, 2, 4, T0, CorrectAfterStartMode.Auto, 5, false, null);
        PlaybackMonitor.Evaluate(r, 1, 4, T0.AddSeconds(30), CorrectAfterStartMode.Auto, 5, false, null);
        var back = PlaybackMonitor.Evaluate(r, 2, 4, T0.AddSeconds(35), CorrectAfterStartMode.Auto, 5, false, null);
        Assert.True(back.Manual);
        Assert.False(back.Held);
        Assert.True(back.Dropped);
        Assert.Null(PlaybackMonitor.Evaluate(r, 2, 4, T0.AddMinutes(5), CorrectAfterStartMode.Auto, 5, false, null).Settled);

        PlaybackMonitor.Evaluate(r, 1, 4, T0.AddMinutes(6), CorrectAfterStartMode.Auto, 5, false, null);
        PlaybackMonitor.Evaluate(r, 3, 4, T0.AddMinutes(6).AddSeconds(5), CorrectAfterStartMode.Auto, 5, false, null);
        var settled = PlaybackMonitor.Evaluate(r, 3, 4, T0.AddMinutes(6).AddSeconds(15), CorrectAfterStartMode.Auto, 5, false, null).Settled;
        Assert.Equal(3, settled!.Audio!.Index);
        Assert.Equal(2, settled.Audio.Previous);
        Assert.Null(settled.Subtitle);
    }

    // ── Helpers ──

    // Babelfin chose these tracks (choice = expected), as the PlaybackInfo filter and the monitor record them.
    private static PlayRecord Expected(int audio, int subtitle) => new() { HasExpected = true, ExpectedAudio = audio, ExpectedSubtitle = subtitle, ChoiceAudio = audio, ChoiceSubtitle = subtitle, Playing = true };

    // A track left to the video has no choice and no expected index: changing it by hand is never remembered, the chosen one still is.
    [Fact]
    public void Monitor_ChangeToATrackLeftToTheVideo_IsNotManual()
    {
        var r = new PlayRecord { HasExpected = true, ExpectedAudio = 2, ChoiceAudio = 2, ExpectedSubtitle = null, ChoiceSubtitle = null, Playing = true, StartedAt = T0 };
        PlaybackMonitor.Evaluate(r, 2, 4, T0, CorrectAfterStartMode.Auto, 5, false, null);

        var subtitleOnly = PlaybackMonitor.Evaluate(r, 2, 6, T0.AddSeconds(10), CorrectAfterStartMode.Auto, 5, false, null);
        Assert.False(subtitleOnly.Manual);
        Assert.False(r.ManualChanged);

        var audio = PlaybackMonitor.Evaluate(r, 1, 6, T0.AddSeconds(20), CorrectAfterStartMode.Auto, 5, false, null);
        Assert.True(audio.Manual);
        Assert.Equal(1, audio.ManualAudio);
    }

    // Nothing chosen at all: no correction and nothing remembered, whatever the app plays.
    [Fact]
    public void Monitor_NothingChosen_NeverActs()
    {
        var r = new PlayRecord { Playing = true, StartedAt = T0 };

        var first = PlaybackMonitor.Evaluate(r, 1, 3, T0, CorrectAfterStartMode.On, 5, false, null);
        var later = PlaybackMonitor.Evaluate(r, 2, 5, T0.AddSeconds(30), CorrectAfterStartMode.On, 5, false, null);

        Assert.False(first.HasCommands);
        Assert.False(later.HasCommands);
        Assert.False(later.Manual);
    }

    private static PersonSettings Person() => Ctx.Person(new[] { "en" }, new[] { "en" });

    private static User TestUser() => new("Test", "Default", "Default") { Id = Ids.Person };

    private static Episode Episode() => new()
    {
        Id = Ids.Item,
        Name = "Episode 1",
        SeasonId = Ids.Season,
        SeriesId = Ids.Series,
        SeriesName = "Skyward Lantern Voyage",
        SeasonName = "Season 1",
    };

    private static MediaSourceInfo Source() => new()
    {
        Id = Ids.N(Ids.Item),
        MediaStreams = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng", Title = "Surround", Codec = "aac", Channels = 6 },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = "jpn", Title = "Surround", Codec = "aac", Channels = 2 },
            new() { Index = 3, Type = MediaStreamType.Audio, Language = "eng", Title = "Commentary", Codec = "aac", Channels = 2 },
            new() { Index = 4, Type = MediaStreamType.Subtitle, Language = "eng", Title = "Full", Codec = "ass" },
            new() { Index = 5, Type = MediaStreamType.Subtitle, Language = "eng", Title = "Signs & Songs", Codec = "ass" },
            new() { Index = 6, Type = MediaStreamType.Subtitle, Language = "en-US", Title = "English", Codec = "subrip" },
        },
    };

    private static (TrackChangeRecorder Recorder, FakePersonStore Store, MemoryActivityLog Activity) Recorder(PersonSettings person, ItemContext? context = null)
    {
        var store = new FakePersonStore();
        store.Save(person);
        var activity = new MemoryActivityLog();
        var resolver = new FixedResolver(store, context ?? Ctx.Episode());
        return (new TrackChangeRecorder(resolver, store, activity, NullLogger<TrackChangeRecorder>.Instance), store, activity);
    }

    // A recorder whose store runs AfterRead once, right after the recorder's own read of the document: another writer coming in at the worst moment.
    private static (TrackChangeRecorder Recorder, FakePersonStore Store, RacingStore Racing) RacingRecorder(PersonSettings person, ItemContext? context = null)
    {
        var store = new FakePersonStore();
        store.Save(person);
        var racing = new RacingStore(store);
        var resolver = new FixedResolver(store, context ?? Ctx.Episode());
        return (new TrackChangeRecorder(resolver, racing, racing.Activity, NullLogger<TrackChangeRecorder>.Instance), store, racing);
    }

    /// <summary>A store that hands every call on to another and, once, runs something right after a read.</summary>
    private sealed class RacingStore : IPersonStore
    {
        private readonly FakePersonStore _inner;

        public RacingStore(FakePersonStore inner) => _inner = inner;

        public event EventHandler<PersonStoreChangedEventArgs>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public MemoryActivityLog Activity { get; } = new();

        public Action? AfterRead { get; set; }

        public string DataFolder => _inner.DataFolder;

        public PersonSettings? TryGet(Guid userId)
        {
            var read = _inner.TryGet(userId);
            var after = AfterRead;
            AfterRead = null;
            after?.Invoke();
            return read;
        }

        public PersonSettings GetOrCreate(Guid userId, Func<PersonSettings> create) => _inner.GetOrCreate(userId, create);

        public void Save(PersonSettings settings) => _inner.Save(settings);

        public bool Update(Guid userId, Func<PersonSettings?, PersonSettings?> change) => _inner.Update(userId, change);

        public bool Delete(Guid userId) => _inner.Delete(userId);

        public IReadOnlyList<Guid> UserIds() => _inner.UserIds();
    }

    /// <summary>An in-memory activity log.</summary>
    private sealed class MemoryActivityLog : IActivityLog
    {
        public List<ActivityEntry> Entries { get; } = new();

        public void Add(ActivityEntry entry) => Entries.Add(entry);

        public IReadOnlyList<ActivityEntry> Query(int limit, string? kind = null, Guid? userId = null)
            => Entries.AsEnumerable().Reverse()
                .Where(e => (kind is null || e.Kind == kind) && (userId is null || e.UserId == userId))
                .Take(limit)
                .ToList();
    }

    /// <summary>A resolver that runs the real precedence over the store's document for one prepared context.</summary>
    private sealed class FixedResolver : IProfileResolver
    {
        private readonly IPersonStore _store;
        private readonly ItemContext _context;

        public FixedResolver(IPersonStore store, ItemContext context)
        {
            _store = store;
            _context = context;
        }

        public ResolvedProfileResult Resolve(User user, BaseItem item, string? client = null) => Resolve(user, _context with { Client = client });

        public ResolvedProfileResult Resolve(User user, ItemContext context)
            => ProfileResolver.Resolve(_store.TryGet(user.Id) ?? new PersonSettings { UserId = user.Id }, new PluginConfiguration(), context, LanguageTable.Default);

        public void Invalidate(Guid? userId)
        {
        }
    }
}
