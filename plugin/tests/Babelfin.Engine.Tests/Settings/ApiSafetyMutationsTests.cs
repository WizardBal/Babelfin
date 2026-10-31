using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Compat;
using Jellyfin.Plugin.Babelfin.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public class ApiSafetyMutationsTests
{
    private static string Json(object? value) => JsonSerializer.Serialize(value, BabelfinJson.Options);
    private static PersonSettings Document() => new()
    {
        UserId = Ids.Person, UserName = "Alice",
        Overrides = new List<ItemOverride> { new() { ItemId = Ids.N(Ids.Item), Settings = new SettingsLayer { Format = FormatPreference.TextOnly } } },
        BurnedIn = new List<BurnedInFlag> { new() { ItemId = Ids.N(Ids.Item), Languages = new List<string> { "en" } } },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BurnedInAddAndRemove_PreserveLatestOverrideRemovalAndRememberedChange(bool add)
    {
        var store = new ApiSafetyStore();
        store.Inner.Save(Document());
        store.BeforeUpdate = () => store.Inner.Update(Ids.Person, current =>
        {
            var latest = current!.Clone();
            latest.Enabled = false;
            latest.Overrides.Clear();
            latest.Overrides.Add(new ItemOverride { ItemId = Ids.N(Ids.Series), Scope = OverrideScope.Series, Manual = new ManualMemory { SubtitlesOff = true } });
            return latest;
        });
        var controller = ApiSafetyHost.Me(store, ApiSafetyHost.Person());
        if (add)
        {
            Assert.IsType<OkObjectResult>(controller.PostBurnedIn(Ids.Item, new BurnedInRequest { Languages = new List<string> { "fr" }, Scope = OverrideScope.Title }, null).Result);
        }
        else
        {
            Assert.IsType<NoContentResult>(controller.DeleteBurnedIn(Ids.Item, null, null));
        }
        var saved = store.TryGet(Ids.Person)!;
        Assert.False(saved.Enabled);
        Assert.Null(saved.FindOverride(Ids.Item));
        Assert.True(Assert.Single(saved.Overrides).Manual!.SubtitlesOff);
        if (add) { Assert.Equal(new[] { "fr" }, Assert.Single(saved.BurnedIn).Languages); }
        else { Assert.Empty(saved.BurnedIn); }
        Assert.Equal(1, store.Updates);
    }

    [Fact]
    public void BurnedInCapUsesTheDocumentInsideTheLock()
    {
        var store = new ApiSafetyStore();
        store.Inner.Save(new PersonSettings { UserId = Ids.Person });
        store.BeforeUpdate = () => store.Inner.Update(Ids.Person, current =>
        {
            var copy = current!.Clone();
            copy.BurnedIn = Enumerable.Range(0, SettingsValidator.MaxBurnedIn).Select(_ => new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N") }).ToList();
            return copy;
        });
        var result = ApiSafetyHost.Me(store, ApiSafetyHost.Person()).PostBurnedIn(Ids.Item, new BurnedInRequest { Languages = new List<string> { "en" }, Scope = OverrideScope.Title }, null);
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(SettingsValidator.MaxBurnedIn, store.TryGet(Ids.Person)!.BurnedIn.Count);
    }

    [Fact]
    public async Task MeResetPreservesLatestLocksAndLocalUndoRecord()
    {
        var store = new ApiSafetyStore();
        store.Inner.Save(Document());
        var changes = new JellyfinChanges { RememberAudioSelections = true, RememberSubtitleSelections = false, ChangedAt = DateTime.UtcNow };
        store.BeforeUpdate = () => store.Inner.Update(Ids.Person, current =>
        {
            var latest = current!.Clone();
            latest.Locks = new SettingsLayer { AvoidCommentary = false };
            latest.JellyfinChanges = changes;
            return latest;
        });
        var flags = new ApiSafetyFlags();
        var response = await ApiSafetyHost.Me(store, ApiSafetyHost.Person(), flags).Reset();
        Assert.IsType<OkObjectResult>(response.Result);
        var saved = store.TryGet(Ids.Person)!;
        Assert.False(saved.Locks.AvoidCommentary);
        Assert.Equal(Json(changes), Json(saved.JellyfinChanges));
        // The fresh document keeps the next-episode switch at its default, on: the flags stay off and the record stays.
        Assert.True(saved.HandleNextEpisode);
        Assert.Empty(flags.Restored);
        Assert.Empty(flags.Applied);
        Assert.Empty(saved.Overrides);
    }

    [Fact]
    public async Task MeResetTurnsTheNextEpisodeSwitchBackOnAndAppliesIt()
    {
        var store = new ApiSafetyStore();
        var document = Document();
        document.HandleNextEpisode = false;
        store.Inner.Save(document);
        var flags = new ApiSafetyFlags();
        var response = await ApiSafetyHost.Me(store, ApiSafetyHost.Person(), flags).Reset();
        Assert.IsType<OkObjectResult>(response.Result);
        Assert.True(store.TryGet(Ids.Person)!.HandleNextEpisode);
        Assert.Equal(new[] { Ids.Person }, flags.Applied);
        Assert.Empty(flags.Restored);
    }

    [Fact]
    public async Task AdminCopyAfterRetryPreservesLatestTargetSwitchesFlagsSeedAndMemory()
    {
        var store = new ApiSafetyStore();
        var source = ApiSafetyHost.Person("Source", Ids.Series);
        var target = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = source.Id, Everyday = new SettingsLayer { Reads = new[] { "fr" } }, PerLibrary = true });
        store.Inner.Save(new PersonSettings { UserId = target.Id });
        var changes = new JellyfinChanges { RememberAudioSelections = true };
        store.BeforeUpdate = () => store.Inner.Update(target.Id, current =>
        {
            var latest = current!.Clone();
            latest.Enabled = false;
            latest.HandleNextEpisode = true;
            latest.JellyfinChanges = changes;
            latest.BurnedIn = Document().BurnedIn;
            latest.Locks = new SettingsLayer { AvoidCommentary = false };
            latest.Seed = new SeedRecord { Confirmed = true };
            latest.Overrides.Add(new ItemOverride { ItemId = Ids.N(Ids.Item), Manual = new ManualMemory { SubtitlesOff = true } });
            return latest;
        });
        var controller = ApiSafetyHost.Admin(store, null, source, target);
        var conflict = Assert.IsAssignableFrom<ObjectResult>(await Babelfin.Engine.Tests.Regression.StorageApiSafetyTests.InvokeApi(controller,
            () => Task.FromResult<IActionResult?>(controller.CopyTo(source.Id, target.Id).Result)));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Null(store.TryGet(target.Id)!.Everyday.Reads);
        var response = controller.CopyTo(source.Id, target.Id);
        Assert.IsType<OkObjectResult>(response.Result);
        var saved = store.TryGet(target.Id)!;
        Assert.False(saved.Enabled);
        Assert.True(saved.HandleNextEpisode);
        Assert.Equal(Json(changes), Json(saved.JellyfinChanges));
        Assert.Single(saved.BurnedIn);
        Assert.False(saved.Locks.AvoidCommentary);
        Assert.True(saved.Seed!.Confirmed);
        Assert.True(saved.Overrides[0].Manual!.SubtitlesOff);
        Assert.True(saved.PerLibrary);
        Assert.Equal(new[] { "fr" }, saved.Everyday.Reads);
    }

    [Theory]
    [InlineData(true, false, true, false, 1, 0)]
    [InlineData(false, false, true, false, 1, 0)]
    [InlineData(false, true, false, true, 0, 1)]
    [InlineData(false, false, false, false, 0, 0)]
    public async Task DisablingRestoresFlags_ReenablingAppliesRetainedNextEpisodeChoice(bool enabledBefore, bool enabledAfter, bool recorded, bool expectApply, int restores, int applies)
    {
        var flags = new ApiSafetyFlags();
        var before = new PersonSettings { Enabled = enabledBefore, HandleNextEpisode = true, JellyfinChanges = recorded ? new JellyfinChanges() : null };
        var after = before.Clone();
        after.Enabled = enabledAfter;
        await PersonDocuments.SyncRememberFlagsAsync(flags, ApiSafetyHost.Person(), before, after);
        Assert.Equal(restores, flags.Restored.Count);
        Assert.Equal(applies, flags.Applied.Count);
        Assert.Equal(expectApply, flags.Applied.Count > 0);
        Assert.True(after.HandleNextEpisode);
    }

    [Fact]
    public async Task AdminEditorDisableRestoresWithoutClearingNextEpisodePreference()
    {
        var store = new ApiSafetyStore();
        store.Inner.Save(new PersonSettings { UserId = Ids.Person, HandleNextEpisode = true, JellyfinChanges = new JellyfinChanges { RememberAudioSelections = true } });
        var body = store.TryGet(Ids.Person)!.Clone();
        body.Enabled = false;
        var flags = new ApiSafetyFlags();
        var response = await ApiSafetyHost.Admin(store, flags, ApiSafetyHost.Person()).PutUserSettings(Ids.Person, body, true);
        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(new[] { Ids.Person }, flags.Restored);
        Assert.Empty(flags.Applied);
        Assert.True(store.TryGet(Ids.Person)!.HandleNextEpisode);
    }

    [Theory]
    [InlineData(0, 0, 200)]
    [InlineData(2, 0, 200)]
    [InlineData(2, 1, 200)]
    [InlineData(2, 2, 500)]
    public async Task ResetAllReportsZeroPeoplePartialAndAllFailedOutcomes(int existing, int failures, int status)
    {
        var store = new ApiSafetyStore();
        var users = Enumerable.Range(0, existing).Select(i => ApiSafetyHost.Person("Person " + i, Guid.NewGuid())).ToArray();
        foreach (var user in users) { store.Inner.Save(new PersonSettings { UserId = user.Id }); }
        // A deleted Jellyfin user still has a stored document and is deliberately skipped.
        var gone = Guid.NewGuid();
        store.Inner.Save(new PersonSettings { UserId = gone });
        foreach (var user in users.Take(failures)) { store.Failing.Add(user.Id); }
        var admin = ApiSafetyHost.Admin(store, null, users);
        var report = await admin.ResetPeopleAsync("test-backup.json");
        Assert.Equal(existing - failures, report.People);
        Assert.Equal(users.Take(failures).Select(u => u.Username).OrderBy(n => n), report.FailedNames.OrderBy(n => n));
        Assert.Equal(status, admin.ResetResult(report).StatusCode);
        if (failures > 0) { Assert.StartsWith("Reset with problems:", report.Message); }
        Assert.NotNull(store.TryGet(gone));
    }

    [Fact]
    public async Task ResetAllPreservesLatestUndoRecordAndKeepsTheNextEpisodeSwitchOn()
    {
        var store = new ApiSafetyStore();
        store.Inner.Save(new PersonSettings { UserId = Ids.Person, HandleNextEpisode = true });
        var latest = new JellyfinChanges { RememberAudioSelections = true, RememberSubtitleSelections = true };
        store.BeforeUpdate = () => store.Inner.Update(Ids.Person, current => { var copy = current!.Clone(); copy.JellyfinChanges = latest; return copy; });
        var flags = new ApiSafetyFlags { FailRestore = true };
        var admin = ApiSafetyHost.Admin(store, flags, ApiSafetyHost.Person());
        var report = await admin.ResetPeopleAsync("backup.json");
        Assert.Empty(report.FailedNames);
        Assert.Equal(200, admin.ResetResult(report).StatusCode);
        Assert.Equal(Json(latest), Json(store.TryGet(Ids.Person)!.JellyfinChanges));
        Assert.True(store.TryGet(Ids.Person)!.HandleNextEpisode);
        Assert.Empty(flags.Applied);
        Assert.Empty(flags.Restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetTurnsTheNextEpisodeSwitchOnAndKeepsJellyfinFlagsOff(bool recorded)
    {
        var store = new ApiSafetyStore();
        var user = ApiSafetyHost.Person();
        user.RememberAudioSelections = !recorded;
        user.RememberSubtitleSelections = !recorded;
        store.Inner.Save(new PersonSettings
        {
            UserId = user.Id, HandleNextEpisode = recorded,
            JellyfinChanges = recorded ? new JellyfinChanges { RememberAudioSelections = true, RememberSubtitleSelections = true } : null,
        });
        var updates = 0;
        var manager = CallProxy.Create<IUserManager>((method, args) =>
        {
            if (method.Name == nameof(IUserManager.UpdateConfigurationAsync))
            {
                updates++;
                var config = (UserConfiguration)args[1]!;
                user.RememberAudioSelections = config.RememberAudioSelections;
                user.RememberSubtitleSelections = config.RememberSubtitleSelections;
                return Task.CompletedTask;
            }
            return null;
        }).Proxy;
        var flags = new RememberFlags(manager, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        var admin = ApiSafetyHost.Admin(store, flags, user);
        var report = await admin.ResetPeopleAsync("backup.json");
        Assert.Equal(1, report.People);
        Assert.Empty(report.FailedNames);
        Assert.Equal(200, admin.ResetResult(report).StatusCode);
        var saved = store.TryGet(user.Id)!;
        Assert.True(saved.HandleNextEpisode);
        Assert.True(saved.JellyfinChanges is { RememberAudioSelections: true, RememberSubtitleSelections: true });
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);
        Assert.Equal(recorded ? 0 : 1, updates);
    }

    [Theory]
    [InlineData("Jellyfin Web", null, false, true)]
    [InlineData("Jellyfin Web", false, false, false)]
    [InlineData("Custom App", true, false, true)]
    [InlineData("Custom App", null, true, false)]
    public void AppRowsExposeEffectivePrePlayBehaviorAndSeenOnly(string client, bool? remember, bool seenOnly, bool expected)
    {
        var seen = DateTime.UtcNow;
        var config = new PluginConfiguration
        {
            AppModes = new List<AppMode> { new() { Client = client, Mode = AppModeKind.ReplaceUnlessManual, RememberPrePlayChoices = remember, SeenOnly = seenOnly, Seen = true, LastSeen = seen } },
        };
        var row = Assert.Single(AdminController.AppRows(config));
        Assert.Equal(client, row.Client);
        Assert.Equal(AppModeKind.ReplaceUnlessManual, row.Mode);
        Assert.True(row.Seen);
        Assert.Equal(seen, row.LastSeen);
        Assert.Equal(expected, row.RememberPrePlayChoices);
        Assert.Equal(seenOnly, row.SeenOnly);
        Assert.Equal(remember, config.AppModes[0].RememberPrePlayChoices);
    }
}
