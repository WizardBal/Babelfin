using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Resolution;
using User = Jellyfin.Database.Implementations.Entities.User;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class StorageLifecycleSafetyTests
{
    // Falling back to legacy invocation makes these reproduce the lost-field bugs before the optional contract exists.
    private static object Invoke(object target, string name, object?[] legacy, string fields)
    {
        var method = target.GetType().GetMethod(name)!;
        var args = legacy.Concat(method.GetParameters().Length > legacy.Length ? new object?[] { fields } : Array.Empty<object?>()).ToArray();
        return method.Invoke(target, args)!;
    }

    private static MeController Me(ApiSafetyStore store, User user)
    {
        var library = CallProxy.Create<ILibraryManager>((method, args) => method.Name == "GetVirtualFolders" ? new List<VirtualFolderInfo>() : null).Proxy;
        var resolver = CallProxy.Create<IProfileResolver>((method, args) => method.Name == "Resolve" ? ProfileResolver.Resolve(store.TryGet(user.Id)!, new PluginConfiguration(), (ItemContext)args[1]!, LanguageTable.Default) : null).Proxy;
        return new MeController(library, ApiSafetyHost.Users(user), CallProxy.Create<ICollectionManager>().Proxy, store, resolver, new FakeLibraryFacts(), new FakeContextBuilder())
        { ControllerContext = ApiSafetyHost.Me(store, user).ControllerContext };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldIntentKeepsUneditedAndConcurrentFieldsAndHonorsNull(bool admin)
    {
        var user = ApiSafetyHost.Person();
        var store = new ApiSafetyStore();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Everyday = new SettingsLayer { Reads = new[] { "en" }, AvoidCommentary = false }, HandleNextEpisode = true });
        var body = store.TryGet(user.Id)!.Clone();
        body.Everyday.Reads = null;
        body.Everyday.AvoidCommentary = null; // Hidden control absent from this screen.
        body.HandleNextEpisode = false; // Stale cloned body.
        store.BeforeUpdate = () => store.Inner.Update(user.Id, current =>
        {
            var copy = current!.Clone(); copy.Everyday.Understood = new[] { "ja" }; copy.Everyday.AvoidCommentary = true;
            copy.BurnedIn.Add(new BurnedInFlag { ItemId = Guid.NewGuid().ToString("N"), Languages = new List<string> { "en" } });
            return copy;
        });
        if (admin)
            Assert.IsType<OkObjectResult>((await (Task<ActionResult<PersonSettings>>)Invoke(ApiSafetyHost.Admin(store, null, user), "PutUserSettings", new object?[] { user.Id, body, true }, "everyday.reads")).Result);
        else
            Assert.IsType<OkObjectResult>((await (Task<ActionResult<MeSettingsResponse>>)Invoke(Me(store, user), "PutSettings", new object?[] { body, true }, "everyday.reads")).Result);
        var saved = store.TryGet(user.Id)!;
        Assert.Null(saved.Everyday.Reads);
        Assert.True(saved.Everyday.AvoidCommentary);
        Assert.Equal(new[] { "ja" }, saved.Everyday.Understood);
        Assert.True(saved.HandleNextEpisode);
        Assert.Single(saved.BurnedIn);
    }

    [Theory]
    [InlineData("userId")]
    [InlineData("everyday.typo")]
    [InlineData("locks.avoidCommentary")]
    [InlineData("")]
    public async Task PersonalFieldIntentRejectsInvalidOrServerOwnedPaths(string fields)
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings { UserId = user.Id });
        var response = await (Task<ActionResult<MeSettingsResponse>>)Invoke(Me(store, user), "PutSettings", new object?[] { new PersonSettings(), true }, fields);
        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(0, store.Updates);
    }

    [Fact]
    public void AdminFieldIntentPreservesHiddenConfigurationAndExplicitDefault()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, ServedWindowMinutes = 45, Everyday = new SettingsLayer { Reads = new[] { "en" }, AvoidCommentary = false } });
        var stale = new PluginConfiguration { Everyday = new SettingsLayer { Reads = null } };
        var controller = ApiSafetyHost.Admin(host.Store);
        Assert.IsType<OkObjectResult>(((ActionResult<PluginConfiguration>)Invoke(controller, "PutSettings", new object?[] { stale }, "everyday.reads")).Result);
        Assert.Null(plugin.Configuration.Everyday.Reads);
        Assert.False(plugin.Configuration.Everyday.AvoidCommentary);
        Assert.Equal(45, plugin.Configuration.ServedWindowMinutes);
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedEpisodeBuildCachesActualSeriesLookupAndKeepsInheritance(bool ownLanguage)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Series", Genres = new[] { "Inherited genre" }, OriginalLanguage = "ja" };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Series", OriginalLanguage = ownLanguage ? "en" : null };
        var calls = 0;
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetItemById") { calls++; return series; }
            if (method.Name == "GetCollectionFolders") return new List<Folder>();
            return null;
        }).Proxy;
        var previous = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = library;
            var builder = new ItemContextBuilder(library, CallProxy.Create<ICollectionManager>().Proxy, new FakeLibraryFacts());
            for (var i = 0; i < 20; i++)
            {
                var result = builder.Build(episode, ApiSafetyHost.Person());
                Assert.Contains("Inherited genre", result.Genres);
                Assert.Equal(ownLanguage ? "en" : "ja", result.OriginalLanguage);
            }
            Assert.InRange(calls, 1, 2);
        }
        finally { BaseItem.LibraryManager = previous; }
    }
    [Fact]
    public void SeriesMetadataChangeInvalidatesBuilderCacheAndDisposeUnsubscribes()
    {
        var series = new Series { Id = Guid.NewGuid(), Genres = new[] { "Before" }, OriginalLanguage = "ja" };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Series" };
        EventHandler<ItemChangeEventArgs>? updated = null;
        var library = CallProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "add_ItemUpdated") updated += (EventHandler<ItemChangeEventArgs>)args[0]!;
            if (method.Name == "remove_ItemUpdated") updated -= (EventHandler<ItemChangeEventArgs>)args[0]!;
            if (method.Name == "GetItemById") return series;
            if (method.Name == "GetCollectionFolders") return new List<Folder>();
            return null;
        }).Proxy;
        var previous = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = library;
            var builder = new ItemContextBuilder(library, CallProxy.Create<ICollectionManager>().Proxy, new FakeLibraryFacts());
            Assert.Contains("Before", builder.Build(episode, ApiSafetyHost.Person()).Genres);
            series = new Series { Id = series.Id, Genres = new[] { "After" }, OriginalLanguage = "fr" };
            updated?.Invoke(null, new ItemChangeEventArgs { Item = series });
            var result = builder.Build(episode, ApiSafetyHost.Person());
            Assert.Contains("After", result.Genres);
            Assert.Equal("fr", result.OriginalLanguage);
            Assert.IsAssignableFrom<IDisposable>(builder).Dispose();
            Assert.Null(updated);
        }
        finally { BaseItem.LibraryManager = previous; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("en")]
    public void BuilderOriginalLanguageMatchesJellyfinForEmptyWhitespaceAndExplicitMetadata(string? language)
    {
        var series = new Series { Id = Guid.NewGuid(), OriginalLanguage = "ja" };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, OriginalLanguage = language };
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => series, "GetCollectionFolders" => new List<Folder>(), _ => null,
        }).Proxy;
        var previous = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = library;
            var expected = episode.GetInheritedOriginalLanguage();
            var builder = new ItemContextBuilder(library, CallProxy.Create<ICollectionManager>().Proxy, new FakeLibraryFacts());
            Assert.Equal(expected, builder.Build(episode, ApiSafetyHost.Person()).OriginalLanguage);
        }
        finally { BaseItem.LibraryManager = previous; }
    }

}
