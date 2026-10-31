using System;
using Jellyfin.Plugin.Babelfin;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public sealed class EmbeddedAssetPortabilityTests
{
    [Fact]
    public void EmbeddedAssetNamesUsePortableForwardSlashes()
    {
        var names = typeof(Plugin).Assembly.GetManifestResourceNames();
        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => name.Contains('\\', StringComparison.Ordinal));
        Assert.Contains("Babelfin.i18n/en.json", names);
        Assert.Contains("Babelfin.i18n-admin/en.json", names);
        Assert.Contains("Babelfin.app.html", names);
        Assert.Contains("Babelfin.configPage.html", names);
    }
}
