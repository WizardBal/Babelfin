using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Compat;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>An activity log that keeps entries in a list.</summary>
internal sealed class ListActivityLog : IActivityLog
{
    public List<ActivityEntry> Entries { get; } = new();

    public void Add(ActivityEntry entry)
    {
        lock (Entries)
        {
            Entries.Add(entry);
        }
    }

    public IReadOnlyList<ActivityEntry> Query(int limit, string? kind = null, Guid? userId = null)
        => Entries.AsEnumerable().Reverse().Where(e => (kind is null || e.Kind == kind) && (userId is null || e.UserId == userId)).Take(limit).ToList();
}

/// <summary>compatibility mode's ledger, the remember flags, and the validator rules for the new admin fields.</summary>
public sealed class CompatTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "babelfin-compat-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static User NewUser(bool rememberAudio = true, bool rememberSubtitles = true) => new("ViewerOne", "Default", "Default")
    {
        Id = Guid.NewGuid(),
        AudioLanguagePreference = "jpn",
        SubtitleLanguagePreference = "eng",
        PlayDefaultAudioTrack = false,
        RememberAudioSelections = rememberAudio,
        RememberSubtitleSelections = rememberSubtitles,
        EnableNextEpisodeAutoPlay = true,
        HidePlayedInLatest = true,
        CastReceiverId = "F007D354",
    };

    // ---- the ledger rules ----

    [Fact]
    public void FirstWrite_RecordsThePreviousValues()
    {
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var plan = CompatLedgerLogic.PlanWrite(null, user, item, currentAudio: 1, currentSubtitle: null, audio: 2, subtitle: 4, T0);

        Assert.True(plan.Write);
        Assert.True(plan.NewEntry);
        Assert.Equal((2, 4), (plan.Audio, plan.Subtitle));
        var entry = plan.Entry!;
        Assert.Equal((1, (int?)null), (entry.PreviousAudio, entry.PreviousSubtitle));
        Assert.Equal((2, 4), (entry.WrittenAudio, entry.WrittenSubtitle));
        Assert.Equal((user, item), (entry.UserId, entry.ItemId));
    }

    [Fact]
    public void LaterWrites_KeepTheFirstPreviousValues()
    {
        var first = CompatLedgerLogic.PlanWrite(null, Guid.NewGuid(), Guid.NewGuid(), 1, 3, 2, 4, T0).Entry!;
        var second = CompatLedgerLogic.PlanWrite(first, first.UserId, first.ItemId, currentAudio: 2, currentSubtitle: 4, audio: 3, subtitle: -1, T0.AddDays(1));

        Assert.True(second.Write);
        Assert.False(second.NewEntry);
        Assert.Equal((1, 3), (second.Entry!.PreviousAudio, second.Entry.PreviousSubtitle));
        Assert.Equal((3, -1), (second.Entry.WrittenAudio, second.Entry.WrittenSubtitle));
        Assert.Equal(T0, second.Entry.FirstWrittenAt);
        Assert.Equal(T0.AddDays(1), second.Entry.WrittenAt);

        // The planner never changes the stored entry in place.
        Assert.Equal((2, 4), (first.WrittenAudio, first.WrittenSubtitle));
    }

    [Fact]
    public void AWriteThatChangesNothing_IsSkipped_AndNotRecorded()
    {
        var plan = CompatLedgerLogic.PlanWrite(null, Guid.NewGuid(), Guid.NewGuid(), 2, 4, 2, 4, T0);
        Assert.False(plan.Write);
        Assert.Null(plan.Entry);
        Assert.False(plan.LedgerChanged);

        var existing = CompatLedgerLogic.PlanWrite(null, Guid.NewGuid(), Guid.NewGuid(), 1, 3, 2, 4, T0).Entry!;
        var again = CompatLedgerLogic.PlanWrite(existing, existing.UserId, existing.ItemId, 2, 4, 2, 4, T0.AddHours(1));
        Assert.False(again.Write);
        Assert.False(again.LedgerChanged);
        Assert.Same(existing, again.Entry);
    }

    [Fact]
    public void ANullIndex_LeavesThatIndexAlone()
    {
        var plan = CompatLedgerLogic.PlanWrite(null, Guid.NewGuid(), Guid.NewGuid(), currentAudio: 1, currentSubtitle: 5, audio: 2, subtitle: null, T0);
        Assert.Equal((2, 5), (plan.Audio, plan.Subtitle));
        Assert.Equal((5, 5), (plan.Entry!.PreviousSubtitle, plan.Entry.WrittenSubtitle));
    }

    [Fact]
    public void Undo_RestoresOnlyWhereTheValueIsStillBabelfins()
    {
        var entry = new CompatEntry { PreviousAudio = 1, PreviousSubtitle = null, WrittenAudio = 2, WrittenSubtitle = 4 };

        var untouched = CompatLedgerLogic.PlanUndo(entry, 2, 4);
        Assert.True(untouched.Write);
        Assert.Equal((1, (int?)null), (untouched.Audio, untouched.Subtitle));

        var personChangedSubtitles = CompatLedgerLogic.PlanUndo(entry, 2, 6);
        Assert.True(personChangedSubtitles.Write);
        Assert.True(personChangedSubtitles.AudioRestored);
        Assert.False(personChangedSubtitles.SubtitleRestored);
        Assert.Equal((1, 6), (personChangedSubtitles.Audio, personChangedSubtitles.Subtitle));

        var personChangedBoth = CompatLedgerLogic.PlanUndo(entry, 3, -1);
        Assert.False(personChangedBoth.Write);
        Assert.Equal((3, -1), (personChangedBoth.Audio, personChangedBoth.Subtitle));
    }

    [Fact]
    public void Undo_OfAFieldBabelfinNeverChanged_IsANoOp()
    {
        var entry = new CompatEntry { PreviousAudio = 1, PreviousSubtitle = 5, WrittenAudio = 2, WrittenSubtitle = 5 };
        var plan = CompatLedgerLogic.PlanUndo(entry, 2, 5);
        Assert.True(plan.AudioRestored);
        Assert.False(plan.SubtitleRestored);
        Assert.Equal((1, 5), (plan.Audio, plan.Subtitle));
    }

    // ---- the writer through fakes ----

    private sealed class FakeJellyfin
    {
        public Dictionary<Guid, UserItemData> Data { get; } = new();

        public Dictionary<Guid, BaseItem> Items { get; } = new();

        public Dictionary<Guid, User> Users { get; } = new();

        public int Saves { get; set; }

        public bool FailSaves { get; set; }

        public IUserDataManager UserData => CallProxy.Create<IUserDataManager>((m, a) =>
        {
            if (m.Name == nameof(IUserDataManager.GetUserData))
            {
                var item = (BaseItem)a[1]!;
                return Data.TryGetValue(item.Id, out var d) ? d : Data[item.Id] = new UserItemData { Key = item.Id.ToString("N") };
            }

            if (m.Name == nameof(IUserDataManager.SaveUserData) && a[2] is UserItemData saved)
            {
                if (FailSaves)
                {
                    throw new InvalidOperationException("database is locked");
                }

                Saves++;
                Data[((BaseItem)a[1]!).Id] = saved;
            }

            return null;
        }).Proxy;

        public IUserManager UserManager => CallProxy.Create<IUserManager>((m, a) =>
            m.Name == nameof(IUserManager.GetUserById) && Users.TryGetValue((Guid)a[0]!, out var u) ? u : null).Proxy;

        public ILibraryManager Library => CallProxy.Create<ILibraryManager>((m, a) =>
            m.Name == nameof(ILibraryManager.GetItemById) && a.Length == 1 && a[0] is Guid id && Items.TryGetValue(id, out var i) ? i : null).Proxy;
    }

    private (CompatibilityWriter Writer, FakeJellyfin Jellyfin, ListActivityLog Activity) CreateWriter()
    {
        var jellyfin = new FakeJellyfin();
        var activity = new ListActivityLog();
        var writer = new CompatibilityWriter(_folder, jellyfin.UserData, jellyfin.UserManager, jellyfin.Library, activity, NullLogger<CompatibilityWriter>.Instance);
        return (writer, jellyfin, activity);
    }

    private static Episode NewEpisode(string name = "Episode 1") => new() { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public async Task Writer_RecordsOnce_WritesJellyfin_AndUndoRestores()
    {
        var (writer, jellyfin, activity) = CreateWriter();
        var user = NewUser();
        var episode = NewEpisode();
        jellyfin.Users[user.Id] = user;
        jellyfin.Items[episode.Id] = episode;
        jellyfin.Data[episode.Id] = new UserItemData { Key = "k", AudioStreamIndex = 1, SubtitleStreamIndex = null };

        await writer.WriteAsync(user, episode, 2, 4, CancellationToken.None);
        await writer.WriteAsync(user, episode, 3, 4, CancellationToken.None);

        Assert.Equal((3, 4), (jellyfin.Data[episode.Id].AudioStreamIndex, jellyfin.Data[episode.Id].SubtitleStreamIndex));
        Assert.Equal(1, writer.LedgerCount);
        Assert.True(File.Exists(writer.FilePath));
        var logged = Assert.Single(activity.Entries);
        Assert.Equal(ActivityKinds.JellyfinSettingChanged, logged.Kind);
        Assert.Equal("false", logged.Details!["mirror"]);

        // A new writer reads the ledger from the file: the first "previous" is what comes back.
        var reloaded = new CompatibilityWriter(_folder, jellyfin.UserData, jellyfin.UserManager, jellyfin.Library, activity, NullLogger<CompatibilityWriter>.Instance);
        await reloaded.UndoAllAsync(CancellationToken.None);

        Assert.Equal((1, (int?)null), (jellyfin.Data[episode.Id].AudioStreamIndex, jellyfin.Data[episode.Id].SubtitleStreamIndex));
        Assert.Equal(0, reloaded.LedgerCount);
        Assert.True(File.Exists(reloaded.FilePath));
        Assert.Equal(ActivityKinds.Restored, activity.Entries[^1].Kind);
    }

    [Fact]
    public async Task Undo_LeavesAPersonsLaterChoiceAlone_AndDropsGoneItems()
    {
        var (writer, jellyfin, activity) = CreateWriter();
        var user = NewUser();
        var kept = NewEpisode("kept");
        var gone = NewEpisode("gone");
        jellyfin.Users[user.Id] = user;
        jellyfin.Items[kept.Id] = kept;
        jellyfin.Items[gone.Id] = gone;

        await writer.WriteAsync(user, kept, 2, 4, CancellationToken.None);
        await writer.WriteAsync(user, gone, 2, 4, CancellationToken.None);
        jellyfin.Data[kept.Id].AudioStreamIndex = 5;
        jellyfin.Data[kept.Id].SubtitleStreamIndex = 6;
        jellyfin.Items.Remove(gone.Id);
        var saves = jellyfin.Saves;

        await writer.UndoAllAsync(CancellationToken.None);

        Assert.Equal((5, 6), (jellyfin.Data[kept.Id].AudioStreamIndex, jellyfin.Data[kept.Id].SubtitleStreamIndex));
        Assert.Equal(saves, jellyfin.Saves);
        Assert.Equal(0, writer.LedgerCount);
        Assert.Equal("1", activity.Entries[^1].Details!["leftAlone"]);
        Assert.Equal("1", activity.Entries[^1].Details!["gone"]);
    }

    [Fact]
    public async Task Undo_KeepsEntriesWhoseRestoreFailed()
    {
        var (writer, jellyfin, _) = CreateWriter();
        var user = NewUser();
        var episode = NewEpisode();
        jellyfin.Users[user.Id] = user;
        jellyfin.Items[episode.Id] = episode;
        await writer.WriteAsync(user, episode, 2, 4, CancellationToken.None);

        jellyfin.FailSaves = true;
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal(1, writer.LedgerCount);

        jellyfin.FailSaves = false;
        await writer.UndoAllAsync(CancellationToken.None);
        Assert.Equal(0, writer.LedgerCount);
        Assert.Null(jellyfin.Data[episode.Id].AudioStreamIndex);
    }

    [Fact]
    public async Task Writer_NeverThrows_AndSkipsUnchangedValues()
    {
        var (writer, jellyfin, activity) = CreateWriter();
        var user = NewUser();
        var episode = NewEpisode();
        jellyfin.Data[episode.Id] = new UserItemData { Key = "k", AudioStreamIndex = 2, SubtitleStreamIndex = 4 };

        await writer.WriteAsync(user, episode, 2, 4, CancellationToken.None);
        Assert.Equal(0, writer.LedgerCount);
        Assert.Equal(0, jellyfin.Saves);
        Assert.Empty(activity.Entries);

        jellyfin.FailSaves = true;
        await writer.WriteAsync(user, episode, 1, 4, CancellationToken.None);
        await writer.WriteAsync(null!, episode, 1, 4, CancellationToken.None);
    }

    // ---- the remember flags through fakes ----

    private sealed class FlagsHost
    {
        public FlagsHost()
        {
            UserManager = CallProxy.Create<IUserManager>((m, a) =>
            {
                if (m.Name == nameof(IUserManager.GetUserById))
                {
                    return Users.TryGetValue((Guid)a[0]!, out var u) ? u : null;
                }

                if (m.Name == nameof(IUserManager.UpdateConfigurationAsync))
                {
                    if (Fail)
                    {
                        return Task.FromException(new InvalidOperationException("database is locked"));
                    }

                    var config = (UserConfiguration)a[1]!;
                    Updates.Add(((Guid)a[0]!, config));
                    if (Users.TryGetValue((Guid)a[0]!, out var user))
                    {
                        user.RememberAudioSelections = config.RememberAudioSelections;
                        user.RememberSubtitleSelections = config.RememberSubtitleSelections;
                    }
                }

                return null;
            }).Proxy;
            Flags = new RememberFlags(UserManager, Store, Activity, NullLogger<RememberFlags>.Instance);
        }

        public Dictionary<Guid, User> Users { get; } = new();

        public List<(Guid UserId, UserConfiguration Config)> Updates { get; } = new();

        public bool Fail { get; set; }

        public FakePersonStore Store { get; } = new();

        public ListActivityLog Activity { get; } = new();

        public IUserManager UserManager { get; }

        public RememberFlags Flags { get; }

        public User Add(bool audio, bool subtitles, bool handle = true)
        {
            var user = NewUser(audio, subtitles);
            Users[user.Id] = user;
            Store.Save(new PersonSettings { UserId = user.Id, UserName = user.Username, HandleNextEpisode = handle });
            return user;
        }
    }

    [Fact]
    public async Task ApplyAfterRestoreAllCannotReapplyAStalePreference()
    {
        var host = new FlagsHost();
        var user = host.Add(audio: true, subtitles: true);
        await host.Flags.ApplyAsync(user, CancellationToken.None);
        await host.Flags.RestoreAllAsync(CancellationToken.None);
        host.Updates.Clear();
        await host.Flags.ApplyAsync(user, CancellationToken.None);
        Assert.True(user.RememberAudioSelections && user.RememberSubtitleSelections);
        Assert.Null(host.Store.TryGet(user.Id)!.JellyfinChanges);
        Assert.Empty(host.Updates);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ApplyUsesTheLatestPersonalSwitches(bool enabled, bool handle)
    {
        var host = new FlagsHost();
        var user = host.Add(audio: true, subtitles: true, handle: handle);
        var doc = host.Store.TryGet(user.Id)!.Clone();
        doc.Enabled = enabled;
        host.Store.Save(doc);
        await host.Flags.ApplyAsync(user, CancellationToken.None);
        Assert.True(user.RememberAudioSelections && user.RememberSubtitleSelections);
        Assert.Null(host.Store.TryGet(user.Id)!.JellyfinChanges);
        Assert.Empty(host.Updates);
    }

    [Fact]
    public async Task Apply_StoresTheOldFlags_ThenTurnsThemOff()
    {
        var host = new FlagsHost();
        var user = host.Add(audio: true, subtitles: false);

        await host.Flags.ApplyAsync(user, CancellationToken.None);

        var changes = host.Store.TryGet(user.Id)!.JellyfinChanges!;
        Assert.True(changes.RememberAudioSelections);
        Assert.False(changes.RememberSubtitleSelections);
        var update = Assert.Single(host.Updates);
        Assert.False(update.Config.RememberAudioSelections);
        Assert.False(update.Config.RememberSubtitleSelections);
        Assert.Equal("jpn", update.Config.AudioLanguagePreference);
        Assert.True(update.Config.EnableNextEpisodeAutoPlay);
        Assert.Equal(ActivityKinds.JellyfinSettingChanged, Assert.Single(host.Activity.Entries).Kind);

        // Applying again keeps the person's own values, not Babelfin's "off".
        await host.Flags.ApplyAsync(user, CancellationToken.None);
        Assert.True(host.Store.TryGet(user.Id)!.JellyfinChanges!.RememberAudioSelections);
        Assert.Single(host.Updates);
    }

    [Fact]
    public async Task Restore_WritesTheStoredFlagsBack_AndClearsTheRecord()
    {
        var host = new FlagsHost();
        var user = host.Add(audio: true, subtitles: true);
        await host.Flags.ApplyAsync(user, CancellationToken.None);

        await host.Flags.RestoreAsync(user, CancellationToken.None);

        Assert.True(user.RememberAudioSelections);
        Assert.True(user.RememberSubtitleSelections);
        Assert.Null(host.Store.TryGet(user.Id)!.JellyfinChanges);
        Assert.Equal(ActivityKinds.Restored, host.Activity.Entries[^1].Kind);

        // Nothing recorded: nothing to do.
        host.Updates.Clear();
        await host.Flags.RestoreAsync(user, CancellationToken.None);
        Assert.Empty(host.Updates);
    }

    [Fact]
    public async Task Restore_KeepsTheRecord_WhenJellyfinFails()
    {
        var host = new FlagsHost();
        var user = host.Add(audio: true, subtitles: true);
        await host.Flags.ApplyAsync(user, CancellationToken.None);

        host.Fail = true;
        await host.Flags.RestoreAsync(user, CancellationToken.None);
        Assert.NotNull(host.Store.TryGet(user.Id)!.JellyfinChanges);

        host.Fail = false;
        await host.Flags.RestoreAsync(user, CancellationToken.None);
        Assert.Null(host.Store.TryGet(user.Id)!.JellyfinChanges);
        Assert.True(user.RememberAudioSelections);
    }

    [Fact]
    public async Task RestoreAll_RestoresEveryone_AndTurnsTheSwitchOff()
    {
        var host = new FlagsHost();
        var a = host.Add(audio: true, subtitles: true);
        var b = host.Add(audio: false, subtitles: true);
        var untouched = host.Add(audio: true, subtitles: true, handle: false);
        await host.Flags.ApplyAsync(a, CancellationToken.None);
        await host.Flags.ApplyAsync(b, CancellationToken.None);

        await host.Flags.RestoreAllAsync(CancellationToken.None);

        Assert.True(a.RememberAudioSelections && a.RememberSubtitleSelections);
        Assert.False(b.RememberAudioSelections);
        Assert.True(b.RememberSubtitleSelections);
        Assert.False(host.Store.TryGet(a.Id)!.HandleNextEpisode);
        Assert.Null(host.Store.TryGet(b.Id)!.JellyfinChanges);
        Assert.True(untouched.RememberAudioSelections);
        Assert.DoesNotContain(host.Updates, u => u.UserId == untouched.Id);
    }

    [Fact]
    public void BuildConfiguration_KeepsEverythingButTheTwoFlags()
    {
        var user = NewUser();
        var config = RememberFlags.BuildConfiguration(user, false, true);
        Assert.False(config.RememberAudioSelections);
        Assert.True(config.RememberSubtitleSelections);
        Assert.Equal(user.SubtitleLanguagePreference, config.SubtitleLanguagePreference);
        Assert.Equal(user.SubtitleMode, config.SubtitleMode);
        Assert.Equal(user.HidePlayedInLatest, config.HidePlayedInLatest);
        Assert.Equal(user.CastReceiverId, config.CastReceiverId);
        Assert.NotNull(config.OrderedViews);
        Assert.NotNull(config.GroupedFolders);
    }

    // ---- validator rules for the new admin fields ----

    private static IReadOnlyList<ValidationError> ValidateAdmin(PluginConfiguration config)
    {
        SettingsValidator.PrepareAdmin(config);
        return SettingsValidator.ValidateAdmin(config, LanguageTable.Default);
    }

    [Fact]
    public void Validator_AcceptsTheShippedAppModes()
    {
        Assert.Empty(ValidateAdmin(new PluginConfiguration()));
    }

    [Fact]
    public void Validator_RejectsBadAppRows()
    {
        var config = new PluginConfiguration
        {
            AppModes = new List<AppMode>
            {
                new() { Client = "Jellyfin Web", Mode = AppModeKind.ReplaceOnAutoplay },
                new() { Client = " jellyfin web ", Mode = AppModeKind.Trust },
                new() { Client = "  ", Mode = AppModeKind.Trust },
                new() { Client = new string('a', 101) },
                new() { Client = "Streamyfin", Mode = (AppModeKind)42 },
            },
        };

        var errors = ValidateAdmin(config);
        Assert.Contains(errors, e => e.Path == "appModes[1].client" && e.Message.Contains("twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Path == "appModes[2].client");
        Assert.Contains(errors, e => e.Path == "appModes[3].client");
        Assert.Contains(errors, e => e.Path == "appModes[4].mode");
        Assert.Equal("jellyfin web", config.AppModes[1].Client);
    }

    [Fact]
    public void Validator_CapsTheAppTable()
    {
        var config = new PluginConfiguration
        {
            AppModes = Enumerable.Range(0, SettingsValidator.MaxAppModes + 1).Select(i => new AppMode { Client = "App " + i }).ToList(),
        };
        Assert.Contains(ValidateAdmin(config), e => e.Path == "appModes");
    }

    [Theory]
    [InlineData("manualChangeAfterSeconds", -1)]
    [InlineData("manualChangeAfterSeconds", 601)]
    [InlineData("servedWindowMinutes", 0)]
    [InlineData("servedWindowMinutes", 1441)]
    [InlineData("autoplayWindowSeconds", -1)]
    [InlineData("autoplayWindowSeconds", 3601)]
    public void Validator_RejectsTimingNumbersOutOfRange(string field, int value)
    {
        var config = new PluginConfiguration();
        switch (field)
        {
            case "manualChangeAfterSeconds": config.ManualChangeAfterSeconds = value; break;
            case "servedWindowMinutes": config.ServedWindowMinutes = value; break;
            case "autoplayWindowSeconds": config.AutoplayWindowSeconds = value; break;
        }

        var error = Assert.Single(ValidateAdmin(config));
        Assert.Equal(field, error.Path);
    }

    [Fact]
    public void Validator_AcceptsTheTimingBounds()
    {
        var low = new PluginConfiguration { ManualChangeAfterSeconds = 0, ServedWindowMinutes = 1, AutoplayWindowSeconds = 0 };
        var high = new PluginConfiguration { ManualChangeAfterSeconds = 600, ServedWindowMinutes = 1440, AutoplayWindowSeconds = 3600 };
        Assert.Empty(ValidateAdmin(low));
        Assert.Empty(ValidateAdmin(high));
    }

    [Fact]
    public void Validator_RejectsUndefinedModes()
    {
        var config = new PluginConfiguration { UnknownAppMode = (AppModeKind)9, CorrectAfterStart = (CorrectAfterStartMode)9 };
        var errors = ValidateAdmin(config);
        Assert.Contains(errors, e => e.Path == "unknownAppMode");
        Assert.Contains(errors, e => e.Path == "correctAfterStart");
    }
}
