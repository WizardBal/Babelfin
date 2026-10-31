using System;
using System.IO;
using System.Linq;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Admin;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Compat;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Library;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Seeding;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Web;

/// <summary>
/// Called by Jellyfin at startup, after the core services are registered and before the web host is built.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Jellyfin reads these once at startup to build its /web pipeline; do the same.
        serviceCollection.AddSingleton(HostSettings.Read(applicationHost));

        var stats = new DecoratorStats();
        serviceCollection.AddSingleton(stats);
        // The engine decider: the engine fed by each person's resolved profile.
        serviceCollection.AddSingleton<IProfileResolver>(sp => new CachedProfileResolver(
            sp.GetRequiredService<IPersonStore>(),
            sp.GetRequiredService<IItemContextBuilder>(),
            sp.GetRequiredService<ILibraryFacts>(),
            sp.GetRequiredService<ILibraryManager>(),
            sp.GetService<IServerConfigurationManager>(),
            sp.GetRequiredService<ILogger<CachedProfileResolver>>()));
        serviceCollection.AddSingleton<ITrackDecider, EngineTrackDecider>();

        // per-person files, library facts, item context and the new-user template.
        serviceCollection.AddSingleton<IPersonStore>(sp => new PersonStore(
            DataFolder(sp, sp.GetRequiredService<ILogger<PersonStore>>()),
            sp.GetRequiredService<ILogger<PersonStore>>()));
        serviceCollection.AddSingleton<ILibraryFacts>(sp => new LibraryFacts(
            DataFolder(sp, sp.GetRequiredService<ILogger<LibraryFacts>>()),
            sp.GetRequiredService<ILibraryManager>(),
            sp.GetRequiredService<ILogger<LibraryFacts>>()));
        serviceCollection.AddSingleton<IItemContextBuilder, ItemContextBuilder>();

        // activity log, compatibility mode, the person's remember-flags switch.
        serviceCollection.AddSingleton<ActivityLog>();
        serviceCollection.AddSingleton<IActivityLog>(sp => sp.GetRequiredService<ActivityLog>());
        serviceCollection.AddSingleton<UserDataLifecycle>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<UserDataLifecycle>());
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>>(sp => sp.GetRequiredService<UserDataLifecycle>());
        serviceCollection.AddSingleton<ICompatibilityWriter>(sp => new CompatibilityWriter(
            DataFolder(sp, sp.GetRequiredService<ILogger<CompatibilityWriter>>()),
            sp.GetRequiredService<IUserDataManager>(),
            sp.GetRequiredService<IUserManager>(),
            sp.GetRequiredService<ILibraryManager>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<ILogger<CompatibilityWriter>>()));
        serviceCollection.AddSingleton<IRememberFlags, RememberFlags>();

        // the play registry, the recorder of manual track changes and the playback monitor (events subscribed in StartAsync).
        serviceCollection.AddSingleton<PlayRegistry>();
        serviceCollection.AddSingleton<IPlayRegistry>(sp => sp.GetRequiredService<PlayRegistry>());
        serviceCollection.AddSingleton<TrackChangeRecorder>();
        // Hosted services stop in reverse order: stop playback subscriptions before restoring disabled-plugin changes.
        serviceCollection.AddHostedService<PluginDisableCleanup>();
        serviceCollection.AddHostedService<PlaybackMonitor>();

        // the admin simulator (one job at a time).
        serviceCollection.AddSingleton<Simulator>();
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>>(sp => new NewUserTemplateConsumer(
            sp.GetRequiredService<IPersonStore>(),
            sp.GetService<IServerConfigurationManager>(),
            sp.GetRequiredService<ILogger<NewUserTemplateConsumer>>(),
            sp.GetRequiredService<IUserManager>()));

        WrapMediaSourceManager(serviceCollection, stats);

        // Outermost middleware, whatever order the plugins load in.
        serviceCollection.Insert(0, ServiceDescriptor.Transient<IStartupFilter, BabelfinStartupFilter>());

        // No plugin graph is resolved by MVC before the guarded filter executes.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add(new GuardedPlaybackInfoFilter()));
    }

    /// <summary>
    /// Replaces the registered <see cref="IMediaSourceManager"/> with <see cref="BabelfinMediaSourceManager"/>
    /// wrapping whatever was registered last (Jellyfin's own, or another plugin's wrapper).
    /// Afterwards exactly one non-keyed registration exists.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="stats">Babelfin's counters.</param>
    /// <returns>True when the manager was wrapped.</returns>
    internal static bool WrapMediaSourceManager(IServiceCollection services, DecoratorStats stats)
    {
        var existing = services.Where(d => d.ServiceType == typeof(IMediaSourceManager) && !d.IsKeyedService).ToList();
        if (existing.Count == 0)
        {
            // Nothing to wrap; BabelfinStartupFilter logs a warning once logging is up.
            return false;
        }

        var descriptor = existing[^1];
        foreach (var d in existing)
        {
            services.Remove(d);
        }

        services.Add(ServiceDescriptor.Describe(
            typeof(IMediaSourceManager),
            sp =>
            {
                var inner = CreateOriginal(sp, descriptor);
                stats.MarkWrapped(inner.GetType().FullName ?? inner.GetType().Name);
                // Only the inner manager is created here. The decider is resolved on the first decision: its graph needs
                // IMediaSourceManager (through ICollectionManager → IProviderManager → ISubtitleManager), so resolving it in this
                // factory is a circular dependency that stops Jellyfin from starting.
                return new BabelfinMediaSourceManager(
                    inner,
                    new LazyTrackDecider(sp),
                    stats,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<BabelfinMediaSourceManager>());
            },
            descriptor.Lifetime));

        var name = descriptor.ImplementationType?.FullName
            ?? descriptor.ImplementationInstance?.GetType().FullName
            ?? "(factory)";
        stats.MarkWrapped(name);
        return true;
    }

    /// <summary>
    /// Babelfin's data folder: <c>{DataPath}/babelfin</c> of the running plugin, else of the host's application paths, else
    /// (test hosts without either) a folder under the system temp folder, with a warning.
    /// </summary>
    /// <param name="sp">The service provider.</param>
    /// <param name="logger">Logger for the fallback warning.</param>
    /// <returns>The folder.</returns>
    internal static string DataFolder(IServiceProvider sp, ILogger logger)
    {
        var dataPath = Plugin.Instance?.Paths.DataPath ?? sp.GetService<IApplicationPaths>()?.DataPath;
        if (!string.IsNullOrEmpty(dataPath))
        {
            return Path.Combine(dataPath, "babelfin");
        }

        var fallback = Path.Combine(Path.GetTempPath(), "babelfin-data");
        logger.LogWarning("[Babelfin] No server data folder is known (no plugin instance); settings are kept in {Folder}", fallback);
        return fallback;
    }

    private static IMediaSourceManager CreateOriginal(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is not null)
        {
            return (IMediaSourceManager)descriptor.ImplementationInstance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IMediaSourceManager)descriptor.ImplementationFactory(sp);
        }

        if (descriptor.ImplementationType is not null)
        {
            return (IMediaSourceManager)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }

        throw new InvalidOperationException("The IMediaSourceManager registration has no implementation");
    }
}


