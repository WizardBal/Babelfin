using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class SettingsExpectedBodyTests
{
    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value, BabelfinJson.Options);

    [Fact]
    public void ValidLargeTableUsesBodyExpectationsAndStillDetectsSameFieldConflicts()
    {
        var config = new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion };
        config.Titles = Enumerable.Range(1, 100).Select(i => new TitleDefault
        {
            ItemId = i.ToString("x32"), Scope = OverrideScope.Title, Name = new string('A', SettingsValidator.MaxStringLength),
            Settings = new SettingsLayer(),
        }).ToList();
        Assert.Empty(SettingsValidator.ValidateAdmin(config, new EngineServices(config).Languages));
        var expected = new Dictionary<string, object?> { ["titles"] = config.Titles };
        var json = JsonSerializer.Serialize(expected, new JsonSerializerOptions(BabelfinJson.Options) { WriteIndented = false });
        Assert.True(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Length > 32768);
        var body = Body(new { titles = config.Titles, expectedValues = expected });
        Assert.True(SettingsUpdateRequest.TryRead<PluginConfiguration>(body, "titles", out var decoded, out var supplied, out var error));
        Assert.Null(error);
        Assert.True(SettingsIntent.TryParse("titles", true, true, out var intent, out _));
        intent!.ReadExpected(new DefaultHttpContext().Request, supplied);
        intent.CheckExpected(config);
        config.Titles[0].Name = "Changed elsewhere";
        Assert.Throws<SettingsConflictException>(() => intent.CheckExpected(config));
        Assert.DoesNotContain("expectedValues", JsonSerializer.Serialize(decoded, BabelfinJson.Options), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminBodyExpectationsSaveConflictAndStayOutOfConfiguration()
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion, ServedWindowMinutes = 90 });
        var logger = new StorageTestLogger<AdminController>();
        var admin = new AdminController(ApiSafetyHost.Library(), ApiSafetyHost.Users(), host.Store, new FakeLibraryFacts(), CallProxy.Create<IProfileResolver>().Proxy, logger: logger);
        admin.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var body = Body(new { servedWindowMinutes = 45, expectedValues = new { servedWindowMinutes = 90 } });
        Assert.IsType<OkObjectResult>(admin.PutSettingsRequest(body, "servedWindowMinutes").Result);
        Assert.Equal(45, plugin.Configuration.ServedWindowMinutes);
        var line = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information && e.Message.Contains("Settings changed", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Folder, line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedValues", JsonSerializer.Serialize(plugin.Configuration, BabelfinJson.Options), StringComparison.OrdinalIgnoreCase);
        var conflict = Assert.IsType<ObjectResult>(await StorageApiSafetyTests.InvokeApi(admin,
            () => Task.FromResult<IActionResult?>(admin.PutSettingsRequest(body, "servedWindowMinutes").Result)));
        Assert.Equal(409, conflict.StatusCode);
        Assert.IsType<OkObjectResult>(admin.PutSettingsRequest(Body(new { servedWindowMinutes = 30 }), "servedWindowMinutes").Result);
        Assert.Equal(30, plugin.Configuration.ServedWindowMinutes);
    }

    [Theory]
    [InlineData("{\"expectedValues\":null}", "enabled")]
    [InlineData("{\"expectedValues\":[]}", "enabled")]
    [InlineData("{\"expectedValues\":{},\"ExpectedValues\":{}}", "enabled")]
    [InlineData("{\"expectedValues\":{}}", null)]
    public void InvalidBodyMetadataFailsBeforeSaving(string json, string? fields)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(SettingsUpdateRequest.TryRead<PersonSettings>(document.RootElement, fields, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("{\"enabled\":true,\"enabled\":false}")]
    [InlineData("{}")]
    [InlineData("{\"enabled\":true,\"perLibrary\":null}")]
    public void DuplicateMissingOrAdditionalExpectedFieldsAreRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(SettingsIntent.TryParse("enabled", false, false, out var intent, out _));
        Assert.Throws<SettingsValidationException>(() => intent!.ReadExpected(new DefaultHttpContext().Request, document.RootElement));
    }

    [Fact]
    public void BodyAndLegacyHeaderCannotBeCombined()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["X-Babelfin-Expected"] = "eyJlbmFibGVkIjp0cnVlfQ";
        Assert.True(SettingsIntent.TryParse("enabled", false, false, out var intent, out _));
        Assert.Throws<SettingsValidationException>(() => intent!.ReadExpected(request, Body(new { enabled = true })));
        intent!.ReadExpected(request);
        intent.CheckExpected(new PersonSettings { Enabled = true });
    }

    [Fact]
    public void NestedTemplateExpectationsAcceptCompactGuidsFromJellyfinResponses()
    {
        var id = Guid.NewGuid();
        var config = new PluginConfiguration { NewUserTemplate = new PersonSettings { UserId = id } };
        var wire = JsonSerializer.Serialize(new { newUserTemplate = config.NewUserTemplate }, BabelfinJson.Options)
            .Replace(id.ToString("D"), id.ToString("N"), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(wire);
        Assert.True(SettingsIntent.TryParse("newUserTemplate", true, true, out var intent, out _));
        intent!.ReadExpected(new DefaultHttpContext().Request, document.RootElement);
        intent.CheckExpected(config);
    }

    [Fact]
    public void OversizedExpectedBodyIsRejected()
    {
        var body = Body(new { expectedValues = new { enabled = new string('x', SettingsUpdateRequest.MaxExpectedBytes) } });
        Assert.False(SettingsUpdateRequest.TryRead<PersonSettings>(body, "enabled", out _, out _, out var error));
        Assert.NotNull(error);
    }
}
