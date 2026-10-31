using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Compat;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Jellyfin.Plugin.Babelfin.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[CollectionDefinition("Preservation safety globals", DisableParallelization = true)]
public sealed class PreservationSafetyGlobalCollection { }

[Collection("Preservation safety globals")]
public sealed class PreservationSafetyTests : IDisposable
{
    private static readonly FieldInfo InstanceField = typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly Plugin? _previousPlugin = Plugin.Instance;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bf-preservation-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        InstanceField.SetValue(null, _previousPlugin);
        if (!Directory.Exists(_folder)) return;
        var path = Path.GetFullPath(_folder);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test cleanup must stay in its temporary directory.");
        Directory.Delete(path, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FutureBackgroundUpdatePreservesExactBytesAndNeverInvokesCallback(bool preload)
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        var original = WriteFuturePerson(store, id);
        if (preload) Assert.Equal(FutureSchema, store.TryGet(id)!.SchemaVersion);
        var called = false;
        var events = 0;
        store.Changed += (_, _) => events++;

        var saved = store.Update(id, current =>
        {
            called = true;
            var copy = current!.Clone();
            copy.MigrationNotes.Clear();
            return copy;
        });

        Assert.False(saved);
        Assert.False(called);
        Assert.Equal(0, events);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(id)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.bak"));
        Assert.Equal(FutureSchema, store.TryGet(id)!.SchemaVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPersonSavePreservesOriginalBytesInBackup(bool preload)
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        var original = WriteFuturePerson(store, id);
        if (preload) _ = store.TryGet(id);
        var called = false;

        var saved = ((IExplicitPersonUpdates)store).UpdateExplicit(id, current =>
        {
            called = true;
            Assert.Equal(FutureSchema, current!.SchemaVersion);
            var copy = current.Clone();
            copy.UserName = "Explicitly saved";
            copy.Everyday.Understood = new[] { "ja" };
            return copy;
        });

        Assert.True(saved);
        Assert.True(called);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(id))!, "*.schema-" + FutureSchema + "-*.bak"));
        Assert.Equal(original, File.ReadAllBytes(backup));
        var reloaded = new PersonStore(store.DataFolder, NullLogger<PersonStore>.Instance).TryGet(id)!;
        Assert.Equal(PersonSettings.CurrentSchemaVersion, reloaded.SchemaVersion);
        Assert.Equal("Explicitly saved", reloaded.UserName);
        Assert.Equal(new[] { "ja" }, reloaded.Everyday.Understood);
    }

    [Fact]
    public void ExplicitApiSettingsSaveRejectsFutureFormatWithoutChangingBytes()
    {
        var store = NewStore();
        var user = NewUser(true, true);
        var original = WriteFuturePerson(store, user.Id);
        var stored = store.TryGet(user.Id)!;
        var incoming = stored.Clone();
        incoming.Everyday.Understood = new[] { "ja" };

        var conflict = Assert.Throws<PersonStorageConflictException>(() => PersonDocuments.SaveMerged(store, stored, incoming, takeLocks: false, user, keepOverrides: true));

        Assert.Contains("Upgrade Babelfin", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(FutureSchema, store.TryGet(user.Id)!.SchemaVersion);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath(user.Id))!, "*.bak"));
    }

    [Fact]
    public void AcknowledgingCurrentNoticesPreservesFutureConfigAndPersonBytes()
    {
        var host = NewPlugin(new PluginConfiguration { ConfigVersion = FutureConfig, LastUpgradeNote = "Future pending note" });
        var configBytes = File.ReadAllBytes(host.Plugin.ConfigurationFilePath);
        var writes = host.Writes;
        var store = NewStore();
        var futureId = Guid.NewGuid();
        var personBytes = WriteFuturePerson(store, futureId);
        var currentId = Guid.NewGuid();
        store.Save(new PersonSettings { UserId = currentId, UserName = "Current", MigrationNotes = new() { "An actual completed migration" } });
        var controller = new MigrationController(store);
        var response = Assert.IsType<OkObjectResult>(controller.Get().Result);
        var notices = Assert.IsAssignableFrom<IReadOnlyList<MigrationNotice>>(response.Value);
        Assert.Equal(currentId, Assert.Single(notices).UserId);

        Assert.IsType<NoContentResult>(controller.Acknowledge());
        host.Plugin.SaveConfiguration(); // Automatic metadata saves must also leave the future XML alone.

        Assert.Equal(writes, host.Writes);
        Assert.Equal(configBytes, File.ReadAllBytes(host.Plugin.ConfigurationFilePath));
        Assert.Equal("Future pending note", host.Plugin.Configuration.LastUpgradeNote);
        Assert.Equal(personBytes, File.ReadAllBytes(store.FilePath(futureId)));
        Assert.NotEmpty(store.TryGet(futureId)!.MigrationNotes);
        Assert.Empty(store.TryGet(currentId)!.MigrationNotes);
    }

    [Fact]
    public void ExplicitConfigurationSaveKeepsAnExactFutureFormatBackup()
    {
        var host = NewPlugin(new PluginConfiguration { ConfigVersion = FutureConfig });
        var original = File.ReadAllBytes(host.Plugin.ConfigurationFilePath);
        var writes = host.Writes;

        host.Plugin.UpdateConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = false });

        Assert.True(host.Writes > writes);
        Assert.False(host.Plugin.Configuration.Enabled);
        var backups = Directory.GetFiles(host.Plugin.BackupsFolder, "settings-version" + FutureConfig + "-*.xml");
        Assert.NotEmpty(backups);
        Assert.All(backups, path => Assert.Equal(original, File.ReadAllBytes(path)));
        var saved = XDocument.Load(host.Plugin.ConfigurationFilePath);
        Assert.Equal(PluginConfiguration.CurrentVersion.ToString(), saved.Root!.Element("ConfigVersion")!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedStartupBackupBlocksEverySaveUntilPreservationSucceeds(bool explicitSave)
    {
        var originalVersion = PluginConfiguration.CurrentVersion - 1;
        var host = NewPlugin(new PluginConfiguration { ConfigVersion = originalVersion }, blockBackups: true);
        var original = File.ReadAllBytes(host.Plugin.ConfigurationFilePath);
        var writes = host.Writes;
        Assert.Equal(originalVersion, host.Plugin.Configuration.ConfigVersion);
        // A caller stamping the new version must not erase the pending backup requirement.
        host.Plugin.Configuration.ConfigVersion = PluginConfiguration.CurrentVersion;
        Action save = explicitSave
            ? () => host.Plugin.UpdateConfiguration(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion })
            : () => host.Plugin.SaveConfiguration();

        Assert.ThrowsAny<IOException>(save);

        Assert.Equal(writes, host.Writes);
        Assert.Equal(original, File.ReadAllBytes(host.Plugin.ConfigurationFilePath));
        File.Delete(host.Plugin.BackupsFolder); // This fixture intentionally used a file to block directory creation.
        save();
        var backup = Assert.Single(Directory.GetFiles(host.Plugin.BackupsFolder, "settings-version" + originalVersion + "-*.xml"));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.True(host.Writes > writes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GlobalOffOnRestoresOriginalFlagsAndPreservesPersonPreferences(bool audio, bool subtitles)
    {
        var host = NewPlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = NewStore();
        var user = NewUser(audio, subtitles);
        store.Save(new PersonSettings { UserId = user.Id, Enabled = true, HandleNextEpisode = true, Everyday = new() { Understood = new[] { "ja" } } });
        var optedOut = NewUser(true, true);
        store.Save(new PersonSettings { UserId = optedOut.Id, HandleNextEpisode = false });
        var users = Users(user, optedOut);
        var flags = new RememberFlags(users, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        using var services = new ServiceCollection().AddSingleton<IPersonStore>(store).AddSingleton<IUserManager>(users).AddSingleton<IRememberFlags>(flags).BuildServiceProvider();

        await BabelfinStartupFilter.SynchronizeFlagsAsync(services);
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);
        AssertUndoRecord(store, user.Id, audio, subtitles);

        host.Plugin.Configuration.Enabled = false;
        await BabelfinStartupFilter.SynchronizeFlagsAsync(services);
        Assert.Equal(audio, user.RememberAudioSelections);
        Assert.Equal(subtitles, user.RememberSubtitleSelections);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
        AssertPreferences(store, user.Id);

        host.Plugin.Configuration.Enabled = true;
        await BabelfinStartupFilter.SynchronizeFlagsAsync(services);
        Assert.False(user.RememberAudioSelections);
        Assert.False(user.RememberSubtitleSelections);
        AssertUndoRecord(store, user.Id, audio, subtitles);
        AssertPreferences(store, user.Id);

        host.Plugin.Configuration.Enabled = false;
        await BabelfinStartupFilter.SynchronizeFlagsAsync(services);
        Assert.Equal(audio, user.RememberAudioSelections);
        Assert.Equal(subtitles, user.RememberSubtitleSelections);
        AssertPreferences(store, user.Id);
        Assert.True(optedOut.RememberAudioSelections);
        Assert.True(optedOut.RememberSubtitleSelections);
        Assert.Null(store.TryGet(optedOut.Id)!.JellyfinChanges);
        Assert.False(store.TryGet(optedOut.Id)!.HandleNextEpisode);
    }

    [Fact]
    public async Task PersonSaveWhileGloballyDisabledDoesNotReapplyRememberFlags()
    {
        NewPlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = false });
        var store = NewStore();
        var user = NewUser(true, true);
        var before = new PersonSettings { UserId = user.Id, HandleNextEpisode = false };
        var after = before.Clone();
        after.HandleNextEpisode = true;
        store.Save(after);
        var flags = new RememberFlags(Users(user), store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);

        Assert.True(await PersonDocuments.SyncRememberFlagsAsync(flags, user, before, after, store));

        Assert.True(user.RememberAudioSelections);
        Assert.True(user.RememberSubtitleSelections);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
    }

    [Fact]
    public async Task OneUnmigratablePersonDoesNotBlockOtherFlagRestorations()
    {
        NewPlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Enabled = false });
        var store = NewStore();
        var user = NewUser(false, false);
        store.Save(new PersonSettings { UserId = user.Id, HandleNextEpisode = true, JellyfinChanges = new() { RememberAudioSelections = true, RememberSubtitleSelections = true } });
        var badId = Guid.NewGuid();
        var failingStore = CallProxy.Create<IPersonStore>((method, args) => method.Name switch
        {
            "UserIds" => new[] { badId, user.Id },
            "TryGet" => (Guid)args[0]! == badId ? throw new IOException("The first person's schema migration cannot preserve its file") : store.TryGet((Guid)args[0]!),
            _ => null,
        }).Proxy;
        var users = Users(user);
        var flags = new RememberFlags(users, store, new ListActivityLog(), NullLogger<RememberFlags>.Instance);
        using var services = new ServiceCollection().AddSingleton<IPersonStore>(failingStore).AddSingleton<IUserManager>(users).AddSingleton<IRememberFlags>(flags).BuildServiceProvider();

        await BabelfinStartupFilter.SynchronizeFlagsAsync(services);

        Assert.True(user.RememberAudioSelections);
        Assert.True(user.RememberSubtitleSelections);
        Assert.Null(store.TryGet(user.Id)!.JellyfinChanges);
        Assert.True(store.TryGet(user.Id)!.HandleNextEpisode);
    }

    private const int FutureSchema = PersonSettings.CurrentSchemaVersion + 10;
    private const int FutureConfig = PluginConfiguration.CurrentVersion + 10;
    private PersonStore NewStore() => new(Path.Combine(_folder, Guid.NewGuid().ToString("N")), NullLogger<PersonStore>.Instance);
    private PluginHost NewPlugin(PluginConfiguration config, bool blockBackups = false) => new(Path.Combine(_folder, Guid.NewGuid().ToString("N")), config, blockBackups);

    private static byte[] WriteFuturePerson(PersonStore store, Guid id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
        var json = "{\r\n  \"schemaVersion\": " + FutureSchema + ",\r\n  \"userId\": \"" + id + "\",\r\n  \"migrationNotes\": [\"Future person note\"],\r\n  \"futureOnly\": {\"keep\": \"exact bytes, including whitespace\"}\r\n}\r\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(store.FilePath(id), bytes);
        return bytes;
    }

    private static User NewUser(bool audio, bool subtitles) => new("Preservation reviewer", "Default", "Default")
    {
        Id = Guid.NewGuid(), RememberAudioSelections = audio, RememberSubtitleSelections = subtitles,
    };

    private static IUserManager Users(params User[] users) => CallProxy.Create<IUserManager>((method, args) =>
    {
        var user = args.Length > 0 && args[0] is Guid id ? users.Single(u => u.Id == id) : null;
        if (method.Name == "GetUserById") return user;
        if (method.Name == "UpdateConfigurationAsync")
        {
            var config = (UserConfiguration)args[1]!;
            user!.RememberAudioSelections = config.RememberAudioSelections;
            user.RememberSubtitleSelections = config.RememberSubtitleSelections;
            return Task.CompletedTask;
        }
        return null;
    }).Proxy;

    private static void AssertPreferences(PersonStore store, Guid id)
    {
        var person = store.TryGet(id)!;
        Assert.True(person.Enabled);
        Assert.True(person.HandleNextEpisode);
        Assert.Equal(new[] { "ja" }, person.Everyday.Understood);
    }

    private static void AssertUndoRecord(PersonStore store, Guid id, bool audio, bool subtitles)
    {
        var changes = Assert.IsType<JellyfinChanges>(store.TryGet(id)!.JellyfinChanges);
        Assert.Equal(audio, changes.RememberAudioSelections);
        Assert.Equal(subtitles, changes.RememberSubtitleSelections);
    }

    private sealed class PluginHost
    {
        public PluginHost(string folder, PluginConfiguration config, bool blockBackups)
        {
            Directory.CreateDirectory(folder);
            var paths = CallProxy.Create<IApplicationPaths>((method, _) => method.ReturnType == typeof(string) ? folder : null).Proxy;
            var xml = CallProxy.Create<IXmlSerializer>((method, args) =>
            {
                if (method.Name == "DeserializeFromFile")
                {
                    using var input = File.OpenRead(args.OfType<string>().Last());
                    return new XmlSerializer(args.OfType<Type>().Single()).Deserialize(input);
                }
                if (method.Name == "SerializeToFile")
                {
                    using var output = File.Create(args.OfType<string>().Last());
                    var value = args.OfType<BasePluginConfiguration>().Single();
                    new XmlSerializer(value.GetType()).Serialize(output, value);
                    Writes++;
                }
                return null;
            }).Proxy;
            if (blockBackups)
            {
                var backups = Path.Combine(folder, "babelfin", "backups");
                Directory.CreateDirectory(Path.GetDirectoryName(backups)!);
                File.WriteAllText(backups, "Prevent directory creation without touching production paths.");
            }
            var bootstrap = new Plugin(paths, xml);
            using var serialized = new MemoryStream();
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(serialized, config);
            serialized.Position = 0;
            var document = XDocument.Load(serialized);
            document.Root!.Add(new XElement("FutureOnly", new XElement("Keep", "unknown XML must survive")));
            File.WriteAllText(bootstrap.ConfigurationFilePath, document.ToString() + "\r\n", new UTF8Encoding(false));

            Plugin = new Plugin(paths, xml);
        }
        public Plugin Plugin { get; }
        public int Writes { get; private set; }
    }
}
