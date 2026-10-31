using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class SettingsApiCompatibilityTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static AdminController Admin(IPersonStore store, Jellyfin.Database.Implementations.Entities.User user)
    {
        var controller = ApiSafetyHost.Admin(store, null, user);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
    private static MeController Me(IPersonStore store, Jellyfin.Database.Implementations.Entities.User user)
    {
        var resolver = CallProxy.Create<IProfileResolver>((m, _) => m.Name == "Resolve" ? Ctx.Resolve(new PersonSettings(), null, Ctx.Episode()) : null).Proxy;
        var library = CallProxy.Create<ILibraryManager>((method, args) => method.Name switch { "GetItemById" => new Movie { Id = (Guid)args[0]!, Name = "Movie" }, "GetVirtualFolders" => new List<VirtualFolderInfo>(), _ => null }).Proxy;
        var controller = new MeController(library, ApiSafetyHost.Users(user), CallProxy.Create<ICollectionManager>().Proxy, store, resolver, new FakeLibraryFacts(), new FakeContextBuilder());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, user.Id.ToString()) }, "Test")) } };
        return controller;
    }
    private static List<BurnedInFlag> Flags(int count) => Enumerable.Range(0, count).Select(_ => new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "en" } }).ToList();

    [Theory]
    [InlineData(201)]
    [InlineData(260)]
    public async Task UnrelatedSettingsSavesKeepOverLimitFlags(int count)
    {
        using var host = new LocalStorageTestHost(); var user = ApiSafetyHost.Person(); var store = host.Store;
        store.GetOrCreate(user.Id, () => new PersonSettings { BurnedIn = Flags(count) });
        var me = Me(store, user); var admin = Admin(store, user);
        Assert.IsType<OkObjectResult>((await me.PutSettings(new PersonSettings { Enabled = false }, true, "enabled")).Result);
        Assert.IsType<OkObjectResult>((await admin.PutUserSettings(user.Id, new PersonSettings { Enabled = true }, true, "enabled")).Result);
        Assert.IsType<OkObjectResult>((await me.PutSettingsRequest(Body("{\"Enabled\":false}"), true)).Result);
        Assert.IsType<OkObjectResult>((await admin.PutUserSettingsRequest(user.Id, Body("{\"Enabled\":true}"), true)).Result);
        Assert.Equal(count, store.TryGet(user.Id)!.BurnedIn.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySettingsOmissionsKeepStoredValuesAndExplicitEmptyClears(bool admin)
    {
        using var host = new LocalStorageTestHost(); var user = ApiSafetyHost.Person(); var store = host.Store;
        var library = Guid.NewGuid().ToString("N"); var collection = Guid.NewGuid().ToString("N");
        store.GetOrCreate(user.Id, () => new PersonSettings { HandleNextEpisode = true, PerLibrary = true, Libraries = new() { new LibraryLayer { LibraryId = library } }, SmartRules = new() { new SmartRule { Name = "Rule" } }, CollectionOrder = new() { collection }, Locks = new SettingsLayer { Reads = new[] { "fr" } } });
        var me = Me(store, user); var controller = Admin(store, user);
        var omitted = Body("{\"Enabled\":true,\"Everyday\":{}}");
        var first = admin ? (await controller.PutUserSettingsRequest(user.Id, omitted, true)).Result : (await me.PutSettingsRequest(omitted, true)).Result;
        Assert.IsType<OkObjectResult>(first);
        var saved = store.TryGet(user.Id)!;
        Assert.Single(saved.Libraries); Assert.Single(saved.SmartRules); Assert.Single(saved.CollectionOrder);
        Assert.True(saved.HandleNextEpisode); Assert.True(saved.PerLibrary); Assert.Equal(new[] { "fr" }, saved.Locks.Reads);
        var clear = Body("{\"Enabled\":true,\"Libraries\":[],\"SmartRules\":[],\"CollectionOrder\":[],\"Locks\":null}");
        var second = admin ? (await controller.PutUserSettingsRequest(user.Id, clear, true)).Result : (await me.PutSettingsRequest(clear, true)).Result;
        Assert.IsType<OkObjectResult>(second);
        saved = store.TryGet(user.Id)!;
        Assert.Empty(saved.Libraries); Assert.Empty(saved.SmartRules); Assert.Empty(saved.CollectionOrder);
        if (admin) Assert.Null(saved.Locks.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNullClearsLegacyLists(bool admin)
    {
        using var host = new LocalStorageTestHost(); var user = ApiSafetyHost.Person(); var store = host.Store;
        store.GetOrCreate(user.Id, () => new PersonSettings { Libraries = new() { new LibraryLayer { LibraryId = Guid.NewGuid().ToString("N") } }, SmartRules = new() { new SmartRule { Name = "Rule" } }, CollectionOrder = new() { Guid.NewGuid().ToString("N") }, Locks = new SettingsLayer { Reads = new[] { "fr" } } });
        var body = Body("{\"Libraries\":null,\"SmartRules\":null,\"CollectionOrder\":null,\"Locks\":null}");
        var response = admin ? (await Admin(store, user).PutUserSettingsRequest(user.Id, body, true)).Result : (await Me(store, user).PutSettingsRequest(body, true)).Result;
        Assert.IsType<OkObjectResult>(response);
        var saved = store.TryGet(user.Id)!;
        Assert.Empty(saved.Libraries); Assert.Empty(saved.SmartRules); Assert.Empty(saved.CollectionOrder);
        if (admin) Assert.Null(saved.Locks.Reads);
    }

    [Fact]
    public async Task PersonResponsesHideAdministratorIdentity()
    {
        using var host = new LocalStorageTestHost(); var user = ApiSafetyHost.Person(); var store = host.Store;
        var adminId = Guid.NewGuid(); const string adminName = "PrivateAdmin";
        store.GetOrCreate(user.Id, () => new PersonSettings { Everyday = new SettingsLayer { Reads = new[] { "en" } } });
        using (PersonWriteActor.Admin(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, adminId.ToString()), new Claim(ClaimTypes.Name, adminName) }, "Test"))))
            store.Update(user.Id, current => { var copy = current!.Clone(); copy.Enabled = false; return copy; });
        var me = Me(store, user); var admin = Admin(store, user);
        var personView = Assert.IsType<MeSettingsResponse>(Assert.IsType<OkObjectResult>(me.GetSettings().Result).Value);
        Assert.Equal("admin", personView.Settings.LastChangedBy); Assert.Null(personView.Settings.LastChangedByName);
        var adminView = Assert.IsType<PersonSettings>(Assert.IsType<OkObjectResult>(admin.GetUserSettings(user.Id).Result).Value);
        Assert.Contains(adminId.ToString("N"), adminView.LastChangedBy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(adminName, adminView.LastChangedByName);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"everyday.reads\":[\"fr\"]}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        me.Request.Headers["X-Babelfin-Expected"] = expected;
        var conflict = Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(me, async () => (await me.PutSettings(new PersonSettings(), true, "everyday.reads")).Result));
        Assert.Equal(409, conflict.StatusCode);
        var json = JsonSerializer.Serialize(conflict.Value);
        Assert.DoesNotContain(adminId.ToString("N"), json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(adminName, json, StringComparison.Ordinal);
        Assert.Contains("admin", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PersonalExportHidesAdministratorIdentity()
    {
        using var host = new LocalStorageTestHost(); var user = ApiSafetyHost.Person(); var store = host.Store;
        var adminId = Guid.NewGuid(); const string adminName = "PrivateAdmin";
        store.GetOrCreate(user.Id, () => new PersonSettings());
        using (PersonWriteActor.Admin(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, adminId.ToString()), new Claim(ClaimTypes.Name, adminName) }, "Test"))))
            store.Update(user.Id, current => { var copy = current!.Clone(); copy.Enabled = false; return copy; });
        var file = Assert.IsType<FileContentResult>(Me(store, user).Export());
        var json = Encoding.UTF8.GetString(file.FileContents);
        Assert.DoesNotContain(adminId.ToString("N"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(adminName, json, StringComparison.Ordinal);
        Assert.Contains("admin", json, StringComparison.Ordinal);
        var adminView = Assert.IsType<PersonSettings>(Assert.IsType<OkObjectResult>(Admin(store, user).GetUserSettings(user.Id).Result).Value);
        Assert.Contains(adminId.ToString("N"), adminView.LastChangedBy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(adminName, adminView.LastChangedByName);
        Assert.Equal(adminView.LastChangedBy, store.TryGet(user.Id)!.LastChangedBy);
        Assert.Equal(adminName, store.TryGet(user.Id)!.LastChangedByName);
    }

    [Fact]
    public void AdminReadSeedsAndMigratesAsSystem()
    {
        using var host = new LocalStorageTestHost(); var user = ApiSafetyHost.Person(); var store = host.Store;
        var controller = Admin(store, user); controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "Admin") }, "Test"));
        Assert.IsType<OkObjectResult>(controller.GetUserSettings(user.Id).Result);
        Assert.Equal("system", store.TryGet(user.Id)!.LastChangedBy);
        var other = ApiSafetyHost.Person("Other", Guid.NewGuid()); var path = store.FilePath(other.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{\"schemaVersion\":1,\"userName\":\"Other\"}");
        controller = Admin(host.Store, other); controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, Guid.NewGuid().ToString()) }, "Test"));
        Assert.IsType<OkObjectResult>(controller.GetUserSettings(other.Id).Result);
        Assert.Equal("system", host.Store.TryGet(other.Id)!.LastChangedBy);
    }
}

