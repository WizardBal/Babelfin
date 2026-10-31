using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class SettingsFieldIntentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyedLibraryLeafKeepsHiddenFieldsAndConcurrentOtherLibrary(bool removeFromBody)
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Libraries = new List<LibraryLayer>
        {
            new() { LibraryId = first.ToString("N"), Name = "First", Settings = new SettingsLayer { Reads = new[] { "en" }, AvoidCommentary = false } },
            new() { LibraryId = second.ToString("N"), Name = "Second", Settings = new SettingsLayer { Reads = new[] { "en" } } },
        } });
        var body = store.TryGet(user.Id)!.Clone();
        body.FindLibrary(first)!.Settings.Reads = null; body.FindLibrary(first)!.Settings.AvoidCommentary = null;
        if (removeFromBody) body.Libraries.RemoveAt(0);
        store.BeforeUpdate = () => store.Inner.Update(user.Id, current =>
        {
            var copy = current!.Clone(); copy.FindLibrary(first)!.Settings.Format = Jellyfin.Plugin.Babelfin.Engine.Selection.FormatPreference.TextOnly;
            copy.FindLibrary(second)!.Settings.Reads = new[] { "ja" }; return copy;
        });
        var response = await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, "libraries." + first.ToString("D").ToUpperInvariant() + ".settings.reads");
        Assert.IsType<OkObjectResult>(response.Result);
        var result = store.TryGet(user.Id)!;
        Assert.Equal(2, result.Libraries.Count);
        Assert.Null(result.FindLibrary(first)!.Settings.Reads);
        Assert.False(result.FindLibrary(first)!.Settings.AvoidCommentary);
        Assert.Equal(Jellyfin.Plugin.Babelfin.Engine.Selection.FormatPreference.TextOnly, result.FindLibrary(first)!.Settings.Format);
        Assert.Equal(new[] { "ja" }, result.FindLibrary(second)!.Settings.Reads);
    }

    [Fact]
    public void ServerKeyedLibraryLeafAndOriginalAudioKeepOtherRowsAndHiddenFields()
    {
        using var host = new LocalStorageTestHost(); var first = Guid.NewGuid(); var other = Guid.NewGuid();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, Libraries = new List<LibraryDefault>
        {
            new() { LibraryId = first.ToString("N"), OriginalAudio = OriginalAudioChoice.Yes, Settings = new SettingsLayer { AvoidCommentary = false, Reads = new[] { "en" } } },
            new() { LibraryId = other.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "ja" } } },
        } });
        var body = new PluginConfiguration { Libraries = new List<LibraryDefault> { new() { LibraryId = first.ToString("D"), OriginalAudio = OriginalAudioChoice.Auto, Settings = new SettingsLayer { Reads = Array.Empty<string>() } } } };
        var response = ApiSafetyHost.Admin(host.Store).PutSettings(body, "libraries." + first.ToString("N") + ".settings.reads,libraries." + first.ToString("N") + ".originalAudio");
        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(2, plugin.Configuration.Libraries.Count);
        Assert.Empty(plugin.Configuration.FindLibrary(first)!.Settings.Reads!);
        Assert.False(plugin.Configuration.FindLibrary(first)!.Settings.AvoidCommentary);
        Assert.Equal(OriginalAudioChoice.Auto, plugin.Configuration.FindLibrary(first)!.OriginalAudio);
        Assert.Equal(new[] { "ja" }, plugin.Configuration.FindLibrary(other)!.Settings.Reads);
    }

    [Fact]
    public void KeywordAndStarterLeafIntentHonorsEmptyAndFalseAndKeepsHiddenSiblings()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion,
            Starter = new StarterSettings { Enabled = true, GenreSharePercent = 77 },
            Keywords = new KeywordSettings { CommentaryWords = new List<string> { "custom commentary" }, FullWords = new List<string> { "custom full" } },
        });
        var body = new PluginConfiguration { Starter = new StarterSettings { Enabled = false }, Keywords = new KeywordSettings { CommentaryWords = new List<string>() } };
        Assert.IsType<OkObjectResult>(ApiSafetyHost.Admin(host.Store).PutSettings(body, "starter.enabled,keywords.commentaryWords").Result);
        Assert.False(plugin.Configuration.Starter.Enabled);
        Assert.Equal(77, plugin.Configuration.Starter.GenreSharePercent);
        Assert.Empty(plugin.Configuration.Keywords.CommentaryWords);
        Assert.Equal(new[] { "custom full" }, plugin.Configuration.Keywords.FullWords);
    }

    [Fact]
    public async Task AdminLockLeafNullDoesNotReplaceOtherLocksOrEverydayFields()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Everyday = new SettingsLayer { Reads = new[] { "ja" } }, Locks = new SettingsLayer { AvoidCommentary = false, PrefersSdh = true } });
        var response = await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, new PersonSettings { Locks = new SettingsLayer { PrefersSdh = null } }, true, "locks.prefersSdh");
        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Null(store.TryGet(user.Id)!.Locks.PrefersSdh);
        Assert.False(store.TryGet(user.Id)!.Locks.AvoidCommentary);
        Assert.Equal(new[] { "ja" }, store.TryGet(user.Id)!.Everyday.Reads);
    }

    [Theory]
    [InlineData("libraries.not-a-guid.settings.reads")]
    [InlineData("libraries.00000000000000000000000000000000.settings.reads")]
    [InlineData("everyday")]
    [InlineData("keywords.fullWordsXml")]
    [InlineData("newUserTemplate.everyday.reads")]
    [InlineData("newUserTemplate.locks.prefersSdh")]
    [InlineData("starter.genreSharePercent.extra")]
    public void InvalidOrAmbiguousServerFieldPathsAreRejected(string field)
    {
        Assert.False(SettingsIntent.TryParse(field, true, true, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldIntentNeverBypassesActualNewerStoredFile409(bool undecodable)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(user.Id))!);
        var original = undecodable ? "{\"schemaVersion\":999,\"everyday\":{\"reads\":{\"future\":true}}}" : "{\"schemaVersion\":999,\"everyday\":{\"reads\":[\"ja\"]}}";
        File.WriteAllText(store.FilePath(user.Id), original);
        var admin = ApiSafetyHost.Admin(store, null, user);
        var response = await StorageApiSafetyTests.InvokeApi(admin, async () => (await admin.PutUserSettings(user.Id, new PersonSettings(), true, "everyday.reads")).Result);
        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(response).StatusCode);
        Assert.Equal(original, File.ReadAllText(store.FilePath(user.Id)));
    }
}
