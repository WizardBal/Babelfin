using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class EditedFieldValidationTests
{
    private static MeController Me(ApiSafetyStore store, Jellyfin.Database.Implementations.Entities.User user)
    {
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name == "GetVirtualFolders" ? new List<VirtualFolderInfo>() : null).Proxy;
        var resolver = CallProxy.Create<IProfileResolver>((method, args) => method.Name == "Resolve" ? ProfileResolver.Resolve(store.TryGet(user.Id)!, new PluginConfiguration(), (ItemContext)args[1]!, LanguageTable.Default) : null).Proxy;
        return new MeController(library, ApiSafetyHost.Users(user), CallProxy.Create<ICollectionManager>().Proxy, store, resolver, new FakeLibraryFacts(), new FakeContextBuilder())
        { ControllerContext = ApiSafetyHost.Me(store, user).ControllerContext };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EditedLeafPreservesStoredReferencesAfterLanguageAliasRemoval(bool admin, bool keepOverrides)
    {
        using var host = new LocalStorageTestHost();
        var config = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, LanguageAliases = new() { new LanguageAlias { Raw = "custom-language", Code = "fr" } } };
        var plugin = host.CreatePlugin(config);
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        var original = new PersonSettings { UserId = user.Id, Everyday = new SettingsLayer { Reads = new[] { "en" } },
            Overrides = new() { new ItemOverride { ItemId = Guid.NewGuid().ToString("N"), Settings = new SettingsLayer { Reads = new[] { "custom-language" } } } },
            BurnedIn = new() { new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new() { "custom-language" } } } };
        Assert.Empty(SettingsValidator.ValidatePerson(original, new EngineServices(config).Languages, true));
        store.Inner.Save(original);
        config.LanguageAliases.Clear(); plugin.UpdateConfiguration(config);
        Assert.NotEmpty(SettingsValidator.ValidatePerson(original, EngineServices.Current.Languages, true));
        var overrides = JsonSerializer.Serialize(original.Overrides, BabelfinJson.Options);
        var flags = JsonSerializer.Serialize(original.BurnedIn, BabelfinJson.Options);
        var body = new PersonSettings { Everyday = new SettingsLayer { Reads = Array.Empty<string>() } };
        IActionResult? response = admin ? (await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, keepOverrides, "everyday.reads")).Result
            : (await Me(store, user).PutSettings(body, keepOverrides, "everyday.reads")).Result;
        Assert.IsType<OkObjectResult>(response);
        Assert.Empty(store.TryGet(user.Id)!.Everyday.Reads!);
        Assert.Equal(overrides, JsonSerializer.Serialize(store.TryGet(user.Id)!.Overrides, BabelfinJson.Options));
        Assert.Equal(flags, JsonSerializer.Serialize(store.TryGet(user.Id)!.BurnedIn, BabelfinJson.Options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidEditedLeafReturnsRealValidationAndDoesNotWrite(bool admin)
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = user.Id });
        var before = JsonSerializer.Serialize(store.TryGet(user.Id), BabelfinJson.Options);
        var body = new PersonSettings { Everyday = new SettingsLayer { Reads = new[] { "unknown-language" } } };
        IActionResult? response = admin ? (await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, "everyday.reads")).Result
            : (await Me(store, user).PutSettings(body, true, "everyday.reads")).Result;
        var problem = Assert.IsType<ApiProblem>(Assert.IsType<BadRequestObjectResult>(response).Value);
        Assert.Contains(problem.Errors, error => error.Path.StartsWith("everyday.reads", StringComparison.Ordinal));
        Assert.Equal(before, JsonSerializer.Serialize(store.TryGet(user.Id), BabelfinJson.Options));
    }

    [Fact]
    public async Task LatestInvalidUntouchedLibraryLeafDoesNotBlockAnotherLeaf()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person(); var id = Guid.NewGuid();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Libraries = new() { new LibraryLayer { LibraryId = id.ToString("N"), Name = new string('x', SettingsValidator.MaxStringLength + 1), Settings = new SettingsLayer { Reads = new[] { "en" } } } } });
        store.BeforeUpdate = () => store.Inner.Update(user.Id, current => { var copy = current!.Clone(); copy.FindLibrary(id)!.Settings.Understood = new[] { "removed-alias" }; return copy; });
        var body = new PersonSettings { Libraries = new() { new LibraryLayer { LibraryId = id.ToString("N"), Settings = new SettingsLayer { Reads = null } } } };
        var response = await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, $"libraries.{id:N}.settings.reads");
        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Null(store.TryGet(user.Id)!.FindLibrary(id)!.Settings.Reads);
        Assert.Equal(new[] { "removed-alias" }, store.TryGet(user.Id)!.FindLibrary(id)!.Settings.Understood);
        Assert.Equal(new string('x', SettingsValidator.MaxStringLength + 1), store.TryGet(user.Id)!.FindLibrary(id)!.Name);
    }

    [Fact]
    public async Task LegacyFullPutStillRejectsInvalidStoredBody()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        var invalid = new PersonSettings { UserId = user.Id, Everyday = new SettingsLayer { Reads = new[] { "removed-alias" } } };
        store.Inner.Save(invalid);
        Assert.IsType<BadRequestObjectResult>((await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, invalid.Clone(), false)).Result);
        Assert.Equal(0, store.Updates);
    }
    [Fact]
    public async Task EditedDependencyChangedUnderTheUpdateLockReturns400WithItsRealField()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Everyday = new SettingsLayer { UnlabelledAudioLanguage = "en" } });
        store.BeforeUpdate = () => store.Inner.Update(user.Id, current => { var copy = current!.Clone(); copy.Everyday.UnlabelledAudioLanguage = "removed-alias"; return copy; });
        var admin = ApiSafetyHost.Admin(store, null, user);
        var response = await StorageApiSafetyTests.InvokeApi(admin, async () => (await admin.PutUserSettings(user.Id,
            new PersonSettings { Everyday = new SettingsLayer { UnlabelledAudio = Jellyfin.Plugin.Babelfin.Engine.Selection.UnlabelledAudioRule.SpecificLanguage } }, true, "everyday.unlabelledAudio")).Result);
        var problem = Assert.IsType<ApiProblem>(Assert.IsType<BadRequestObjectResult>(response).Value);
        Assert.Contains(problem.Errors, error => error.Path == "everyday.unlabelledAudioLanguage");
        Assert.Null(store.TryGet(user.Id)!.Everyday.UnlabelledAudio);
        Assert.Equal("removed-alias", store.TryGet(user.Id)!.Everyday.UnlabelledAudioLanguage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UneditedMalformedFlagsAtLimitRemainExactDuringScalarSave(bool admin)
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        var person = new PersonSettings { UserId = user.Id, BurnedIn = Enumerable.Range(0, SettingsValidator.MaxBurnedIn)
            .Select(i => new BurnedInFlag { ItemId = "legacy-invalid-" + i, Scope = OverrideScope.Library, Languages = new() { "removed-alias" } }).ToList() };
        store.Inner.Save(person); var before = JsonSerializer.Serialize(person.BurnedIn, BabelfinJson.Options);
        IActionResult? response = admin ? (await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, new PersonSettings { Enabled = false }, false, "enabled")).Result
            : (await Me(store, user).PutSettings(new PersonSettings { Enabled = false }, false, "enabled")).Result;
        Assert.IsType<OkObjectResult>(response);
        Assert.False(store.TryGet(user.Id)!.Enabled);
        Assert.Equal(before, JsonSerializer.Serialize(store.TryGet(user.Id)!.BurnedIn, BabelfinJson.Options));
    }

    [Fact]
    public void ExactTemplateLeafPreservesInvalidHiddenSiblings()
    {
        using var host = new LocalStorageTestHost();
        var config = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, NewUserTemplate = new PersonSettings
            { Everyday = new SettingsLayer { Reads = new[] { "removed-alias" } }, Locks = new SettingsLayer { Understood = new[] { "removed-alias" } } } };
        var plugin = host.CreatePlugin(config);
        var response = ApiSafetyHost.Admin(host.Store).PutSettings(new PluginConfiguration
            { NewUserTemplate = new PersonSettings { Everyday = new SettingsLayer { Understood = Array.Empty<string>() } } }, "newUserTemplate.everyday.understood");
        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Empty(plugin.Configuration.NewUserTemplate!.Everyday.Understood!);
        Assert.Equal(new[] { "removed-alias" }, plugin.Configuration.NewUserTemplate.Everyday.Reads);
        Assert.Equal(new[] { "removed-alias" }, plugin.Configuration.NewUserTemplate.Locks.Understood);
    }

    [Fact]
    public async Task KeyedLeafCannotAddBeyondLibraryCapButCanEditAnExistingOverLimitDocument()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person(); var newId = Guid.NewGuid();
        var person = new PersonSettings { UserId = user.Id, Libraries = Enumerable.Range(0, SettingsValidator.MaxLibraries)
            .Select(_ => new LibraryLayer { LibraryId = Guid.NewGuid().ToString("N") }).ToList() };
        store.Inner.Save(person); var before = JsonSerializer.Serialize(store.TryGet(user.Id), BabelfinJson.Options);
        var body = new PersonSettings { Libraries = new() { new LibraryLayer { LibraryId = newId.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "en" } } } } };
        Assert.IsType<BadRequestObjectResult>((await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, $"libraries.{newId:N}.settings.reads")).Result);
        Assert.Equal(before, JsonSerializer.Serialize(store.TryGet(user.Id), BabelfinJson.Options));
        person.Libraries.Add(body.Libraries[0].Clone()); store.Inner.Save(person);
        body.Libraries[0].Settings.Reads = null;
        Assert.IsType<OkObjectResult>((await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, $"libraries.{newId:N}.settings.reads")).Result);
        Assert.Equal(SettingsValidator.MaxLibraries + 1, store.TryGet(user.Id)!.Libraries.Count);
        Assert.Null(store.TryGet(user.Id)!.FindLibrary(newId)!.Settings.Reads);
    }

    [Fact]
    public async Task AtomicArrayAndEditedLocksStillReceiveFullValidation()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person(); store.Inner.Save(new PersonSettings { UserId = user.Id });
        var invalid = new PersonSettings { SmartRules = new() { new SmartRule { Name = "Invalid choice", Settings = new SettingsLayer { Reads = new[] { "removed-alias" } } } } };
        Assert.IsType<BadRequestObjectResult>((await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, invalid, true, "smartRules")).Result);
        var locks = new PersonSettings { Locks = new SettingsLayer { Reads = new[] { "removed-alias" } } };
        var problem = Assert.IsType<ApiProblem>(Assert.IsType<BadRequestObjectResult>((await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, locks, true, "locks.reads")).Result).Value);
        Assert.Contains(problem.Errors, error => error.Path.StartsWith("locks.reads", StringComparison.Ordinal));
        Assert.Equal(0, store.Updates);
    }

    [Fact]
    public async Task KeyedLeafErrorsReferToTheActualLibraryRow()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person(); var id = Guid.NewGuid();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Libraries = new()
            { new LibraryLayer { LibraryId = Guid.NewGuid().ToString("N") }, new LibraryLayer { LibraryId = id.ToString("N") } } });
        var body = new PersonSettings { Libraries = new() { new LibraryLayer { LibraryId = id.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "removed-alias" } } } } };
        var response = await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, $"libraries.{id:N}.settings.reads");
        var problem = Assert.IsType<ApiProblem>(Assert.IsType<BadRequestObjectResult>(response.Result).Value);
        Assert.Contains(problem.Errors, error => error.Path.StartsWith("libraries[1].settings.reads", StringComparison.Ordinal));
        Assert.Equal(0, store.Updates);
    }

}
