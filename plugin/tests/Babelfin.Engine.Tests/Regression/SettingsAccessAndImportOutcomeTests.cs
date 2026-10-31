using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class SettingsAccessAndImportOutcomeTests
{
    private static IProfileResolver Resolver(IPersonStore store) => CallProxy.Create<IProfileResolver>((method,args)=>method.Name=="Resolve"
        ? Jellyfin.Plugin.Babelfin.Resolution.ProfileResolver.Resolve(store.TryGet(((Jellyfin.Database.Implementations.Entities.User)args[0]!).Id)!,new PluginConfiguration(),(ItemContext)args[1]!,LanguageTable.Default)
        : null).Proxy;
    private static MeController Me(IPersonStore store,Jellyfin.Database.Implementations.Entities.User user) => new(
        CallProxy.Create<ILibraryManager>((method,_)=>method.Name=="GetVirtualFolders"?new List<MediaBrowser.Model.Entities.VirtualFolderInfo>():null).Proxy,
        ApiSafetyHost.Users(user),CallProxy.Create<ICollectionManager>().Proxy,store,Resolver(store),new FakeLibraryFacts(),new FakeContextBuilder())
        {ControllerContext=ApiSafetyHost.Me(store,user).ControllerContext};
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UneditedMalformedStoredReferenceDoesNotBlockAnEverydayEdit(bool admin, bool collection)
    {
        using var host = new LocalStorageTestHost();
        var store=host.Store; var user=ApiSafetyHost.Person();
        store.GetOrCreate(user.Id,()=>new PersonSettings {
            Libraries=collection ? new() : new() {new() {LibraryId="broken-old-id"}},
            CollectionOrder=collection ? new() {"broken-old-id"} : new(),
        });
        var incoming=new PersonSettings {Everyday=new SettingsLayer {Reads=new[]{"en"}}};
        var result=admin ? (await ApiSafetyHost.Admin(store,null,user).PutUserSettings(user.Id,incoming,true,"everyday.reads")).Result
            : (await Me(store,user).PutSettings(incoming,true,"everyday.reads")).Result;
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[]{"en"},store.TryGet(user.Id)!.Everyday.Reads);
        if (collection) Assert.Equal("broken-old-id", store.TryGet(user.Id)!.CollectionOrder.Single());
        else Assert.Empty(store.TryGet(user.Id)!.Libraries);
    }

    [Fact]
    public void KeyedLibraryLeafDoesNotValidateStoredName()
    {
        var id=Guid.NewGuid();
        var document=new PersonSettings {Libraries=new() {new() {LibraryId=id.ToString("N"),Name=new string('x',201),Settings=new SettingsLayer {Reads=new[]{"en"}}}}};
        Assert.True(SettingsIntent.TryParse("libraries."+id.ToString("N")+".settings.reads",true,false,out var intent,out _));
        Assert.Empty(intent!.Validate(document,LanguageTable.Default,true,document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidUneditedLanguageSiblingsArePreservedButBadEditReturns400(bool admin)
    {
        var store=new ApiSafetyStore(); var user=ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings {UserId=user.Id,Everyday=new SettingsLayer {Understood=new[]{"retired-alias"}},
            Overrides=new(){new(){ItemId=Guid.NewGuid().ToString("N"),Settings=new SettingsLayer {Reads=new[]{"retired-alias"}}}},
            BurnedIn=new(){new(){ItemId=Guid.NewGuid().ToString("N"),Languages=new(){"retired-alias"}}}});
        async Task<IActionResult?> Save(string language) {
            var body=new PersonSettings {Everyday=new SettingsLayer {Reads=new[]{language}}};
            return admin ? (await ApiSafetyHost.Admin(store,null,user).PutUserSettings(user.Id,body,true,"everyday.reads")).Result
                : (await Me(store,user).PutSettings(body,true,"everyday.reads")).Result;
        }
        Assert.IsType<OkObjectResult>(await Save("en"));
        Assert.Equal("retired-alias",store.TryGet(user.Id)!.Everyday.Understood![0]);
        Assert.Equal("retired-alias",store.TryGet(user.Id)!.Overrides.Single().Settings.Reads![0]);
        Assert.Equal("retired-alias",store.TryGet(user.Id)!.BurnedIn.Single().Languages[0]);
        Assert.IsType<BadRequestObjectResult>(await Save("another-unknown-alias"));
        Assert.Equal(new[]{"en"},store.TryGet(user.Id)!.Everyday.Reads);
    }

    [Fact]
    public async Task DependencyInvalidatedBetweenValidationAndSaveReturns400()
    {
        var store=new ApiSafetyStore(); var user=ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings {UserId=user.Id,Everyday=new SettingsLayer {UnlabelledAudioLanguage="en"}});
        store.BeforeUpdate=()=>store.Inner.Update(user.Id,current=>{var copy=current!.Clone();copy.Everyday.UnlabelledAudioLanguage="removed-alias";return copy;});
        var admin=ApiSafetyHost.Admin(store,null,user);
        var result=await StorageApiSafetyTests.InvokeApi(admin,async()=>(await admin.PutUserSettings(user.Id,
            new PersonSettings {Everyday=new SettingsLayer {UnlabelledAudio=UnlabelledAudioRule.SpecificLanguage}},true,"everyday.unlabelledAudio")).Result);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SeriesUpdateDuringLookupCannotRepopulateOldMetadata()
    {
        var old=new Series {Id=Guid.NewGuid(),OriginalLanguage="ja",Genres=new[]{"Before"}};
        var latest=new Series {Id=old.Id,OriginalLanguage="fr",Genres=new[]{"After"}};
        EventHandler<ItemChangeEventArgs>? updated=null; int calls=0;
        var library=CallProxy.Create<ILibraryManager>((method,args)=>{
            if(method.Name=="add_ItemUpdated") updated+=(EventHandler<ItemChangeEventArgs>)args[0]!;
            if(method.Name=="remove_ItemUpdated") updated-=(EventHandler<ItemChangeEventArgs>)args[0]!;
            if(method.Name=="GetCollectionFolders") return new List<Folder>();
            if(method.Name=="GetItemById") {
                if(++calls==1) {updated?.Invoke(null,new ItemChangeEventArgs {Item=latest});return old;}
                return latest;
            }
            return null;
        }).Proxy;
        var previous=BaseItem.LibraryManager;
        try {
            BaseItem.LibraryManager=library;
            using var builder=new ItemContextBuilder(library,CallProxy.Create<ICollectionManager>().Proxy,new FakeLibraryFacts());
            var episode=new Episode {Id=Guid.NewGuid(),SeriesId=old.Id}; var user=ApiSafetyHost.Person();
            builder.Build(episode,user);
            Assert.Contains("After",builder.Build(episode,user).Genres);
            Assert.Equal("fr",builder.Build(episode,user).OriginalLanguage);
            Assert.Equal(2,calls);
        } finally {BaseItem.LibraryManager=previous;}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeLibraryFailuresReturnAnActionableResult(bool settings)
    {
        var store=new ApiSafetyStore();var user=ApiSafetyHost.Person();
        store.Inner.Save(new PersonSettings {UserId=user.Id});
        var library=CallProxy.Create<ILibraryManager>((method,_)=>method.Name=="GetVirtualFolders" ? throw new InvalidOperationException("Temporary database failure") : null).Proxy;
        var me=new MeController(library,ApiSafetyHost.Users(user),CallProxy.Create<ICollectionManager>().Proxy,store,
            Resolver(store),new FakeLibraryFacts(),new FakeContextBuilder()) {ControllerContext=ApiSafetyHost.Me(store,user).ControllerContext};
        var result=await StorageApiSafetyTests.InvokeApi(me,()=>Task.FromResult<IActionResult?>(settings?me.GetSettings().Result:me.GetLibraries().Result));
        var status=Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(503,status.StatusCode);
        Assert.Contains("retry",Assert.IsType<ApiProblem>(status.Value).Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PartialImport409IncludesAppliedFlag()
    {
        using var host=new LocalStorageTestHost();host.CreatePlugin(new PluginConfiguration {ConfigVersion=PluginConfiguration.CurrentVersion});
        var inner=host.Store;var first=ApiSafetyHost.Person();var second=ApiSafetyHost.Person("Second",Guid.NewGuid());
        inner.GetOrCreate(first.Id,()=>new PersonSettings {UserName=first.Username});
        inner.GetOrCreate(second.Id,()=>new PersonSettings {UserName=second.Username});
        var store=new ConcurrentUpdateHookStore(inner);
        store.BeforeUpdate=()=>File.WriteAllText(inner.FilePath(second.Id),"{\"schemaVersion\":999}");
        var users=CallProxy.Create<IUserManager>((method,args)=>method.Name switch {"GetUsers"=>new[]{first,second},"GetUserById"=>(Guid)args[0]! ==first.Id?first:second,_=>null}).Proxy;
        var admin=new AdminController(ApiSafetyHost.Library(),users,store,new FakeLibraryFacts(),CallProxy.Create<IProfileResolver>().Proxy);
        var document=new ExportDocument {Users=new(){new(){UserId=first.Id,Enabled=false},new(){UserId=second.Id,Enabled=false}}};
        var result=Assert.IsAssignableFrom<ObjectResult>((await admin.Import(document,false)).Result);
        Assert.Equal(409,result.StatusCode);Assert.False(inner.TryGet(first.Id)!.Enabled);
        var json=JsonSerializer.SerializeToElement(result.Value,BabelfinJson.Options);
        Assert.True(json.TryGetProperty("applied",out var applied),json.ToString());
        Assert.True(applied.GetBoolean());
        var outcome = Assert.IsType<ApiProblem>(result.Value).Import;
        Assert.NotNull(outcome); Assert.True(outcome.Applied);
        Assert.Contains(outcome.Users, row => row.UserId == first.Id && row.Action == ImportUserPreview.Update);
        Assert.Contains(outcome.Users, row => row.UserId == second.Id && row.Action == ImportUserPreview.Skip && row.Reason!.Contains("Upgrade Babelfin", StringComparison.Ordinal));
        Assert.Equal("{\"schemaVersion\":999}", File.ReadAllText(inner.FilePath(second.Id)));
    }
    [Theory]
    [InlineData(false, "library")]
    [InlineData(true, "library")]
    [InlineData(false, "collection")]
    [InlineData(true, "collection")]
    [InlineData(false, "keyed")]
    [InlineData(true, "keyed")]
    public async Task NewlyEditedInvisibleReferencesAreStillDenied(bool admin, string kind)
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person(); var hidden = Guid.NewGuid();
        store.Inner.Save(new PersonSettings { UserId = user.Id, Libraries = new() { new LibraryLayer { LibraryId = "broken-old-id" } }, CollectionOrder = new() { "broken-old-id" } });
        var before = JsonSerializer.Serialize(store.TryGet(user.Id), BabelfinJson.Options);
        var library = CallProxy.Create<ILibraryManager>((method, _) => method.Name == "GetVirtualFolders" ? new List<MediaBrowser.Model.Entities.VirtualFolderInfo>() : null).Proxy;
        var body = new PersonSettings { Libraries = new() { new LibraryLayer { LibraryId = hidden.ToString("N"), Settings = new SettingsLayer { Reads = new[] { "en" } } } }, CollectionOrder = new() { hidden.ToString("N") } };
        var fields = kind == "collection" ? "collectionOrder" : kind == "keyed" ? $"libraries.{hidden:N}.settings.reads" : "libraries";
        IActionResult? result;
        if (admin) result = (await new AdminController(library, ApiSafetyHost.Users(user), store, new FakeLibraryFacts(), Resolver(store)).PutUserSettings(user.Id, body, true, fields)).Result;
        else
        {
            var me = new MeController(library, ApiSafetyHost.Users(user), CallProxy.Create<ICollectionManager>().Proxy, store, Resolver(store), new FakeLibraryFacts(), new FakeContextBuilder())
                { ControllerContext = ApiSafetyHost.Me(store, user).ControllerContext };
            result = (await me.PutSettings(body, true, fields)).Result;
        }
        var problem = Assert.IsType<ApiProblem>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains(hidden.ToString("N"), problem.ItemIds!);
        Assert.Equal(before, JsonSerializer.Serialize(store.TryGet(user.Id), BabelfinJson.Options)); Assert.Equal(0, store.Updates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewlyEditedMalformedReferenceReturnsValidationError(bool collection)
    {
        var store = new ApiSafetyStore(); var user = ApiSafetyHost.Person(); store.Inner.Save(new PersonSettings { UserId = user.Id });
        var body = new PersonSettings { Libraries = new() { new LibraryLayer { LibraryId = "broken-new-id" } }, CollectionOrder = new() { "broken-new-id" } };
        var result = await ApiSafetyHost.Admin(store, null, user).PutUserSettings(user.Id, body, true, collection ? "collectionOrder" : "libraries");
        var problem = Assert.IsType<ApiProblem>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.NotEmpty(problem.Errors); Assert.Equal(0, store.Updates);
    }

    [Fact]
    public async Task RefusedImportExplicitlyReportsNoSettingsWereApplied()
    {
        using var host = new LocalStorageTestHost(); host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store; var healthy = ApiSafetyHost.Person(); var future = ApiSafetyHost.Person("Future", Guid.NewGuid());
        store.GetOrCreate(healthy.Id, () => new PersonSettings { UserName = healthy.Username });
        var before = File.ReadAllBytes(store.FilePath(healthy.Id)); var raw = "{\"schemaVersion\":999,\"futureValue\":true}";
        File.WriteAllText(store.FilePath(future.Id), raw);
        var users = CallProxy.Create<IUserManager>((method, args) => method.Name switch
            { "GetUsers" => new[] { healthy, future }, "GetUserById" => (Guid)args[0]! == healthy.Id ? healthy : future, _ => null }).Proxy;
        var admin = new AdminController(ApiSafetyHost.Library(), users, store, new FakeLibraryFacts(), Resolver(store));
        var result = Assert.IsAssignableFrom<ObjectResult>((await admin.Import(new ExportDocument { Users = new()
            { new PersonSettings { UserId = healthy.Id, Enabled = false }, new PersonSettings { UserId = future.Id, Enabled = false } } }, false)).Result);
        Assert.Equal(409, result.StatusCode); var problem = Assert.IsType<ApiProblem>(result.Value);
        Assert.False(problem.Applied); Assert.NotNull(problem.Import); Assert.False(problem.Import.Applied);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath(healthy.Id))); Assert.Equal(raw, File.ReadAllText(store.FilePath(future.Id)));
    }

    [Fact]
    public void LibraryUnavailableFilterLogsTheOriginalFailureAndDoesNotExposeIt()
    {
        var logger = new StorageTestLogger<StorageApiErrorsAttribute>();
        using var services = new ServiceCollection().AddSingleton<ILogger<StorageApiErrorsAttribute>>(logger).BuildServiceProvider();
        var original = new InvalidOperationException("Database failure detail");
        var error = Assert.Throws<LibraryUnavailableException>(() => LibraryQueries.Read<int>(() => throw original));
        var http = new DefaultHttpContext { RequestServices = services };
        var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = error };
        new StorageApiErrorsAttribute().OnException(context);
        Assert.True(context.ExceptionHandled); Assert.Equal(503, Assert.IsType<ObjectResult>(context.Result).StatusCode);
        var problem = Assert.IsType<ApiProblem>(((ObjectResult)context.Result!).Value);
        Assert.Contains("retry", problem.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(original.Message, problem.Message, StringComparison.Ordinal);
        Assert.Same(original, Assert.Single(logger.Entries).Error!.InnerException);
    }

}