/// <summary>Defers the playback dependency graph until guarded execution and records only successful response sources.</summary>
public sealed class GuardedPlaybackInfoFilter : IAsyncActionFilter, IAsyncResultFilter
{
    /// <inheritdoc />
    public async System.Threading.Tasks.Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        PlaybackInfoFilter? filter = null;
        try
        {
            if (context.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor action
                && action.ActionName == "GetPostedPlaybackInfo" && action.ControllerName == "MediaInfo")
            {
                filter = ActivatorUtilities.CreateInstance<PlaybackInfoFilter>(context.HttpContext.RequestServices);
            }
        }
        catch (Exception ex)
        {
            LogFailure(context.HttpContext.RequestServices, ex);
        }

        if (filter is not null)
        {
            await filter.OnActionExecutionAsync(context, next).ConfigureAwait(false);
        }
        else
        {
            await next().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var result = await next().ConfigureAwait(false);
        if (result.Canceled || result.Exception is not null || context.HttpContext.Response.StatusCode >= 400)
        {
            return;
        }

        try
        {
            var userId = ClaimsHelper.GetUserId(context.HttpContext.User);
            if (userId.HasValue)
            {
                var value = context.Result is ObjectResult obj ? obj.Value : (context.Result as JsonResult)?.Value;
                context.HttpContext.RequestServices.GetService<DecoratorStats>()?.RecordResponse(userId.Value, value,
                    ClaimsHelper.GetDeviceId(context.HttpContext.User), ClaimsHelper.GetClient(context.HttpContext.User));
            }
        }
        catch (Exception ex)
        {
            LogFailure(context.HttpContext.RequestServices, ex);
        }
    }

    private static void LogFailure(IServiceProvider services, Exception ex)
    {
        try
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<GuardedPlaybackInfoFilter>()
                .LogError(ex, "[Babelfin] Playback filter unavailable; Jellyfin's request passes through");
        }
        catch (Exception)
        {
            // Even logging services may be unavailable during startup.
        }
    }
}
