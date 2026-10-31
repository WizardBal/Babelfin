using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using C = Babelfin.Engine.Tests.Settings.Ctx;
using static Babelfin.Engine.Tests.Regression.PreferenceApiTests;

namespace Babelfin.Engine.Tests.Regression;

public class GenreMigrationTests
{
    [Fact]
    public void DirectLegacyOrderSurvivesClassification_AndSplitArchivesStateTheirAmbiguity()
    {
        foreach (var extras in new[] { false, true })
        {
            var first = Genre("No subs", Audio("ja"), "first");
            first.Conditions[0].Values = new() { "Anime", "Animation" };
            if (extras) first.Settings.Format = FormatPreference.TextOnly;
            var second = Genre("Anime", Audio("en"), "second");
            var original = JsonSerializer.Serialize(first);
            var person = C.Person(); person.SchemaVersion = 1;
            person.SmartRules.AddRange(new[] { first.Clone(), second.Clone() });
            PersonStore.Migrate(person);
            var admin = new PluginConfiguration { ConfigVersion = 6, SmartRules = new() { first.Clone(), second.Clone() } };
            Assert.True(admin.Upgrade());
            foreach (var rows in new[] { person.SmartRules, admin.SmartRules })
            {
                Assert.Equal(new[] { "Anime", "Animation", "Anime" }, rows.Select(r => r.Name));
                Assert.Equal("ja", rows[0].Settings.AudioChain![0].Language);
                Assert.Equal("en", rows[2].Settings.AudioChain![0].Language);
                Assert.Equal(3, rows.Select(r => r.Id).Distinct().Count());
            }
            foreach (var result in new[] { C.Resolve(person, null, C.Episode(genres: new[] { "Anime" })), C.Resolve(C.Person(), admin, C.Episode(genres: new[] { "Anime" })) })
                Assert.Equal("ja", result.Profile.AudioChain[0].Language);
            var animation = C.Resolve(person, null, C.Episode(genres: new[] { "Animation" }));
            Assert.Equal("your Animation genre settings", animation.Sources[SettingsFields.AudioChain]);
            Assert.DoesNotContain("No subs", animation.Sources[SettingsFields.AudioChain]);
            Assert.Contains(person.MigrationNotes, n => n.Contains("No subs", StringComparison.Ordinal));
            var personArchive = extras ? person.Retired.Rules : person.Retired.GenreRules;
            var adminArchive = extras ? admin.Retired.Rules : admin.Retired.GenreRules;
            Assert.Equal(original, JsonSerializer.Serialize(personArchive.First(r => r.Id == "first")));
            Assert.Equal(original, JsonSerializer.Serialize(adminArchive.First(r => r.Id == "first")));
            Assert.DoesNotContain(person.MigrationNotes, n => n.Contains("cannot be reconstructed", StringComparison.Ordinal));
            var ids = person.SmartRules.Select(r => r.Id).ToArray();
            PersonStore.Migrate(person); Assert.False(admin.Upgrade());
            Assert.Equal(ids, person.SmartRules.Select(r => r.Id));

            // Legacy schema 2/config 7 archives retain order only within their separate groups.
            var split = C.Person(); split.SchemaVersion = 2;
            var splitAdmin = new PluginConfiguration { ConfigVersion = 7 };
            (extras ? split.Retired.Rules : split.Retired.GenreRules).Add(first.Clone());
            (extras ? splitAdmin.Retired.Rules : splitAdmin.Retired.GenreRules).Add(first.Clone());
            split.Retired.GenreRules.Add(second.Clone()); splitAdmin.Retired.GenreRules.Add(second.Clone());
            PersonStore.Migrate(split); splitAdmin.Upgrade();
            var expected = extras ? "en" : "ja";
            Assert.Equal(expected, C.Resolve(split, null, C.Episode(genres: new[] { "Anime" })).Profile.AudioChain[0].Language);
            Assert.Equal(expected, C.Resolve(C.Person(), splitAdmin, C.Episode(genres: new[] { "Anime" })).Profile.AudioChain[0].Language);
            Assert.Equal(extras, split.MigrationNotes.Any(n => n.Contains("cross-list order cannot be reconstructed", StringComparison.Ordinal)));
            Assert.Equal(extras, splitAdmin.LastUpgradeNote?.Contains("cross-list order cannot be reconstructed", StringComparison.Ordinal) == true);
            Assert.Equal(original, JsonSerializer.Serialize((extras ? split.Retired.Rules : split.Retired.GenreRules).First(r => r.Id == "first")));
        }
    }

