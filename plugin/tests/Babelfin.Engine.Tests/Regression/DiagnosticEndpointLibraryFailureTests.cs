using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Admin;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Web;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class DiagnosticEndpointLibraryFailureTests
{
    [Theory]
    [InlineData("me")]
    [InlineData("explain")]
    [InlineData("simulate-item")]
    [InlineData("simulate-library")]
    public async Task FailedLibraryLookupReturnsUnavailable(string endpoint)
    {
        var user = ApiSafetyHost.Person(); var store = new ApiSafetyStore(); var itemId = Guid.NewGuid();
        store.Inner.Save(new Jellyfin.Plugin.Babelfin.Settings.PersonSettings { UserId = user.Id, Overrides = new() { new() { ItemId = itemId.ToString("N") } } });
        var library = CallProxy.Create<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetItemById" && endpoint == "simulate-library") return new Movie { Id = itemId };
            if (method.Name is "GetItemById" or "GetVirtualFolders" or "GetItemList" or "GetCollectionFolders") throw new IOException("Library unavailable");
            return null;
        }).Proxy;
        var users = ApiSafetyHost.Users(user); var sources = CallProxy.Create<IMediaSourceManager>().Proxy;
        var context = ApiSafetyHost.Me(store, user).ControllerContext;
        ControllerBase controller; Func<IActionResult?> action;
        if (endpoint == "me")
        {
            var me = new MeController(library, users, CallProxy.Create<MediaBrowser.Controller.Collections.ICollectionManager>().Proxy, store, CallProxy.Create<IProfileResolver>().Proxy, new FakeLibraryFacts(), new FakeContextBuilder()) { ControllerContext = context };
            controller = me; action = () => me.GetOverrides().Result;
        }
        else if (endpoint == "explain")
        {
            var diagnostic = new BabelfinController(library, users, sources, new DecoratorStats(), new HostSettings(), new ConfigurationBuilder().Build(), store, new FakeLibraryFacts()) { ControllerContext = context };
            controller = diagnostic; action = () => diagnostic.Explain(itemId, null);
        }
        else
        {
            var simulator = new Simulator(library, users, sources, new DecoratorStats(), NullLogger<Simulator>.Instance);
            var simulation = new SimulateController(simulator, users, library, new FakeLibraryFacts()) { ControllerContext = context };
            controller = simulation; action = () => simulation.Start(new SimulateRequest { UserId = user.Id, ItemId = itemId }).Result;
        }
        var result = Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(controller, () => Task.FromResult(action())));
        Assert.Equal(503, result.StatusCode); Assert.Equal("library-unavailable", Assert.IsType<ApiProblem>(result.Value).Code);
    }
}

