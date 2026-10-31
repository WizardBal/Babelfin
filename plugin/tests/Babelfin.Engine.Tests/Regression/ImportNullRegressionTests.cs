using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class ImportNullRegressionTests
{
    [Theory]
    [InlineData("{\"users\":null}")]
    [InlineData("{\"users\":[null]}")]
    [InlineData("{\"users\":[{\"overrides\":null}]}")]
    [InlineData("{\"users\":[{\"everyday\":null}]}")]
    [InlineData("{\"users\":[{\"seed\":{\"assumed\":null}}]}")]
    [InlineData("{\"users\":[{\"smartRules\":[{\"conditions\":[{\"values\":null}]}]}]}")]
    [InlineData("{\"admin\":{\"libraries\":null}}")]
    [InlineData("{\"admin\":{\"newUserTemplate\":{\"overrides\":null}}}")]
    public async Task AdminRejectsNullStructureWith400BeforeAnyPersonOrAdminMutation(string json)
    {
        using var host = new LocalStorageTestHost();
        var plugin = host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        store.Save(new PersonSettings { UserId = user.Id, UserName = user.Username });
        var bytes = File.ReadAllBytes(store.FilePath(user.Id));
        var configBytes = File.ReadAllBytes(plugin.ConfigurationFilePath);
        var document = JsonSerializer.Deserialize<ExportDocument>(json, BabelfinJson.Options)!;
        // Put a valid, changing entry first: rejecting a later invalid entry must reject the entire import.
        if (document.Users is not null) document.Users.Insert(0, new PersonSettings { UserId = user.Id, Enabled = false });
        var before = JsonSerializer.Serialize(document, BabelfinJson.Options);
        var response = await ApiSafetyHost.Admin(store, null, user).Import(document, dryRun: false);
        var problem = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.NotEmpty(Assert.IsType<ApiProblem>(problem.Value).Errors);
        var replaced = false;
        var preview = ImportPlanner.Run(document, plugin.Configuration, new[] { new ImportTargetUser(user.Id, user.Username) },
            store, LanguageTable.Default, _ => replaced = true, dryRun: false);
        Assert.False(preview.Applied);
        Assert.False(replaced);
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath(user.Id)));
        Assert.Equal(configBytes, File.ReadAllBytes(plugin.ConfigurationFilePath));
        Assert.Equal(before, JsonSerializer.Serialize(document, BabelfinJson.Options));
    }

    [Theory]
    [InlineData("{\"overrides\":null}")]
    [InlineData("{\"libraries\":null}")]
    [InlineData("{\"burnedIn\":[{\"languages\":null}]}")]
    [InlineData("{\"smartRules\":[null]}")]
    public async Task PersonalImportRejectsNullStructureBeforeSeeding(string json)
    {
        using var host = new LocalStorageTestHost();
        var store = host.Store;
        var user = ApiSafetyHost.Person();
        var body = JsonSerializer.Deserialize<PersonSettings>(json, BabelfinJson.Options)!;
        var response = await ApiSafetyHost.Me(store, user).Import(body);
        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Empty(store.UserIds());
        Assert.False(File.Exists(store.FilePath(user.Id)));
    }

    [Fact]
    public void OmittedListsAndInheritedNullLanguageArraysRemainValid()
    {
        var body = JsonSerializer.Deserialize<ExportDocument>("{\"users\":[{\"everyday\":{\"understood\":null}}]}", BabelfinJson.Options)!;
        Assert.Empty(ImportStructure.Validate(body));
    }
}
