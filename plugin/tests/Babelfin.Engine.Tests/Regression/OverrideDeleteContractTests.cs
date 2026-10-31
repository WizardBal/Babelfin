using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class OverrideDeleteContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BareDeleteReturns405ThroughRealAspNetRoutingWithoutEditingData(bool admin, bool trailingSlash)
        => await CheckRoute(admin, trailingSlash ? "/" : string.Empty, StatusCodes.Status405MethodNotAllowed, changes: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitAllRouteRemovesOverrides(bool admin)
        => await CheckRoute(admin, "/All", StatusCodes.Status204NoContent, changes: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroGuidIs400WithoutEditingData(bool admin)
        => await CheckRoute(admin, "/" + Guid.Empty, StatusCodes.Status400BadRequest, changes: false);

    private static async Task CheckRoute(bool isAdmin, string suffix, int status, bool changes)
    {
        using var host = new LocalStorageTestHost();
        Directory.CreateDirectory(host.Folder);
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.Save(new PersonSettings { UserId = user.Id, Overrides = new() { new() { ItemId = Guid.NewGuid().ToString("N"), Settings = new SettingsLayer { Reads = new[] { "en" } } } } });
        var before = File.ReadAllBytes(store.FilePath(user.Id));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = host.Folder, EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(MeController).Assembly).AddControllersAsServices();
        builder.Services.AddSingleton(ApiSafetyHost.Me(store, user));
        builder.Services.AddSingleton(ApiSafetyHost.Admin(store, null, user));
        var elevation = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(AdminController), typeof(AuthorizeAttribute))!;
        builder.Services.AddAuthorization(options => options.AddPolicy(elevation.Policy!, policy => policy.RequireAssertion(_ => true)));
        await using var app = builder.Build();
        var pipeline = new ApplicationBuilder(app.Services);
        pipeline.UseRouting();
        pipeline.UseAuthorization();
        pipeline.UseEndpoints(endpoints => endpoints.MapControllers());
        var request = pipeline.Build();
        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = "DELETE";
        context.Request.Path = (isAdmin ? "/Babelfin/Admin/Users/" + user.Id + "/Overrides" : "/Babelfin/Me/Overrides") + suffix;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimsHelper.UserIdClaim, user.Id.ToString()) }, "Test"));
        context.Response.Body = new MemoryStream();
        await request(context);
        Assert.Equal(status, context.Response.StatusCode);
        if (changes) Assert.Empty(store.TryGet(user.Id)!.Overrides);
        else
        {
            Assert.Equal(before, File.ReadAllBytes(store.FilePath(user.Id)));
            Assert.Single(store.TryGet(user.Id)!.Overrides);
        }
    }
}
