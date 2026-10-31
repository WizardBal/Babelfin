using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class LibraryFailureApiTests
{
    private sealed class LibraryDatabaseException : DbException { }
    [Theory]
    [InlineData("overrides", false)]
    [InlineData("overrides", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("approve", false)]
    [InlineData("approve", true)]
    [InlineData("libraries", false)]
    [InlineData("libraries", true)]
    public async Task LibraryFailuresReturnUnavailableAndDeleteIsAtomic(string endpoint, bool io)
    {
        using var host = new LocalStorageTestHost(); var store = host.Store; var user = ApiSafetyHost.Person(); var item = Guid.NewGuid();
        host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        store.GetOrCreate(user.Id, () => new PersonSettings { Overrides = new() { new ItemOverride { ItemId = item.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "en" } } } }, BurnedIn = new() { new BurnedInFlag { ItemId = item.ToString("N"), Languages = new List<string> { "en" } } } });
        var library = CallProxy.Create<ILibraryManager>((method, _) =>
        {
            if (method.Name is "GetItemById" or "GetVirtualFolders" or "GetItemList" or "GetCollectionFolders")
                throw io ? new IOException("Library unavailable") : new LibraryDatabaseException();
            return null;
        }).Proxy;
        var admin = new AdminController(library, ApiSafetyHost.Users(user), store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy, null);
        admin.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        IActionResult? Action() => endpoint switch
        {
            "overrides" => admin.GetUserOverrides(user.Id).Result,
            "delete" => admin.DeleteUserOverride(user.Id, item, null),
            "approve" => admin.ApproveBurnedIn(user.Id, item, null).Result,
            _ => admin.GetLibraries().Result,
        };
        var response = Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(admin, () => Task.FromResult(Action())));
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("library-unavailable", Assert.IsType<ApiProblem>(response.Value).Code);
        Assert.Single(store.TryGet(user.Id)!.Overrides);
    }

    [Fact]
    public async Task ConcurrentAdditionAtLimitStillConflicts()
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        var existing = Enumerable.Range(0, 199).Select(_ => new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "en" } }).ToList();
        store.Inner.Save(new PersonSettings { UserId = user.Id, BurnedIn = existing });
        var body = new PersonSettings { BurnedIn = existing.Select(f => f.Clone()).Concat(new[] { new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "en" } } }).ToList() };
        store.BeforeUpdate = () => store.Inner.Update(user.Id, current => { var copy = current!.Clone(); copy.BurnedIn.Add(new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "fr" } }); return copy; });
        var me = ApiSafetyHost.Me(store, user);
        var result = Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(me, async () => (await me.PutSettings(body, false)).Result));
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(200, store.TryGet(user.Id)!.BurnedIn.Count);
    }
}
