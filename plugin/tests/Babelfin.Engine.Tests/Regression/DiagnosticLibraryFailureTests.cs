using System;
using System.Collections.Generic;
using System.IO;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Web;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class DiagnosticLibraryFailureTests
{
    [Fact]
    public void ChoiceStatusReportsUnavailableInsteadOfClaimingNoSavedChoicesAndCanRecover()
    {
        var user = ApiSafetyHost.Person(); var store = new ApiSafetyStore(); var fail = true;
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name == "GetVirtualFolders"
            ? fail ? throw new IOException("Library query failed") : new List<VirtualFolderInfo>() : null).Proxy;
        var controller = new ChoiceStatusController(store, ApiSafetyHost.Users(user), library)
            { ControllerContext = ApiSafetyHost.Me(store, user).ControllerContext };
        var unavailable = Assert.IsType<ObjectResult>(controller.Me().Result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.IsType<ApiProblem>(unavailable.Value);
        Assert.Equal(0, store.Updates);
        fail = false;
        Assert.IsType<ChoiceStatus>(Assert.IsType<OkObjectResult>(controller.Me().Result).Value);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("episode")]
    [InlineData("sources")]
    public void ChoiceReportsUnavailableForFailedLookupAndRecoversWithoutCachingFailure(string phase)
    {
        var user = ApiSafetyHost.Person(); var store = new ApiSafetyStore(); var fail = true;
        var id = Guid.NewGuid();
        var movie = new Movie { Id = id, Name = "Example" };
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => fail && phase == "item" ? throw new IOException("Library query failed") : phase == "episode" ? new Series { Id = id } : movie,
            "GetItemList" => fail ? throw new IOException("Episode query failed") : new BaseItem[] { movie },
            _ => null,
        }).Proxy;
        var sources = CallProxy.Create<IMediaSourceManager>((method, _) => method.Name == "GetStaticMediaSources" && fail && phase == "sources" ? throw new IOException("Source query failed") : null).Proxy;
        var controller = new BabelfinController(library, ApiSafetyHost.Users(user), sources, new DecoratorStats(), new HostSettings(), new ConfigurationBuilder().Build(), store, new FakeLibraryFacts())
            { ControllerContext = ApiSafetyHost.Me(store, user).ControllerContext };
        var unavailable = Assert.IsType<ObjectResult>(controller.Choice(id));
        Assert.Equal(503, unavailable.StatusCode);
        Assert.IsType<ApiProblem>(unavailable.Value);
        Assert.Equal(0, store.Updates);
        fail = false;
        Assert.IsType<OkObjectResult>(controller.Choice(id));
    }
}
