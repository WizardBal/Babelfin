using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Web;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public sealed class PlaybackSafetyTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Jellyfin Web", true)]
    [InlineData("jellyfin media player", true)]
    [InlineData(" Jellyfin Desktop ", true)]
    [InlineData("Jellyfin Android", true)]
    [InlineData("Android TV", false)]
    [InlineData("Streamyfin", false)]
    [InlineData("Swiftfin", false)]
    [InlineData("Unknown", false)]
    [InlineData(null, false)]
    public void ShippedPreplayBehaviorIsLimitedToWebFamily(string? client, bool expected)
    {
        var config = new PluginConfiguration();
        Assert.Equal(expected, config.RemembersPrePlayChoices(client));
    }

    [Fact]
    public void AdminCanOverrideOrRestoreShippedPreplayBehavior()
    {
        var config = new PluginConfiguration();
        var row = config.FindAppMode("Jellyfin Web")!;
        row.RememberPrePlayChoices = false;
        Assert.False(config.RemembersPrePlayChoices(row.Client));
        row.RememberPrePlayChoices = null;
        Assert.True(config.RemembersPrePlayChoices(row.Client));
        config.AppModes.Add(new AppMode { Client = "Other", RememberPrePlayChoices = true });
        Assert.True(config.RemembersPrePlayChoices("Other"));
    }

    [Theory]
    [InlineData(false, false, PlaybackInfoOutcome.Replace)]
    [InlineData(true, false, PlaybackInfoOutcome.Manual)]
    [InlineData(false, true, PlaybackInfoOutcome.Manual)]
    public void NonwebPreplayPicksAreNotRememberedButInPlayChangesAre(bool remember, bool repeat, PlaybackInfoOutcome expected)
    {
        var facts = new PlaybackInfoFacts(true, repeat, true, true, true, AppModeKind.ReplaceUnlessManual, false, true, remember);
        Assert.Equal(expected, PlaybackInfoFilter.Decide(facts));
    }

    [Theory]
    [InlineData(true, true, PlaybackInfoOutcome.Manual)]
    [InlineData(true, false, PlaybackInfoOutcome.Replace)]
    [InlineData(false, true, PlaybackInfoOutcome.Replace)]
    public void FreshPreplayChoiceWinsInsideAutoplayWindow(bool remember, bool fresh, PlaybackInfoOutcome expected)
    {
        var facts = new PlaybackInfoFacts(true, false, false, true, true, AppModeKind.ReplaceOnAutoplay, true, true, remember, fresh);
        Assert.Equal(expected, PlaybackInfoFilter.Decide(facts));
    }

    [Fact]
    public void FreshServedChangeIsRememberedEvenWhenCurrentDecisionAlreadyMatches()
    {
        var facts = new PlaybackInfoFacts(true, false, false, true, false, AppModeKind.ReplaceOnAutoplay, true, false, true, true);
        Assert.Equal(PlaybackInfoOutcome.Manual, PlaybackInfoFilter.Decide(facts));
        Assert.Equal(PlaybackInfoOutcome.Manual, PlaybackInfoFilter.Decide(facts with { AutoplayWindowHit = false, DiffersFromChoice = true, FreshServedChoice = false }));
    }

    [Theory]
    [InlineData(-1, true, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, true)]
    [InlineData(-1, false, true)]
    public void OnlyServedPickersAfterTheStopOverrideAutoplay(int servedOffsetSeconds, bool autoplay, bool expected)
    {
        var entry = new PlayRecord { LastStoppedAt = T0 };
        var served = Record(T0.AddSeconds(servedOffsetSeconds));
        Assert.Equal(expected, PlaybackInfoFilter.IsFreshPrePlayChoice(served, entry, autoplay));
        Assert.False(PlaybackInfoFilter.IsFreshPrePlayChoice(null, entry, autoplay));
    }

    [Fact]
    public void FirstIndexedReportAfterSilentStartIsOnlyTheBaseline()
    {
        var play = new PlayRecord { HasExpected = true, ExpectedAudio = 2, ExpectedSubtitle = 4, ChoiceAudio = 2, ChoiceSubtitle = 4 };
        PlaybackMonitor.Evaluate(play, null, null, T0, CorrectAfterStartMode.Off, 5, false, null);
        var baseline = PlaybackMonitor.Evaluate(play, 1, -1, T0.AddSeconds(30), CorrectAfterStartMode.Off, 5, false, null);
        Assert.False(baseline.Manual);
        Assert.True(play.HasReport);
        Assert.False(play.ManualChanged);
        Assert.Equal((1, -1), (play.LastReportedAudio, play.LastReportedSubtitle));
        var changed = PlaybackMonitor.Evaluate(play, 3, -1, T0.AddSeconds(40), CorrectAfterStartMode.Off, 5, false, null);
        Assert.True(changed.Manual);
        Assert.Equal(1, changed.PreviousAudio);
    }

    [Fact]
    public void FirstIndexedReportCanStillBeCorrectedWithoutRememberingIt()
    {
        var play = new PlayRecord { HasExpected = true, ExpectedAudio = 2, ChoiceAudio = 2 };
        PlaybackMonitor.Evaluate(play, null, null, T0, CorrectAfterStartMode.On, 5, false, null);
        var baseline = PlaybackMonitor.Evaluate(play, 1, null, T0.AddSeconds(30), CorrectAfterStartMode.On, 5, false, new[] { GeneralCommandType.SetAudioStreamIndex });
        Assert.False(baseline.Manual);
        Assert.Equal(2, baseline.SendAudio);
    }

    [Fact]
    public void DiscoveryCapsRowsAndEvictsOldestSeenOnly()
    {
        var config = new PluginConfiguration { AppModes = new List<AppMode> { new() { Client = "admin", Seen = true, LastSeen = T0.AddYears(-2) } } };
        for (var i = 0; i < 60; i++)
        {
            Assert.True(config.NoteAppSeen("seen" + i, T0.AddSeconds(i)));
        }

        Assert.Equal(51, config.AppModes.Count);
        Assert.NotNull(config.FindAppMode("admin"));
        Assert.Null(config.FindAppMode("seen0"));
        Assert.Null(config.FindAppMode("seen9"));
        Assert.NotNull(config.FindAppMode("seen10"));
        Assert.True(config.FindAppMode("seen59")!.SeenOnly);
    }

    [Fact]
    public void ExplicitRowsAreNeverEvictedAndOverlongNamesAreIgnored()
    {
        var config = new PluginConfiguration { AppModes = Enumerable.Range(0, 100).Select(i => new AppMode { Client = "admin" + i, Seen = true }).ToList() };
        Assert.False(config.NoteAppSeen("new", T0));
        Assert.False(config.NoteAppSeen(new string('x', 101), T0));
        Assert.True(config.NoteAppSeen("admin50", T0));
        Assert.Equal(T0, config.FindAppMode("admin50")!.LastSeen);
        Assert.Equal(100, config.AppModes.Count);
        Assert.All(config.AppModes, row => Assert.False(row.SeenOnly));
    }

    [Fact]
    public void FutureConfigurationDiscoveryIsReadOnly()
    {
        var config = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion + 1 };
        var count = config.AppModes.Count;
        var row = config.FindAppMode("Jellyfin Web")!;
        Assert.False(config.NoteAppSeen("new", T0));
        Assert.False(config.NoteAppSeen(row.Client, T0));
        Assert.Equal(count, config.AppModes.Count);
        Assert.False(row.Seen);
        Assert.Null(row.LastSeen);
    }

    [Fact]
    public void InternalLookupsAndSimulatorDecisionsAreNeverServed()
    {
        var stats = new DecoratorStats(() => T0);
        var user = NewUser();
        var item = new Movie { Id = Guid.NewGuid() };
        var source = Source("a");
        var manager = Manager(stats, source);
        manager.GetStaticMediaSources(item, true, user);
        Assert.NotNull(stats.GetDecision(user.Id, item.Id, source.Id));
        Assert.Null(stats.GetServed(user.Id, item.Id, source.Id, "device", "Jellyfin Web"));
        // A simulator report carries scalar decisions, not returned media sources.
        stats.RecordResponse(user.Id, new { Items = new[] { new { ItemId = item.Id, AudioIndex = 2 } } }, "device", "Jellyfin Web");
        Assert.Null(stats.GetServed(user.Id, item.Id, source.Id, "device", "Jellyfin Web"));
    }

    [Fact]
    public void ServedMapIsScopedExpiresAndSurvivesInternalLookups()
    {
        var now = T0;
        var stats = new DecoratorStats(() => now);
        var user = NewUser();
        var item = Guid.NewGuid();
        var source = Source("VersionA");
        stats.Record(user.Id, item, source.Id, Record(T0), source);
        stats.RecordResponse(Guid.NewGuid(), source, "device", "Jellyfin Web");
        Assert.Null(stats.GetServed(user.Id, item, source.Id, "device", "Jellyfin Web"));
        stats.RecordResponse(user.Id, new { Items = new[] { new BaseItemDto { MediaSources = new[] { source } } } }, "device", "Jellyfin Web");
        Assert.NotNull(stats.GetServed(user.Id, item, "versiona", "device", "Jellyfin Web"));
        Assert.Null(stats.GetServed(user.Id, Guid.NewGuid(), source.Id, "device", "Jellyfin Web"));
        Assert.Null(stats.GetServed(Guid.NewGuid(), item, source.Id, "device", "Jellyfin Web"));
        Assert.Null(stats.GetServed(user.Id, item, "versionb", "device", "Jellyfin Web"));
        now = T0.AddMinutes(10);
        stats.Record(user.Id, item, source.Id, Record(now));
        Assert.Equal(T0, stats.GetServed(user.Id, item, source.Id, "device", "Jellyfin Web")!.At);
        now = T0.AddMinutes(30);
        Assert.Null(stats.GetServed(user.Id, item, source.Id, "device", "Jellyfin Web"));
    }

    [Fact]
    public async Task GlobalResultFilterMarksOnlySuccessfulReturnedSources()
    {
        var stats = new DecoratorStats(() => T0);
        var user = NewUser();
        var item = Guid.NewGuid();
        var source = Source("a");
        stats.Record(user.Id, item, source.Id, Record(T0), source);
        using var services = new ServiceCollection().AddSingleton(stats).BuildServiceProvider();
        var action = ActionContext(services, user.Id);
        var result = new ObjectResult(new { MediaSources = new[] { source } });
        var context = new ResultExecutingContext(action, new List<IFilterMetadata>(), result, new object());
        var filter = new GuardedPlaybackInfoFilter();
        action.HttpContext.Response.StatusCode = 500;
        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(action, new List<IFilterMetadata>(), result, new object())));
        Assert.Null(stats.GetServed(user.Id, item, source.Id, "device", "Jellyfin Web"));
        action.HttpContext.Response.StatusCode = 200;
        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(action, new List<IFilterMetadata>(), result, new object())));
        Assert.NotNull(stats.GetServed(user.Id, item, source.Id, "device", "Jellyfin Web"));
    }

    [Fact]
    public void PausedReplaceUnlessManualRemainsARepeat()
    {
        var item = Guid.NewGuid();
        var play = new PlayRecord { Playing = true, ItemId = item, StartedAt = T0, LastProgressAt = T0, HasExpected = true, ExpectedAudio = 2 };
        Assert.True(PlaybackInfoFilter.IsRepeat(play, item, AppModeKind.ReplaceUnlessManual, T0.AddHours(1)));
        Assert.False(PlaybackInfoFilter.IsRepeat(play, Guid.NewGuid(), AppModeKind.ReplaceUnlessManual, T0.AddHours(1)));
        Assert.False(PlaybackInfoFilter.IsRepeat(play, item, AppModeKind.ReplaceOnAutoplay, T0.AddHours(1)));
        Assert.Equal(PlaybackInfoOutcome.Manual, PlaybackInfoFilter.Decide(new PlaybackInfoFacts(true, true, true, false, true, AppModeKind.ReplaceUnlessManual, false, true, false)));
    }

    [Fact]
    public async Task LiveTvTypesAndActiveRecordingSourcesPassThrough()
    {
        var stats = new DecoratorStats();
        var calls = 0;
        var decider = CallProxy.Create<ITrackDecider>((_, _) => { calls++; return new TrackDecision(2, 4, Array.Empty<string>()); }).Proxy;
        var source = Source("tv");
        var inner = CallProxy.Create<IMediaSourceManager>((method, _) => method.Name == nameof(IMediaSourceManager.GetRecordingStreamMediaSources)
            ? Task.FromResult<IReadOnlyList<MediaSourceInfo>>(new[] { source }) : null).Proxy;
        var manager = new BabelfinMediaSourceManager(inner, decider, stats, NullLogger.Instance);
        foreach (var item in new BaseItem[] { new LiveTvChannel(), new LiveTvProgram() })
        {
            manager.SetDefaultAudioAndSubtitleStreamIndices(item, source, NewUser());
            Assert.False(BabelfinMediaSourceManager.CanChooseTracks(item));
        }

        var recording = await manager.GetRecordingStreamMediaSources(new ActiveRecordingInfo(), CancellationToken.None);
        Assert.Same(source, Assert.Single(recording));
        Assert.Equal(0, calls);
        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.True(BabelfinMediaSourceManager.CanChooseTracks(new Movie()));
    }

    [Fact]
    public void FailedFirstResolutionRetriesWithBackoffAndKeepsSuccessfulResolution()
    {
        var now = T0;
        var attempts = 0;
        var decider = CallProxy.Create<ITrackDecider>().Proxy;
        var services = CallProxy.Create<IServiceProvider>((_, _) => ++attempts <= 2 ? throw new InvalidOperationException("startup unavailable") : decider).Proxy;
        var lazy = new LazyTrackDecider(services, () => now);
        Assert.False(lazy.IsResolved);
        Assert.Equal(0, attempts);
        Assert.Throws<InvalidOperationException>(() => lazy.Decide(new Movie(), Source("a"), NewUser()));
        Assert.Null(lazy.Decide(new Movie(), Source("a"), NewUser()));
        Assert.Equal(1, attempts);
        now = T0.AddSeconds(1);
        Assert.Throws<InvalidOperationException>(() => lazy.Decide(new Movie(), Source("a"), NewUser()));
        now = T0.AddSeconds(2);
        Assert.Null(lazy.Decide(new Movie(), Source("a"), NewUser()));
        Assert.Equal(2, attempts);
        now = T0.AddSeconds(3);
        lazy.Decide(new Movie(), Source("a"), NewUser());
        lazy.Decide(new Movie(), Source("a"), NewUser());
        Assert.True(lazy.IsResolved);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ConcurrentFirstCallsResolveOnce()
    {
        var attempts = 0;
        var decider = CallProxy.Create<ITrackDecider>().Proxy;
        var services = CallProxy.Create<IServiceProvider>((_, _) => { Interlocked.Increment(ref attempts); return decider; }).Proxy;
        var lazy = new LazyTrackDecider(services);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => lazy.Decide(new Movie(), Source("a"), NewUser()))));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DependencyFailurePassesThroughAndUnrelatedActionsResolveNothing()
    {
        var resolutions = 0;
        var provider = CallProxy.Create<IServiceProvider>((_, _) => { resolutions++; throw new InvalidOperationException("dependency unavailable"); }).Proxy;
        var action = ActionContext(provider, Guid.NewGuid());
        var descriptor = (ControllerActionDescriptor)action.ActionDescriptor;
        descriptor.ControllerName = "MediaInfo";
        descriptor.ActionName = "OtherAction";
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        var calls = 0;
        Task<ActionExecutedContext> Next() { calls++; return Task.FromResult(new ActionExecutedContext(action, new List<IFilterMetadata>(), new object())); }
        var filter = new GuardedPlaybackInfoFilter();
        await filter.OnActionExecutionAsync(context, Next);
        Assert.Equal(0, resolutions);
        descriptor.ActionName = "GetPostedPlaybackInfo";
        await filter.OnActionExecutionAsync(context, Next);
        Assert.True(resolutions > 0);
        Assert.Equal(2, calls);
        Assert.Empty(context.ActionArguments);
    }

    [Fact]
    public void WrapperFactoryDoesNotResolveTheDecider()
    {
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CallProxy.Create<IMediaSourceManager>((method, _) => method.Name == nameof(IMediaSourceManager.GetStaticMediaSources) ? new[] { Source("a") } : null).Proxy);
        services.AddSingleton<ITrackDecider>(_ => { resolutions++; throw new InvalidOperationException("not ready at startup"); });
        var wrap = typeof(ServiceRegistrator).GetMethod("WrapMediaSourceManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True((bool)wrap.Invoke(null, new object[] { services, new DecoratorStats() })!);
        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IMediaSourceManager>();
        Assert.IsType<BabelfinMediaSourceManager>(manager);
        Assert.Equal(0, resolutions);
        var returned = manager.GetStaticMediaSources(new Movie(), true, NewUser());
        Assert.Equal(1, Assert.Single(returned).DefaultAudioStreamIndex);
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public void PendingPlaybackInfoWithoutAStartIsNotAnInPlayRepeat()
    {
        var item = Guid.NewGuid();
        var pending = new PlayRecord { Playing = true, ItemId = item, HasExpected = true, ExpectedAudio = 2 };
        Assert.False(PlaybackInfoFilter.IsRepeat(pending, item, AppModeKind.ReplaceUnlessManual, T0));
    }

    [Fact]
    public void AdoptionRejectsAnotherUserAndLeavesTheirPendingEntryIntact()
    {
        var registry = new PlayRegistry(() => T0);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var item = Guid.NewGuid();
        registry.UpdatePending("device", "client", item, r => { r.UserId = owner; r.ExpectedAudio = 2; r.ManualChanged = true; });
        Assert.Null(registry.Adopt("other-session", "device", "client", item, other));
        Assert.Null(registry.Get("other-session"));
        Assert.NotNull(registry.GetPending("device", "client", item));
        registry.Update("owner-session", r => { r.UserId = owner; r.LastStoppedAt = T0; });
        var adopted = registry.Adopt("owner-session", "device", "client", item, owner);
        Assert.NotNull(adopted);
        Assert.Equal(owner, adopted!.UserId);
        Assert.Equal(2, adopted.ExpectedAudio);
        Assert.True(adopted.ManualChanged);
        Assert.Equal(T0, adopted.LastStoppedAt);
        Assert.Null(registry.GetPending("device", "client", item));
    }

    [Fact]
    public void LegacyAdoptionChecksExistingSessionUser()
    {
        var registry = new PlayRegistry(() => T0);
        var item = Guid.NewGuid();
        registry.UpdatePending("device", "client", item, r => r.UserId = Guid.NewGuid());
        registry.Update("s", r => r.UserId = Guid.NewGuid());
        Assert.Null(registry.Adopt("s", "device", "client", item));
        Assert.NotNull(registry.GetPending("device", "client", item));
    }

    private static ActionContext ActionContext(IServiceProvider services, Guid userId)
    {
        var http = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, userId.ToString()), new Claim(ClaimsHelper.DeviceIdClaim, "device"), new Claim(ClaimsHelper.ClientClaim, "Jellyfin Web") }, "test")) };
        return new ActionContext(http, new RouteData(), new ControllerActionDescriptor(), new ModelStateDictionary());
    }

    private static User NewUser() => new("Test", "Default", "Default") { Id = Guid.NewGuid() };
    private static MediaSourceInfo Source(string id) => new() { Id = id, DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = -1 };
    private static DecisionRecord Record(DateTime at) => new(at, 1, -1, new TrackDecision(2, 4, Array.Empty<string>()), DecisionOutcome.Applied);

    private static BabelfinMediaSourceManager Manager(DecoratorStats stats, MediaSourceInfo source)
    {
        var inner = CallProxy.Create<IMediaSourceManager>((method, _) => method.Name == nameof(IMediaSourceManager.GetStaticMediaSources) ? new[] { source } : null).Proxy;
        var decider = CallProxy.Create<ITrackDecider>((_, _) => new TrackDecision(2, 4, Array.Empty<string>())).Proxy;
        return new BabelfinMediaSourceManager(inner, decider, stats, NullLogger.Instance);
    }
}