    [Fact]
    public void LegacyForeignTitlesMaterialisesAudioAtGenrePrecedence_WithoutChangingArchives()
    {
        foreach (var foreign in new[] { ForeignTitlesRule.DubInMyLanguage, ForeignTitlesRule.OriginalWithSubtitles })
        {
            var legacy = Genre("No subs", new() { ForeignTitles = foreign }, "legacy");
            legacy.Conditions[0].Values = new() { "Animation" };
            var original = JsonSerializer.Serialize(legacy);
            var person = C.Person(); person.SchemaVersion = 2; person.PerLibrary = false;
            person.Everyday.AudioChain = Audio("ja").AudioChain;
            person.Retired.GenreRules.Add(legacy.Clone()); PersonStore.Migrate(person);
            var row = Assert.Single(person.SmartRules);
            var expected = StarterRules.ChainFor(foreign);
            Assert.Equal(expected.Select(s => s.Kind), row.Settings.AudioChain!.Select(s => s.Kind));
            Assert.Null(row.Settings.ForeignTitles);
            var result = C.Resolve(person, null, C.Episode(genres: new[] { "Animation" }, original: "ja"));
            Assert.Equal(expected, result.Profile.AudioChain);
            Assert.Equal("your Animation genre settings", result.Sources[SettingsFields.AudioChain]);
            Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(person.Retired.GenreRules)));
            var admin = new PluginConfiguration { ConfigVersion = 7, Everyday = Audio("ja"), PerLibraryDefaults = false };
            admin.Retired.GenreRules.Add(legacy.Clone()); admin.Upgrade();
            Assert.Equal(expected, C.Resolve(C.Person(), admin, C.Episode(genres: new[] { "Animation" })).Profile.AudioChain);
            Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(admin.Retired.GenreRules)));
            // An explicit chain in the same legacy row remains authoritative.
            legacy.Settings.AudioChain = Audio("de").AudioChain;
            var explicitPerson = C.Person(); explicitPerson.SchemaVersion = 2; explicitPerson.Retired.GenreRules.Add(legacy);
            PersonStore.Migrate(explicitPerson);
            Assert.Equal("de", Assert.Single(explicitPerson.SmartRules).Settings.AudioChain![0].Language);
        }
    }

    [Fact]
    public void DisablingPlaybackDoesNotEraseSavedPreferencesStatus()
    {
        foreach (var personEnabled in new[] { false, true })
        foreach (var serverEnabled in new[] { false, true })
        {
            var p = C.Person(); p.Enabled = personEnabled; p.Everyday.AudioChain = Audio("ja").AudioChain;
            var admin = new PluginConfiguration { Enabled = serverEnabled };
            Assert.True(ChoiceStatusLogic.ForPerson(p, admin, new[] { Ids.Shows }));
            var store = new FakePersonStore(); store.Save(p);
            var controller = Controller(store, admin, Array.Empty<VirtualFolderInfo>());
            Assert.True(Response(controller.Admin()).HasChoices);
            p.Everyday.AudioChain = null;
            Assert.False(ChoiceStatusLogic.ForPerson(p, admin, new[] { Ids.Shows }));
            admin.PerLibraryDefaults = false; admin.Everyday = Audio("ja");
            Assert.True(ChoiceStatusLogic.AdminDefaults(admin, Array.Empty<Guid>()));
            Assert.True(ChoiceStatusLogic.ForPerson(p, admin, new[] { Ids.Shows }));
        }
    }

    [Fact]
    public void AdminStatusIgnoresDeletedLibraryRows_InLogicAndEndpoint()
    {
        foreach (var perLibrary in new[] { false, true })
        {
            var admin = new PluginConfiguration { PerLibraryDefaults = perLibrary };
            var orphan = C.TheirLibrary(Ids.Anime, perLibrary ? Audio("ja") : new() { Format = FormatPreference.TextOnly });
            admin.Libraries.Add(orphan);
            Assert.False(ChoiceStatusLogic.AdminDefaults(admin, new[] { Ids.Shows }));
            Assert.True(ChoiceStatusLogic.AdminDefaults(admin, new[] { Ids.Anime }));
            var store = new FakePersonStore();
            Assert.False(Response(Controller(store, admin, new[] { new VirtualFolderInfo { ItemId = Ids.Shows.ToString("N") }, new VirtualFolderInfo { ItemId = "invalid" } }).Admin()).HasChoices);
            Assert.True(Response(Controller(store, admin, new[] { new VirtualFolderInfo { ItemId = Ids.Anime.ToString("N") } }).Admin()).HasChoices);
            orphan.Settings = new();
            Assert.False(ChoiceStatusLogic.AdminDefaults(admin, new[] { Ids.Anime }));
        }
    }

    private static ChoiceStatusController Controller(FakePersonStore store, PluginConfiguration admin, IEnumerable<VirtualFolderInfo> folders)
    {
        var libraries = CallProxy.Create<ILibraryManager>((m, _) => m.Name == "GetVirtualFolders" ? folders.ToList() : null).Proxy;
        return new(store, ApiSafetyHost.Users(ApiSafetyHost.Person()), libraries, () => admin);
    }
    private static ChoiceStatus Response(ActionResult<ChoiceStatus> response) => Assert.IsType<ChoiceStatus>(Assert.IsType<OkObjectResult>(response.Result).Value);
}
