using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using C = Babelfin.Engine.Tests.Settings.Ctx;
using static Babelfin.Engine.Tests.Regression.PreferenceApiTests;

namespace Babelfin.Engine.Tests.Regression;

public class ChoiceStatusTests
{
    private static readonly Guid Visible = Ids.Shows;
    private static bool Status(PersonSettings p, PluginConfiguration? a = null, params Guid[] visible) => ChoiceStatusLogic.ForPerson(p, a ?? new(), visible);

    [Fact]
    public void SeedLanguageListsAndArchiveAloneAreNotTrackChoices()
    {
        var p = C.Person(); p.Seed = new() { JellyfinSubtitleMode = "Always", Confirmed = true };
        p.Everyday.SubtitleLanguages = new[] { "de" };
        p.Retired.GenreRules.Add(Genre("Anime", Audio("ja")));
        Assert.False(Status(p, null, Visible));
        var a = new PluginConfiguration { NewUserTemplate = p.Clone() };
        Assert.False(ChoiceStatusLogic.AdminDefaults(a, new[] { Visible }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheActiveTableModeCounts(bool perLibrary)
    {
        var p = C.Person(); p.PerLibrary = perLibrary;
        if (perLibrary) p.Everyday.AudioChain = Audio("ja").AudioChain;
        else p.Libraries.Add(C.MyLibrary(Visible, Audio("ja")));
        Assert.False(Status(p, null, Visible));
        if (perLibrary) p.Libraries.Add(C.MyLibrary(Visible, Audio("en")));
        else p.Everyday.AudioChain = Audio("en").AudioChain;
        Assert.True(Status(p, null, Visible));
    }

    [Fact]
    public void EffectiveVisibleAdminDefaultsCount_AndInvisibleDefaultsDoNot()
    {
        var p = C.Person(); var a = new PluginConfiguration { PerLibraryDefaults = true };
        a.Libraries.Add(C.TheirLibrary(Ids.Anime, Audio("ja")));
        Assert.False(Status(p, a, Visible));
        Assert.True(Status(p, a, Ids.Anime));
        p.PerLibrary = false;
        p.Everyday.AudioChain = C.Chain(C.Step(AudioStepKind.VideoDefault));
        Assert.False(Status(p, a, Ids.Anime));
        a.Libraries[0].Settings.WhenAudioNotUnderstood = SubtitleWant.Full;
        Assert.True(Status(p, a, Ids.Anime));
        Assert.False(Status(C.Person(), a));
    }

    [Fact]
    public void GenresAndManualOverridesCount_EmptyOrDisabledRowsDoNot()
    {
        var p = C.Person();
        var row = Genre("Anime", Audio("ja")); row.Enabled = false; p.SmartRules.Add(row);
        Assert.False(Status(p, null, Visible));
        row.Enabled = true;
        Assert.True(Status(p, null, Visible));
        p.SmartRules.Clear();
        p.Overrides.Add(C.Override(Ids.Series, OverrideScope.Series, new(), new ManualMemory { SubtitlesOff = true }));
        Assert.True(Status(p, null, Visible));
        p.Overrides[0].Manual = new();
        Assert.False(Status(p, null, Visible));
    }

    [Theory]
    [InlineData(SettingsFields.AvoidCommentary)]
    [InlineData(SettingsFields.UsesAudioDescription)]
    [InlineData(SettingsFields.PrefersSdh)]
    [InlineData(SettingsFields.Format)]
    [InlineData(SettingsFields.Sdh)]
    [InlineData(SettingsFields.Embedded)]
    public void ExplicitAdvancedFilteringAndLanguagePreferencesCannotBeReportedAsNeutral(string field)
    {
        var p = C.Person();
        var values = new SettingsLayer { AvoidCommentary = true, UsesAudioDescription = true, PrefersSdh = true,
            Format = FormatPreference.TextOnly, Sdh = Preference.Avoid, Embedded = EmbeddedPreference.PreferExternal };
        p.Everyday.CopyField(values, field);
        Assert.True(Status(p, null, Visible));
    }

    [Fact]
    public void DisabledPlaybackKeepsSavedChoices_AndOnlySelectedTableModeCounts()
    {
        var p = C.Person(); p.Everyday.AudioChain = Audio("ja").AudioChain;
        p.Enabled = false; Assert.True(Status(p, null, Visible));
        p.Enabled = true; Assert.True(Status(p, new() { Enabled = false }, Visible));
        var a = new PluginConfiguration { PerLibraryDefaults = true, Everyday = Audio("ja") };
        Assert.False(ChoiceStatusLogic.AdminDefaults(a, new[] { Visible }));
        a.SmartRules.Add(Genre("Anime", Audio("ja")));
        Assert.True(ChoiceStatusLogic.AdminDefaults(a, new[] { Visible }));
        a.SmartRules[0].Enabled = false;
        Assert.False(ChoiceStatusLogic.AdminDefaults(a, new[] { Visible }));
    }

    [Fact]
    public void NoSavedChoicesDoesNotPromiseBuiltInCommentaryAvoidanceIsDisabled()
    {
        var p = C.Person();
        Assert.False(Status(p, null, Visible));
        var resolved = C.Resolve(p, null, C.Episode());
        var tracks = new[] { Babelfin.Engine.Tests.Selection.Fx.Audio(1, "eng", "Commentary", isDefault: true), Babelfin.Engine.Tests.Selection.Fx.Audio(2, "eng") };
        var actual = Babelfin.Engine.Tests.Selection.Fx.Selector.Decide(tracks, ItemFacts.None, resolved.Profile);
        Assert.Equal(2, actual.AudioIndex);
        Assert.False(actual.AudioLeftToVideo);
    }
    [Fact]
    public void AnActiveBurnedInChoiceIsNotMistakenForJustALanguageList()
    {
        var p = C.Person();
        p.BurnedIn.Add(new() { ItemId = Ids.Series.ToString("N"), Scope = OverrideScope.Series, Languages = new() { "en" } });
        Assert.True(Status(p, null, Visible));
        p.BurnedIn[0].Languages.Clear();
        Assert.False(Status(p, null, Visible));
    }
    private static ChoiceStatusController Controller(FakePersonStore store, PluginConfiguration? config = null, bool authenticated = true)
    {
        var user = ApiSafetyHost.Person();
        var libraries = CallProxy.Create<ILibraryManager>((m, _) => m.Name == "GetVirtualFolders" ? new List<VirtualFolderInfo>() : null).Proxy;
        var controller = new ChoiceStatusController(store, ApiSafetyHost.Users(user), libraries, () => config ?? new());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticated ? new[] { new Claim(ClaimsHelper.UserIdClaim, user.Id.ToString()) } : Array.Empty<Claim>(), authenticated ? "Test" : null)),
        } };
        return controller;
    }

    [Fact]
    public void EndpointsRequireAuthenticationAndAdminElevation_AndReturnPascalCase()
    {
        var type = typeof(ChoiceStatusController);
        Assert.NotEmpty(type.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Contains(type.GetMethod("Admin")!.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == Policies.RequiresElevation);
        Assert.Equal("Me/ChoiceStatus", type.GetMethod("Me")!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("Admin/ChoiceStatus", type.GetMethod("Admin")!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        var store = new FakePersonStore();
        Assert.IsType<UnauthorizedResult>(Controller(store, authenticated: false).Me().Result);
        var response = Assert.IsType<OkObjectResult>(Controller(store).Me().Result);
        Assert.Equal("{\"HasChoices\":false}", JsonSerializer.Serialize(Assert.IsType<ChoiceStatus>(response.Value), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(0, store.Creates);
    }

    [Fact]
    public void AdminReportsActualPeoplesChoices_AndNoneWhenNoOneHasChosen()
    {
        var store = new FakePersonStore(); var controller = Controller(store);
        Assert.False(Assert.IsType<ChoiceStatus>(Assert.IsType<OkObjectResult>(controller.Admin().Result).Value).HasChoices);
        var p = C.Person(); p.Everyday.AudioChain = Audio("ja").AudioChain; store.Save(p);
        Assert.True(Assert.IsType<ChoiceStatus>(Assert.IsType<OkObjectResult>(controller.Admin().Result).Value).HasChoices);
    }
}