using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Babelfin.Api;

public sealed record MigrationNotice(Guid? UserId, string Name, IReadOnlyList<string> Changes);

/// <summary>The one-time administrator report for settings that were actually migrated.</summary>
[ApiController]
[Route("Babelfin/Admin/Migrations")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class MigrationController : ControllerBase
{
    private readonly IPersonStore _store;
    public MigrationController(IPersonStore store) => _store = store;

    [HttpGet]
    public ActionResult<IReadOnlyList<MigrationNotice>> Get()
    {
        var notices = new List<MigrationNotice>();
        var config = Plugin.Instance?.Configuration;
        if (config is { ConfigVersion: <= Configuration.PluginConfiguration.CurrentVersion } && !string.IsNullOrWhiteSpace(config.LastUpgradeNote))
            notices.Add(new(null, "Babelfin", config.LastUpgradeNote.Split('\n')));
        foreach (var id in _store.UserIds())
        {
            var person = _store.TryGet(id);
            if (person is { SchemaVersion: <= PersonSettings.CurrentSchemaVersion, MigrationNotes.Count: > 0 })
                notices.Add(new(id, person.UserName ?? id.ToString("N"), person.MigrationNotes.ToArray()));
        }
        return Ok(notices);
    }

    [HttpPost("Acknowledge")]
    public IActionResult Acknowledge()
    {
        foreach (var id in _store.UserIds())
            _store.Update(id, current =>
            {
                if (current is null || current.MigrationNotes.Count == 0) return null;
                var copy = current.Clone();
                copy.MigrationNotes.Clear();
                return copy;
            });
        var plugin = Plugin.Instance;
        if (plugin is not null && plugin.Configuration.ConfigVersion <= Configuration.PluginConfiguration.CurrentVersion && !string.IsNullOrEmpty(plugin.Configuration.LastUpgradeNote))
        {
            lock (plugin)
            {
                plugin.Configuration.LastUpgradeNote = string.Empty;
                plugin.UpdateConfiguration(plugin.Configuration);
            }
        }
        return NoContent();
    }
}
