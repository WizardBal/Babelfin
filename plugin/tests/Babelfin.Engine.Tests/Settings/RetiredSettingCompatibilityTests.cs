using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public sealed class RetiredSettingCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderSubtitleDownloadFieldDoesNotResetSavedPreferences(bool retiredValue)
    {
        var folder = Path.Combine(Path.GetTempPath(), "babelfin-retired-setting-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            var original = new PersonSettings
            {
                UserId = id,
                Everyday = new SettingsLayer { Understood = new[] { "en" }, Memory = MemoryScope.Season },
            };
            var json = JsonSerializer.SerializeToNode(original, BabelfinJson.Options)!;
            json["everyday"]!["fetchMissing"] = JsonValue.Create(retiredValue);
            var store = new PersonStore(folder, NullLogger<PersonStore>.Instance);
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath(id))!);
            File.WriteAllText(store.FilePath(id), json.ToJsonString(BabelfinJson.Options));

            var loaded = Assert.IsType<PersonSettings>(store.TryGet(id));
            Assert.Equal(new[] { "en" }, loaded.Everyday.Understood);
            Assert.Equal(MemoryScope.Season, loaded.Everyday.Memory);
            var admin = new PluginConfiguration();
            var resolved = ProfileResolver.Resolve(loaded, admin, ItemContext.None, LanguageTable.Default);
            Assert.Equal(MemoryScope.Season, TrackChangeRecorder.MemoryFor(loaded, admin, resolved));

            var exported = ImportPlanner.BuildExport(admin, new[] { loaded }, "1.0.0.0", DateTime.UtcNow);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(exported, BabelfinJson.Options));
            Assert.False(document.RootElement.GetProperty("users")[0].GetProperty("everyday").TryGetProperty("fetchMissing", out _));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
