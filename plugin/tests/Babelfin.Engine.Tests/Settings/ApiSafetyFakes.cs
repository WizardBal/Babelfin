using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Babelfin.Engine.Tests.Settings;

internal sealed class ApiSafetyStore : IPersonStore
{
    internal readonly FakePersonStore Inner = new();
    internal Action? BeforeUpdate;
    internal readonly HashSet<Guid> Failing = new();
    internal int Updates;
    public event EventHandler<PersonStoreChangedEventArgs>? Changed { add => Inner.Changed += value; remove => Inner.Changed -= value; }
    public string DataFolder => Inner.DataFolder;
    public PersonSettings? TryGet(Guid id) => Inner.TryGet(id);
    public PersonSettings GetOrCreate(Guid id, Func<PersonSettings> create) => Inner.GetOrCreate(id, create);
    public void Save(PersonSettings settings) => throw new InvalidOperationException("API mutations must use Update");
    public bool Update(Guid id, Func<PersonSettings?, PersonSettings?> change)
    {
        Updates++;
        var pending = BeforeUpdate;
        BeforeUpdate = null;
        pending?.Invoke();
        if (Failing.Contains(id))
        {
            throw new IOException("Test disk failure");
        }

        return Inner.Update(id, change);
    }

    public bool Delete(Guid id) => Inner.Delete(id);
    public IReadOnlyList<Guid> UserIds() => Inner.UserIds();
}

internal sealed class ApiSafetyFlags : IRememberFlags
{
    internal readonly List<Guid> Applied = new();
    internal readonly List<Guid> Restored = new();
    internal bool FailRestore;
    public Task ApplyAsync(User user, CancellationToken token) { Applied.Add(user.Id); return Task.CompletedTask; }
    public Task RestoreAsync(User user, CancellationToken token)
    {
        if (FailRestore) { throw new IOException("Test restore failure"); }
        Restored.Add(user.Id);
        return Task.CompletedTask;
    }

    public void StopWrites() { }
    public Task RestoreAllAsync(CancellationToken token) => Task.CompletedTask;
}

internal static class ApiSafetyHost
{
    internal static User Person(string name = "Alice", Guid? id = null) => new(name, "Default", "Default") { Id = id ?? Ids.Person };
    internal static IUserManager Users(params User[] users) => CallProxy.Create<IUserManager>((method, args) => method.Name switch
    {
        "GetUserById" => users.FirstOrDefault(u => u.Id == (Guid)args[0]!),
        _ => null,
    }).Proxy;

    internal static ILibraryManager Library() => CallProxy.Create<ILibraryManager>((method, args) => method.Name switch
    {
        "GetItemById" => new Movie { Id = (Guid)args[0]!, Name = "A movie" },
        _ => null,
    }).Proxy;

    internal static AdminController Admin(IPersonStore store, IRememberFlags? flags = null, params User[] users)
        => new(Library(), Users(users), store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy, rememberFlags: flags);

    internal static MeController Me(IPersonStore store, User user, ApiSafetyFlags? flags = null)
    {
        var controller = new MeController(Library(), Users(user), CallProxy.Create<ICollectionManager>().Proxy, store,
            CallProxy.Create<IProfileResolver>().Proxy, new FakeLibraryFacts(), new FakeContextBuilder(), rememberFlags: flags);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, user.Id.ToString()) }, "Test")),
            },
        };
        return controller;
    }
}
