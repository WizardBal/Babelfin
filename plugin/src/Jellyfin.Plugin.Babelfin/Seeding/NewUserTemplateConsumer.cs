using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Seeding;

/// <summary>
/// When Jellyfin creates a user, seeds their Babelfin settings from their Jellyfin preferences and applies the admin's
/// new-user template on top. People who existed before install are seeded lazily on first use instead.
/// </summary>
/// <remarks>
/// Behavior: the document is written through <see cref="IPersonStore.GetOrCreate"/>, so a person who
/// already has a file (an import that ran first, a repeated event) is left exactly as they are; any failure is logged and never
/// stops the user being created (the person is then seeded on first use, without the template). The server's metadata language is
/// the fallback for unknown languages, as in the resolver.
/// </remarks>
public class NewUserTemplateConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly IPersonStore _store;
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly ILogger<NewUserTemplateConsumer> _logger;
    private readonly IUserManager _users;

    /// <summary>Initializes a new instance of the <see cref="NewUserTemplateConsumer"/> class.</summary>
    /// <param name="store">The person store.</param>
    /// <param name="serverConfiguration">The server configuration (its preferred metadata language), or null in test hosts.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="users">Authoritative account lookup, rechecked inside the lifecycle transition.</param>
    public NewUserTemplateConsumer(IPersonStore store, IServerConfigurationManager? serverConfiguration, ILogger<NewUserTemplateConsumer> logger, IUserManager users)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(users);
        _store = store;
        _serverConfiguration = serverConfiguration;
        _logger = logger;
        _users = users;
    }

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        var user = eventArgs?.Argument;
        if (user is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            lock (UserLifecycleTransitions.For(_store))
            {
                // Creation callbacks can be delayed past deletion. The event entity is only a candidate id.
                var currentUser = _users.GetUserById(user.Id);
                if (currentUser is null) return Task.CompletedTask;
                if (_store is IPersonStoreSafety safety) safety.Activate(currentUser.Id);
                var created = false;
                _store.GetOrCreate(currentUser.Id, () =>
                {
                    created = true;
                    var settings = PersonSeeder.Seed(
                        SeedInputs.FromUser(currentUser, _serverConfiguration?.Configuration?.PreferredMetadataLanguage),
                        EngineServices.Current.Languages);
                    if (Plugin.Instance?.Configuration.NewUserTemplate is { } template)
                    {
                        settings.ApplyTemplate(template);
                    }

                    return settings;
                });

                if (created)
                {
                    _logger.LogInformation("[Babelfin] Settings created for new person {UserName}", currentUser.Username);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't create settings for new person {UserName}; they are seeded when they first play something", user.Username);
        }

        return Task.CompletedTask;
    }
}
