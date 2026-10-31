using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Classification;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[CollectionDefinition("Playback response safety globals", DisableParallelization = true)]
public sealed class PlaybackResponseSafetyCollection { }

[Collection("Playback response safety globals")]
public sealed class PlaybackResponseSafetyTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "babelfin-playback-response-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, _previous);
    }

    [Theory]
    [InlineData(false, -20)]
    [InlineData(true, -20)]
    [InlineData(false, -5)]
    [InlineData(true, -5)]
    public async Task OnlyAStartedOtherItemSuppressesAServedPickerChoice(bool hasStart, int servedOffset)
    {
        var fixture = Create();
        fixture.ServeAt(T0.AddSeconds(servedOffset));
        fixture.Registry.Update("session", play =>
        {
            play.UserId = fixture.User.Id;
            play.ItemId = Guid.NewGuid();
            play.Playing = true;
            play.StartedAt = hasStart ? T0.AddSeconds(-10) : null;
            play.LastProgressAt = T0.AddSeconds(-1);
            play.LastStoppedItemId = Guid.NewGuid();
            play.LastStoppedAt = T0.AddSeconds(-30);
        });

        for (var request = 0; request < 2; request++)
        {
            var context = await fixture.Request("Jellyfin Web", 1);
            if (hasStart)
            {
                Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
                Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
                Assert.False(fixture.Registry.Get("session")!.ManualChanged);
            }
            else
            {
                Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
                Assert.Equal(1, context.ActionArguments["audioStreamIndex"]);
            }
        }
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task OnlyAPickerServedAfterTheLatestStopCanRememberDuringAutoplay(int servedOffset, bool manual)
    {
        var fixture = Create();
        fixture.ServeAt(T0.AddSeconds(-10 + servedOffset));
        fixture.Registry.Update("session", play =>
        {
            play.UserId = fixture.User.Id;
            play.Playing = false;
            play.LastStoppedItemId = Guid.NewGuid();
            play.LastStoppedAt = T0.AddSeconds(-10);
        });

        var context = await fixture.Request("Jellyfin Web", 1);

        Assert.Equal(manual ? 1 : 2, context.ActionArguments["audioStreamIndex"]);
        if (manual)
        {
            var remembered = Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
            Assert.Equal(OverrideScope.Series, remembered.Scope);
            Assert.Equal(Ids.N(Ids.Series), remembered.ItemId);
            Assert.Equal("en", remembered.Manual!.Audio!.Language);
        }
        else Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheNextItemFetchedRightBeforeAnAutoplayRequestIsNotAPickerChoice(bool servedBeforeTheStopToo)
    {
        var fixture = Create();
        if (servedBeforeTheStopToo) fixture.ServeAt(T0.AddSeconds(-600));
        fixture.StopOtherItem(T0.AddSeconds(-2));
        fixture.ServeAt(T0.AddSeconds(-1));

        var context = await fixture.Request("Jellyfin Web", 1);

        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.False(fixture.Registry.Get("session")!.ManualChanged);
    }

    [Fact]
    public async Task APickerOpenedAndRefetchedDuringTheAutoplayWindowIsStillARememberedChoice()
    {
        var fixture = Create();
        fixture.StopOtherItem(T0.AddSeconds(-30));
        fixture.ServeAt(T0.AddSeconds(-20));
        fixture.ServeAt(T0.AddSeconds(-1));

        var context = await fixture.Request("Jellyfin Web", 1);

        Assert.Equal(1, context.ActionArguments["audioStreamIndex"]);
        var remembered = Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.Equal(OverrideScope.Series, remembered.Scope);
        Assert.Equal(Ids.N(Ids.Series), remembered.ItemId);
        Assert.Equal("en", remembered.Manual!.Audio!.Language);
        Assert.True(fixture.Registry.Get("session")!.ManualChanged);
    }

    [Fact]
    public async Task AutoplayCarryingOverASignsAndSongsSubtitleIsReplacedWithTheFullOne()
    {
        var fixture = Create();
        fixture.NewSource = SignsAndSongsSource;
        fixture.Choice = new TrackDecision(2, 3, Array.Empty<string>());
        fixture.StopOtherItem(T0.AddSeconds(-2));
        fixture.ServeAt(T0.AddSeconds(-1));

        var context = await fixture.Request("Jellyfin Web", 2, subtitle: 4);

        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(3, context.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.False(fixture.Registry.Get("session")!.ManualChanged);
    }

    // The previous episode's full subtitles carried over by position: the same language and kind, but not Babelfin's track.
    [Fact]
    public async Task AutoplayCarryingOverAnotherFullSubtitleOfTheSameLanguageIsReplaced()
    {
        var fixture = Create();
        fixture.NewSource = TwoFullSubtitlesSource;
        fixture.Choice = new TrackDecision(2, 5, Array.Empty<string>());
        fixture.StopOtherItem(T0.AddSeconds(-2));
        fixture.ServeAt(T0.AddSeconds(-1));

        var context = await fixture.Request("Jellyfin Web", 2, subtitle: 3);

        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(5, context.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.False(fixture.Registry.Get("session")!.ManualChanged);
        Assert.False(fixture.Registry.Get("session")!.Trusted);
    }

    [Fact]
    public async Task OutsideAutoplayTheSameRequestIsTheAppsOwnChoice()
    {
        var fixture = Create();
        fixture.NewSource = TwoFullSubtitlesSource;
        fixture.Choice = new TrackDecision(2, 5, Array.Empty<string>());

        var context = await fixture.Request("Jellyfin Web", 2, subtitle: 3);

        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(3, context.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.True(fixture.Registry.Get("session")!.Trusted);
    }

    public static TheoryData<string> CarryOverCases() => new(CarryOvers.Keys);

    public static TheoryData<string> PickCases() => new(Picks.Keys);

    // The app ranks the next item's tracks from the previous item's: a copy can name a track of the same language and kind, or another one.
    [Theory]
    [MemberData(nameof(CarryOverCases))]
    public async Task AutoplayCarryOverInAnyLanguageIsReplacedWithBabelfinsChoice(string carryOver)
    {
        var c = CarryOvers[carryOver];
        var source = c.Source();
        var (copiedTrack, chosenTrack) = c.Copied.Audio != c.Choice.Audio
            ? (TrackComparer.Describe(source, c.Copied.Audio, TrackType.Audio), TrackComparer.Describe(source, c.Choice.Audio, TrackType.Audio))
            : (TrackComparer.Describe(source, c.Copied.Subtitle, TrackType.Subtitle), TrackComparer.Describe(source, c.Choice.Subtitle, TrackType.Subtitle));
        Assert.Equal(c.SameLanguageAndKind, copiedTrack is { } copied && chosenTrack is { } chosen
            && (copied.Language, copied.Variety, copied.Kind) == (chosen.Language, chosen.Variety, chosen.Kind));

        var fixture = Create();
        fixture.NewSource = c.Source;
        fixture.Choice = new TrackDecision(c.Choice.Audio, c.Choice.Subtitle, Array.Empty<string>());
        fixture.StopOtherItem(T0.AddSeconds(-2));
        fixture.ServeAt(T0.AddSeconds(-1));

        var context = await fixture.Request("Jellyfin Web", c.Copied.Audio, subtitle: c.Copied.Subtitle);

        Assert.Equal(c.Choice.Audio, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(c.Choice.Subtitle, context.ActionArguments["subtitleStreamIndex"]);
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.False(fixture.Registry.Get("session")!.ManualChanged);
    }

    [Theory]
    [MemberData(nameof(PickCases))]
    public async Task APickerChoiceInAnyLanguageDuringTheAutoplayWindowIsRemembered(string pick)
    {
        var p = Picks[pick];
        var fixture = Create();
        fixture.NewSource = p.Source;
        fixture.Choice = new TrackDecision(p.Choice.Audio, p.Choice.Subtitle, Array.Empty<string>());
        fixture.StopOtherItem(T0.AddSeconds(-30));
        fixture.ServeAt(T0.AddSeconds(-20));
        fixture.ServeAt(T0.AddSeconds(-1));

        var context = await fixture.Request("Jellyfin Web", p.Picked.Audio, subtitle: p.Picked.Subtitle);

        Assert.Equal(p.Picked.Audio, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(p.Picked.Subtitle, context.ActionArguments["subtitleStreamIndex"]);
        var remembered = Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.Equal(OverrideScope.Series, remembered.Scope);
        Assert.Equal(Ids.N(Ids.Series), remembered.ItemId);
        var manual = remembered.Manual!;
        var (track, unchanged) = p.Changed == TrackType.Audio ? (manual.Audio, manual.Subtitle) : (manual.Subtitle, manual.Audio);
        Assert.Null(unchanged);
        Assert.False(manual.SubtitlesOff);
        Assert.NotNull(track);
        Assert.Equal(p.Remembered, (track.Language, track.Variety, track.Kind));
        Assert.True(fixture.Registry.Get("session")!.ManualChanged);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("playback")]
    [InlineData("defaults")]
    public async Task DeciderFailureReplacesTheOldDecisionAndPreservesServedHistory(string entryPoint)
    {
        var fixture = Create();
        fixture.ServeAt(T0.AddSeconds(-5));
        var served = fixture.Stats.GetServed(fixture.User.Id, fixture.Item.Id, "source", "device", "Jellyfin Web");
        fixture.FailDecision = true;
        var source = Source();

        if (entryPoint == "static") source = Assert.Single(fixture.Manager.GetStaticMediaSources(fixture.Item, true, fixture.User));
        else if (entryPoint == "playback") source = Assert.Single(await fixture.Manager.GetPlaybackMediaSources(fixture.Item, fixture.User, false, true, CancellationToken.None));
        else fixture.Manager.SetDefaultAudioAndSubtitleStreamIndices(fixture.Item, source, fixture.User);

        Assert.Equal((1, -1), (source.DefaultAudioStreamIndex, source.DefaultSubtitleStreamIndex));
        var latest = fixture.Stats.GetDecision(fixture.User.Id, fixture.Item.Id, source.Id);
        Assert.NotNull(latest);
        Assert.Null(latest.Decision);
        Assert.Equal(DecisionOutcome.PassedThrough, latest.Outcome);
        Assert.Equal((1, -1), (latest.JellyfinAudioIndex, latest.JellyfinSubtitleIndex));
        Assert.Equal(1, fixture.Stats.Errors);
        Assert.Same(served, fixture.Stats.GetServed(fixture.User.Id, fixture.Item.Id, source.Id, "device", "Jellyfin Web"));
        fixture.Stats.RecordResponse(fixture.User.Id, source, "device", "Jellyfin Web");
        Assert.Equal(1, fixture.Stats.GetServed(fixture.User.Id, fixture.Item.Id, source.Id, "device", "Jellyfin Web")!.JellyfinAudioIndex);
    }

    [Fact]
    public async Task PlaybackInfoAfterADeciderFailureDoesNotReuseTheOldDecision()
    {
        var fixture = Create();
        fixture.Manager.GetStaticMediaSources(fixture.Item, true, fixture.User);
        fixture.FailDecision = true;
        fixture.Config.CompatibilityMode = true;
        fixture.Config.FindAppMode("Jellyfin Web")!.Mode = AppModeKind.ReplaceUnlessManual;

        var context = await fixture.Request("Jellyfin Web", 2);

        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.False(context.ActionArguments.ContainsKey("mediaSourceId"));
        Assert.Null(fixture.Registry.Get("session"));
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.Empty(fixture.Compatibility.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ClientNamesCannotCauseUnboundedConfigurationSaveAttempts(bool failSave, bool rotateName)
    {
        var fixture = Create();
        fixture.Plugin.FailSave = failSave;
        var client = "invented-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 150; i++)
        {
            await fixture.Request(rotateName ? "invented-" + Guid.NewGuid().ToString("N") : client, null);
        }

        Assert.Equal(1, fixture.Plugin.SaveAttempts);
        Assert.True(fixture.Config.AppModes.Count(row => row.SeenOnly) <= 50);
        Assert.NotNull(fixture.Config.FindAppMode("Jellyfin Web"));
    }

    [Fact]
    public async Task ConfiguredClientModeStillAppliesWhenDiscoveryWritesAreThrottled()
    {
        var fixture = Create();
        await fixture.Request("invented-" + Guid.NewGuid().ToString("N"), null);
        var client = "configured-" + Guid.NewGuid().ToString("N");
        fixture.Config.AppModes.Add(new AppMode { Client = client, Mode = AppModeKind.ReplaceUnlessManual });
        var context = await fixture.Request(client, 1);
        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(1, fixture.Plugin.SaveAttempts);
    }

    [Fact]
    public async Task InPlayChangesStillRememberWhenAnotherItemStoppedEarlier()
    {
        var fixture = Create();
        fixture.ServeAt(T0.AddSeconds(-20));
        fixture.Registry.Update("session", play =>
        {
            play.UserId = fixture.User.Id;
            play.ItemId = fixture.Item.Id;
            play.Playing = true;
            play.StartedAt = T0.AddSeconds(-10);
            play.LastProgressAt = T0.AddSeconds(-1);
            play.HasExpected = true;
            play.ExpectedAudio = 2;
            play.ExpectedSubtitle = -1;
            play.LastStoppedItemId = Guid.NewGuid();
            play.LastStoppedAt = T0.AddSeconds(-30);
        });
        var context = await fixture.Request("Jellyfin Web", 1);
        Assert.Equal(1, context.ActionArguments["audioStreamIndex"]);
        Assert.True(fixture.Registry.Get("session")!.ManualChanged);
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        fixture.SetTime(T0 + PlaybackMonitor.SettlePeriod);
        fixture.Report(1, -1);
        Assert.Equal(OverrideScope.Series, Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides).Scope);
    }

    [Fact]
    public async Task ThrottledDiscoveryUpdatesAreSavedWithTheNextHourlyAttempt()
    {
        var fixture = Create();
        var first = "first-" + Guid.NewGuid().ToString("N");
        var pending = "pending-" + Guid.NewGuid().ToString("N");
        await fixture.Request(first, null);
        await fixture.Request(pending, null);
        Assert.Equal(1, fixture.Plugin.SaveAttempts);
        Assert.DoesNotContain(pending, fixture.Plugin.SavedClients);
        Assert.NotNull(fixture.Config.FindAppMode(pending));

        fixture.SetTime(T0.AddHours(1).AddSeconds(1));
        await fixture.Request(first, null);

        Assert.Equal(2, fixture.Plugin.SaveAttempts);
        Assert.Contains(pending, fixture.Plugin.SavedClients);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedNextItemKeepsTheActivePlayAndAdoptsExpectedTracks(bool stopBeforeAdoption)
    {
        var fixture = Create();
        var previousItem = Guid.NewGuid();
        fixture.ServeAt(T0.AddSeconds(-20));
        fixture.Registry.Update("session", play =>
        {
            play.UserId = fixture.User.Id;
            play.ItemId = previousItem;
            play.Playing = true;
            play.StartedAt = T0.AddSeconds(-10);
            play.LastProgressAt = T0.AddSeconds(-1);
            play.HasExpected = true;
            play.ExpectedAudio = 1;
            play.LastStoppedAt = T0.AddSeconds(-30);
            play.LastStoppedItemId = Guid.NewGuid();
        });

        await fixture.Request("Jellyfin Web", 1);

        var active = fixture.Registry.Get("session")!;
        Assert.Equal(previousItem, active.ItemId);
        Assert.Equal(1, active.ExpectedAudio);
        var pending = fixture.Registry.GetPending("device", "Jellyfin Web", fixture.Item.Id);
        Assert.NotNull(pending);
        Assert.Equal(2, pending.ExpectedAudio);
        if (stopBeforeAdoption)
        {
            fixture.Registry.Update("session", play =>
            {
                play.LastStoppedItemId = previousItem;
                play.LastStoppedAt = T0.AddSeconds(1);
                play.ResetPlay();
            });
            fixture.SetTime(T0.AddSeconds(1));
            await fixture.Request("Jellyfin Web", 1);
        }
        else Assert.NotNull(fixture.Registry.Adopt("session", "device", "Jellyfin Web", fixture.Item.Id, fixture.User.Id));

        var adopted = fixture.Registry.Get("session")!;
        Assert.Equal(fixture.Item.Id, adopted.ItemId);
        Assert.Equal(2, adopted.ExpectedAudio);
        Assert.False(adopted.ManualChanged);
        Assert.Null(fixture.Registry.GetPending("device", "Jellyfin Web", fixture.Item.Id));
        Assert.Empty(fixture.Store.TryGet(fixture.User.Id)!.Overrides);

        if (stopBeforeAdoption)
        {
            fixture.ServeAt(T0.AddSeconds(2));
            fixture.SetTime(T0.AddSeconds(2) + PlaybackInfoFilter.PickerUseTime);
            await fixture.Request("Jellyfin Web", 1);
            Assert.Equal(1, fixture.Registry.Get("session")!.ExpectedAudio);
            Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        }
    }
    [Theory]
    [InlineData("success-not-started")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task AnUnstartedRequestDoesNotReplaceTheNextItemsRealPickerChoice(string outcome)
    {
        var fixture = Create();
        if (outcome == "failed") await Assert.ThrowsAsync<IOException>(() => fixture.Request("Jellyfin Web", null, outcome));
        else if (outcome == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Request("Jellyfin Web", null, outcome));
        else await fixture.Request("Jellyfin Web", null, outcome);
        var prepared = fixture.Registry.Get("session")!;
        Assert.False(prepared.Playing);
        Assert.Null(prepared.StartedAt);
        Assert.Null(prepared.LastProgressAt);
        fixture.Item.Id = Guid.NewGuid();
        fixture.ServeAt(T0.AddSeconds(1));
        fixture.SetTime(T0.AddSeconds(2));
        var picked = await fixture.Request("Jellyfin Web", 1);
        Assert.Equal(1, picked.ActionArguments["audioStreamIndex"]);
        Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
        Assert.False(fixture.Registry.Get("session")!.Playing);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -121)]
    [InlineData(true, -3600)]
    public async Task AnUnstartedOrStaleOtherItemDoesNotCountAsAnAutoplayTransition(bool started, int progressSeconds)
    {
        var fixture = Create();
        fixture.ServeAt(T0.AddSeconds(-1));
        fixture.Registry.Update("session", play =>
        {
            play.UserId = fixture.User.Id;
            play.ItemId = Guid.NewGuid();
            play.Playing = true;
            play.StartedAt = started ? T0.AddHours(-2) : null;
            play.LastProgressAt = T0.AddSeconds(progressSeconds);
        });
        var picked = await fixture.Request("Jellyfin Web", 1);
        Assert.Equal(1, picked.ActionArguments["audioStreamIndex"]);
        Assert.Single(fixture.Store.TryGet(fixture.User.Id)!.Overrides);
    }

    [Theory]
    [InlineData("other-device", "Jellyfin Web", false)]
    [InlineData("device", "Other app", false)]
    [InlineData("DEVICE", "Jellyfin Web", false)]
    [InlineData("", "Jellyfin Web", false)]
    [InlineData("device", "", false)]
    [InlineData("device", "jellyfin web", true)]
    [InlineData("device", "Jellyfin Web", true)]
    public async Task APrePlayPickIsRememberedOnlyForTheDeviceAndAppThatReceivedThePicker(string servedDevice, string servedClient, bool remember)
    {
        var fixture = Create();
        var source = Assert.Single(fixture.Manager.GetStaticMediaSources(fixture.Item, true, fixture.User));
        using var services = new ServiceCollection().AddSingleton(fixture.Stats).BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimsHelper.UserIdClaim, fixture.User.Id.ToString()),
                new Claim(ClaimsHelper.DeviceIdClaim, servedDevice),
                new Claim(ClaimsHelper.ClientClaim, servedClient),
            }, "test")),
        };
        var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor(), new ModelStateDictionary());
        var result = new ObjectResult(new { MediaSources = new[] { source } });
        await new Jellyfin.Plugin.Babelfin.Web.GuardedPlaybackInfoFilter().OnResultExecutionAsync(
            new ResultExecutingContext(action, new List<IFilterMetadata>(), result, new object()),
            () => Task.FromResult(new ResultExecutedContext(action, new List<IFilterMetadata>(), result, new object())));
        await fixture.Request("Jellyfin Web", 1);
        Assert.Equal(remember ? 1 : 0, fixture.Store.TryGet(fixture.User.Id)!.Overrides.Count);
    }

    private Fixture Create()
    {
        Directory.CreateDirectory(_folder);
        var paths = CallProxy.Create<IApplicationPaths>((method, _) => method.ReturnType == typeof(string) ? _folder : null).Proxy;
        var config = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
        return new Fixture(new CountingPlugin(paths, CallProxy.Create<IXmlSerializer>().Proxy, config));
    }

    private sealed class CountingPlugin : Plugin
    {
        internal int SaveAttempts;
        internal bool FailSave;
        internal string[] SavedClients = Array.Empty<string>();
        internal CountingPlugin(IApplicationPaths paths, IXmlSerializer xml, PluginConfiguration config) : base(paths, xml)
        {
            Configuration = config;
            SaveAttempts = 0;
        }
        public override void SaveConfiguration()
        {
            SaveAttempts++;
            if (FailSave) throw new IOException("Test configuration write failure");
            SavedClients = Configuration.AppModes.Select(row => row.Client).ToArray();
        }
    }

    private static MediaSourceInfo Source() => new()
    {
        Id = "source", DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = -1,
        MediaStreams = new List<MediaStream>
        {
            new() { Index = 1, Type = MediaStreamType.Audio, Language = "eng", Codec = "aac", Channels = 2 },
            new() { Index = 2, Type = MediaStreamType.Audio, Language = "jpn", Codec = "aac", Channels = 2 },
        },
    };

    private static MediaSourceInfo SignsAndSongsSource()
    {
        var source = Source();
        source.MediaStreams = source.MediaStreams.Concat(new[]
        {
            new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Language = "eng", Title = "Full", Codec = "subrip" },
            new MediaStream { Index = 4, Type = MediaStreamType.Subtitle, Language = "eng", Title = "Signs & Songs", Codec = "subrip" },
        }).ToList();
        return source;
    }

    private static MediaSourceInfo TwoFullSubtitlesSource()
    {
        var source = Source();
        source.MediaStreams = source.MediaStreams.Concat(new[]
        {
            new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Language = "eng", Title = "Full Sub", Codec = "PGSSUB" },
            new MediaStream { Index = 4, Type = MediaStreamType.Subtitle, Language = "eng", Title = "Signs & Songs", Codec = "ass", IsDefault = true },
            new MediaStream { Index = 5, Type = MediaStreamType.Subtitle, Language = "eng", Title = "Dialogue", Codec = "ass" },
        }).ToList();
        return source;
    }

    private static readonly Dictionary<string, CarryOver> CarryOvers = new(StringComparer.Ordinal)
    {
        ["fr: another full French subtitle"] = new(
            () => SourceOf(AudioStream(1, "fre", "Français"), AudioStream(2, "jpn", "日本語"), SubtitleStream(3, "fre", "Français"), SubtitleStream(4, "fre", "Français (dialogues)", "ass")),
            Choice: (2, 4), Copied: (2, 3), SameLanguageAndKind: true),
        ["de: another German audio (stereo over 5.1)"] = new(
            () => SourceOf(AudioStream(1, "ger", "Deutsch Stereo"), AudioStream(2, "ger", "Deutsch 5.1", "ac3", 6), AudioStream(3, "jpn", "日本語"), SubtitleStream(4, "ger", "Schilder & Songs", forced: true)),
            Choice: (2, 4), Copied: (1, 4), SameLanguageAndKind: true),
        ["es: another full Spanish subtitle (image)"] = new(
            () => SourceOf(AudioStream(1, "jpn", "日本語"), SubtitleStream(2, "spa", "Español", "PGSSUB"), SubtitleStream(3, "spa", "Español")),
            Choice: (1, 3), Copied: (1, 2), SameLanguageAndKind: true),
        ["en audio copied over a Japanese choice"] = new(
            () => SourceOf(AudioStream(1, "eng", "English"), AudioStream(2, "jpn", "日本語"), SubtitleStream(3, "eng", "Signs & Songs", forced: true), SubtitleStream(4, "eng", "Dialogue")),
            Choice: (2, 4), Copied: (1, 4), SameLanguageAndKind: false),
        ["pt-BR subtitles copied over a pt-PT choice"] = new(
            () => SourceOf(AudioStream(1, "jpn", "日本語"), SubtitleStream(2, "por", "Português (Brasil)"), SubtitleStream(3, "por", "Português (Portugal)")),
            Choice: (1, 3), Copied: (1, 2), SameLanguageAndKind: false),
        ["zh-Hans subtitles copied over a zh-Hant choice"] = new(
            () => SourceOf(AudioStream(1, "jpn", "日本語"), SubtitleStream(2, "chi", "简体"), SubtitleStream(3, "chi", "繁體")),
            Choice: (1, 3), Copied: (1, 2), SameLanguageAndKind: false),
        ["de Schilder & Songs over a German full choice"] = new(
            () => SourceOf(AudioStream(1, "jpn", "日本語"), SubtitleStream(2, "ger", "Schilder & Songs", "ass", forced: true), SubtitleStream(3, "ger", "Vollständig", "ass")),
            Choice: (1, 3), Copied: (1, 2), SameLanguageAndKind: false),
        ["en subtitles copied over a Korean choice"] = new(
            () => SourceOf(AudioStream(1, "kor", "한국어"), SubtitleStream(2, "eng", "English"), SubtitleStream(3, "kor", "한국어")),
            Choice: (1, 3), Copied: (1, 2), SameLanguageAndKind: false),
        ["it subtitles copied while Babelfin chose off"] = new(
            () => SourceOf(AudioStream(1, "ita", "Italiano"), AudioStream(2, "eng", "English"), SubtitleStream(3, "ita", "Italiano")),
            Choice: (1, -1), Copied: (1, 3), SameLanguageAndKind: false),
        ["off copied while Babelfin chose French full"] = new(
            () => SourceOf(AudioStream(1, "jpn", "日本語"), SubtitleStream(2, "fre", "Français")),
            Choice: (1, 2), Copied: (1, -1), SameLanguageAndKind: false),
        ["fr audio + Forcés over ja + French Complets"] = new(
            () => SourceOf(AudioStream(1, "fre", "VF"), AudioStream(2, "jpn", "VO"), SubtitleStream(3, "fre", "Forcés", "ass", forced: true), SubtitleStream(4, "fre", "Complets", "ass")),
            Choice: (2, 4), Copied: (1, 3), SameLanguageAndKind: false),
    };

    private static readonly Dictionary<string, Pick> Picks = new(StringComparer.Ordinal)
    {
        ["de audio over a French choice"] = new(
            () => SourceOf(AudioStream(1, "fre", "Français"), AudioStream(2, "ger", "Deutsch")),
            Choice: (1, -1), Picked: (2, -1), TrackType.Audio, Remembered: ("de", null, "normal")),
        ["pt-PT audio over a pt-BR choice"] = new(
            () => SourceOf(AudioStream(1, "por", "Português (Brasil)"), AudioStream(2, "por", "Português (Portugal)")),
            Choice: (1, -1), Picked: (2, -1), TrackType.Audio, Remembered: ("pt", "pt-PT", "normal")),
        ["yue audio over a Mandarin choice"] = new(
            () => SourceOf(AudioStream(1, "chi", "Mandarin"), AudioStream(2, "chi", "Cantonese")),
            Choice: (1, -1), Picked: (2, -1), TrackType.Audio, Remembered: ("zh", "yue", "normal")),
        ["zh-Hant subtitles over a zh-Hans choice"] = new(
            () => SourceOf(AudioStream(1, "jpn", "日本語"), SubtitleStream(2, "chi", "简体"), SubtitleStream(3, "chi", "繁體")),
            Choice: (1, 2), Picked: (1, 3), TrackType.Subtitle, Remembered: ("zh", "zh-Hant", "full")),
        ["Spanish Carteles over a Spanish full choice"] = new(
            () => SourceOf(AudioStream(1, "spa", "Español"), SubtitleStream(2, "spa", "Carteles", "ass", forced: true), SubtitleStream(3, "spa", "Completos", "ass")),
            Choice: (1, 3), Picked: (1, 2), TrackType.Subtitle, Remembered: ("es", null, "partial")),
        ["French SDH over a French full choice"] = new(
            () => SourceOf(AudioStream(1, "jpn", "日本語"), SubtitleStream(2, "fre", "Français"), SubtitleStream(3, "fre", "Français SDH", hearingImpaired: true)),
            Choice: (1, 2), Picked: (1, 3), TrackType.Subtitle, Remembered: ("fr", null, "sdh")),
    };

    private static MediaSourceInfo SourceOf(params MediaStream[] streams) => new()
    {
        Id = "source", DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = -1, MediaStreams = streams.ToList(),
    };

    private static MediaStream AudioStream(int index, string language, string title, string codec = "aac", int channels = 2) =>
        new() { Index = index, Type = MediaStreamType.Audio, Language = language, Title = title, Codec = codec, Channels = channels };

    private static MediaStream SubtitleStream(int index, string language, string title, string codec = "subrip", bool forced = false, bool hearingImpaired = false) =>
        new() { Index = index, Type = MediaStreamType.Subtitle, Language = language, Title = title, Codec = codec, IsForced = forced, IsHearingImpaired = hearingImpaired };

    /// <summary>An autoplay request naming the previous item's tracks.</summary>
    /// <param name="Source">The next item's source, new on every call.</param>
    /// <param name="Choice">Babelfin's choice (subtitle -1 = off).</param>
    /// <param name="Copied">The indices the app copied from the previous item.</param>
    /// <param name="SameLanguageAndKind">Whether the copied track that differs has the language, variety and kind of Babelfin's.</param>
    private sealed record CarryOver(Func<MediaSourceInfo> Source, (int Audio, int Subtitle) Choice, (int Audio, int Subtitle) Copied, bool SameLanguageAndKind);

    /// <summary>A pre-play picker choice that differs from Babelfin's in one track.</summary>
    /// <param name="Source">The item's source, new on every call.</param>
    /// <param name="Choice">Babelfin's choice (subtitle -1 = off).</param>
    /// <param name="Picked">The indices the person picked.</param>
    /// <param name="Changed">The track the person changed.</param>
    /// <param name="Remembered">The stored language base, variety and kind of the picked track.</param>
    private sealed record Pick(Func<MediaSourceInfo> Source, (int Audio, int Subtitle) Choice, (int Audio, int Subtitle) Picked, TrackType Changed, (string Language, string? Variety, string Kind) Remembered);

    private sealed class Fixture
    {
        internal readonly CountingPlugin Plugin;
        internal PluginConfiguration Config => Plugin.Configuration;
        internal readonly User User = new("Playback test", "Default", "Default") { Id = Ids.Person };
        internal readonly Episode Item = new() { Id = Ids.Item, Name = "Episode", SeriesId = Ids.Series, SeasonId = Ids.Season, SeriesName = "Test show" };
        internal readonly FakePersonStore Store = new();
        internal readonly PlayRegistry Registry;
        internal readonly DecoratorStats Stats;
        internal readonly BabelfinMediaSourceManager Manager;
        internal readonly CallProxy Compatibility;
        internal bool FailDecision;
        internal TrackDecision Choice = new(2, -1, Array.Empty<string>());
        internal Func<MediaSourceInfo> NewSource = Source;
        private DateTime _now = T0;
        private readonly Func<PlaybackInfoFilter> _newFilter;
        private readonly PlaybackMonitor _monitor;

        internal Fixture(CountingPlugin plugin)
        {
            Plugin = plugin;
            Stats = new DecoratorStats(() => _now);
            Registry = new PlayRegistry(() => _now);
            var person = Ctx.Person(new[] { "en" }, new[] { "en" });
            person.Everyday.Memory = MemoryScope.RestOfShow;
            Store.Save(person);
            var resolver = CallProxy.Create<IProfileResolver>((method, _) => method.Name == "Resolve" ? Ctx.Resolve(Store.TryGet(User.Id)!, Config, Ctx.Episode()) : null).Proxy;
            var activity = new ListActivityLog();
            var recorder = new TrackChangeRecorder(resolver, Store, activity, NullLogger<TrackChangeRecorder>.Instance);
            var inner = CallProxy.Create<IMediaSourceManager>((method, _) => method.Name switch
            {
                nameof(IMediaSourceManager.GetStaticMediaSources) => new[] { NewSource() },
                nameof(IMediaSourceManager.GetPlaybackMediaSources) => Task.FromResult<IReadOnlyList<MediaSourceInfo>>(new[] { NewSource() }),
                _ => null,
            }).Proxy;
            var decider = CallProxy.Create<ITrackDecider>((_, _) => FailDecision ? throw new InvalidOperationException("Test decider failure") : Choice).Proxy;
            Manager = new BabelfinMediaSourceManager(inner, decider, Stats, NullLogger.Instance);
            var users = CallProxy.Create<IUserManager>((method, _) => method.Name == nameof(IUserManager.GetUserById) ? User : null).Proxy;
            var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name == nameof(ILibraryManager.GetItemById) ? Item : null).Proxy;
            var sessions = CallProxy.Create<ISessionManager>((method, _) => method.Name == "get_Sessions" ? new[] { new SessionInfo(null!, NullLogger.Instance) { Id = "session", DeviceId = "device", Client = "Jellyfin Web", UserId = User.Id } } : null).Proxy;
            var compatibility = CallProxy.Create<ICompatibilityWriter>();
            Compatibility = compatibility.Recorder;
            _newFilter = () => new PlaybackInfoFilter(users, library, Manager, Stats, Registry, recorder, sessions, activity, compatibility.Proxy, NullLogger<PlaybackInfoFilter>.Instance);
            _monitor = new PlaybackMonitor(sessions, Registry, recorder, Manager, users, library, activity, NullLogger<PlaybackMonitor>.Instance, Stats);
        }

        internal void SetTime(DateTime time) => _now = time;

        internal void ServeAt(DateTime time)
        {
            _now = time;
            var source = Assert.Single(Manager.GetStaticMediaSources(Item, true, User));
            Stats.RecordResponse(User.Id, source, "device", "Jellyfin Web");
            _now = T0;
        }

        internal void Report(int? audio, int? subtitle)
        {
            var session = new SessionInfo(null!, NullLogger.Instance)
            {
                Id = "session", DeviceId = "device", Client = "Jellyfin Web", UserId = User.Id,
                PlayState = new PlayerStateInfo { AudioStreamIndex = audio, SubtitleStreamIndex = subtitle, MediaSourceId = "source" },
            };
            typeof(PlaybackMonitor).GetMethod("HandleReport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_monitor, new object?[] { new PlaybackProgressEventArgs { Session = session, Item = Item, MediaSourceId = "source" } });
        }

        internal void StopOtherItem(DateTime time) => Registry.Update("session", play =>
        {
            play.UserId = User.Id;
            play.Playing = false;
            play.LastStoppedItemId = Guid.NewGuid();
            play.LastStoppedAt = time;
        });

        internal async Task<ActionExecutingContext> Request(string client, int? audio, string outcome = "success-not-started", int? subtitle = null)
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimsHelper.UserIdClaim, User.Id.ToString()),
                    new Claim(ClaimsHelper.ClientClaim, client),
                    new Claim(ClaimsHelper.DeviceIdClaim, "device"),
                }, "test")),
            };
            var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor { ControllerName = "MediaInfo", ActionName = "GetPostedPlaybackInfo" }, new ModelStateDictionary());
            var args = new Dictionary<string, object?> { ["itemId"] = Item.Id };
            if (audio.HasValue) args["audioStreamIndex"] = audio.Value;
            if (subtitle.HasValue) args["subtitleStreamIndex"] = subtitle.Value;
            var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), args, new object());
            await _newFilter().OnActionExecutionAsync(context, () => outcome switch
            {
                "failed" => Task.FromException<ActionExecutedContext>(new IOException("Synthetic playback request failure")),
                "cancelled" => Task.FromCanceled<ActionExecutedContext>(new CancellationToken(true)),
                _ => Task.FromResult(new ActionExecutedContext(action, new List<IFilterMetadata>(), new object())),
            });
            return context;
        }
    }
}