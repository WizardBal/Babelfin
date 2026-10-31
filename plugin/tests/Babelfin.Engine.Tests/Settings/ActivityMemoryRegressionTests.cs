using System;
using System.IO;
using Jellyfin.Plugin.Babelfin.Activity;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public sealed class ActivityMemoryRegressionTests
{
    [Fact]
    public void ActivityDoesNotCreateFilesOrSurviveRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "babelfin-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new ActivityLog();
            first.Add(new ActivityEntry(default, ActivityKinds.SettingsChanged, null, null, "saved"));
            Assert.Single(first.Query(10));
            Assert.False(Directory.Exists(folder));
            var second = new ActivityLog();
            Assert.Empty(second.Query(10));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void LegacyActivityIsIgnoredAndLeftUntouched()
    {
        var folder = Path.Combine(Path.GetTempPath(), "babelfin-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "activity.jsonl");
        var bytes = System.Text.Encoding.UTF8.GetBytes("legacy contents\n");
        File.WriteAllBytes(path, bytes);
        try
        {
            var log = new ActivityLog();
            log.Add(new ActivityEntry(default, ActivityKinds.SettingsChanged, null, null, "new"));
            Assert.Single(log.Query(10));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void LockedLegacyFileDoesNotAffectMemoryActivity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "babelfin-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "activity.jsonl");
        File.WriteAllText(path, "legacy");
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var log = new ActivityLog();
            log.Add(new ActivityEntry(default, ActivityKinds.SettingsChanged, null, null, "new"));
            Assert.Equal("new", Assert.Single(log.Query(10)).Text);
        }
        finally { Directory.Delete(folder, true); }
    }
}
