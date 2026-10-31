using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Babelfin.Engine.Tests.Settings;

public class ApiSafetyOverridesTests
{
    private static string Json(object? value) => JsonSerializer.Serialize(value, BabelfinJson.Options);
    private static SettingsLayer Choices() => new()
    {
        AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "en") },
        WhenAudioUnderstood = SubtitleWant.Off,
        WhenAudioNotUnderstood = SubtitleWant.Full,
        SubtitleLanguages = new[] { "en" },
    };

    private static PersonSettings Stored()
    {
        var doc = new PersonSettings { UserId = Ids.Person };
        doc.Overrides.Add(new ItemOverride
        {
            ItemId = Ids.N(Ids.Item), Settings = Choices(), UpdatedAt = DateTime.UtcNow.AddDays(-2),
            Manual = new ManualMemory
            {
                Audio = new RememberedTrack { Language = "en", Codec = "aac", Kind = "normal" },
                Subtitle = new RememberedTrack { Language = "en", Kind = "full" }, SubtitlesOff = true,
                At = DateTime.UtcNow.AddDays(-1), Client = "Jellyfin Web",
            },
        });
        return doc;
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("foreign")]
    [InlineData("understood")]
    [InlineData("notUnderstood")]
    [InlineData("languages")]
    [InlineData("unrelated")]
    [InlineData("unchanged")]
    public void SingleAndWholeDocumentSaves_ClearOnlyTheChangedChoice(string field)
    {
        var stored = Stored();
        var old = stored.Overrides[0];
        var layer = old.Settings.Clone();
        switch (field)
        {
            case "audio": layer.AudioChain = new[] { new AudioStepSetting(AudioStepKind.Language, "ja") }; break;
            case "foreign": layer.ForeignTitles = ForeignTitlesRule.OriginalWithSubtitles; break;
            case "understood": layer.WhenAudioUnderstood = SubtitleWant.Full; break;
            case "notUnderstood": layer.WhenAudioNotUnderstood = SubtitleWant.Off; break;
            case "languages": layer.SubtitleLanguages = new[] { "ja", "en" }; break;
            case "unrelated": layer.AvoidCommentary = true; break;
        }

        var before = Json(stored);
        var single = PersonDocuments.WithOverride(stored, Ids.Item, OverrideScope.Title, "Movie", layer, DateTime.UtcNow, out _)!;
        var body = stored.Clone();
        body.Overrides[0].Settings = layer;
        // A supplied Manual object must never replace storage's remembered fields in an editor save.
        body.Overrides[0].Manual = new ManualMemory { Audio = new RememberedTrack { Language = "de" } };
        foreach (var result in new[] { single, PersonDocuments.Merge(stored, body, false, ApiSafetyHost.Person()), PersonDocuments.Merge(stored, body, true, ApiSafetyHost.Person()) })
        {
            var memory = Assert.IsType<ManualMemory>(result.Overrides[0].Manual);
            if (field is "audio" or "foreign") { Assert.Null(memory.Audio); }
            else { Assert.Equal(Json(old.Manual!.Audio), Json(memory.Audio)); }
            if (field is "understood" or "notUnderstood" or "languages")
            {
                Assert.Null(memory.Subtitle);
                Assert.False(memory.SubtitlesOff);
            }
            else
            {
                Assert.Equal(Json(old.Manual!.Subtitle), Json(memory.Subtitle));
                Assert.True(memory.SubtitlesOff);
            }
            Assert.Equal(old.Manual!.At, memory.At);
            Assert.Equal(old.Manual.Client, memory.Client);
        }
        Assert.Equal(before, Json(stored));
    }

    [Fact]
    public void ChangingBothChoices_RemovesEmptyMemory_KeepOverridesPreservesExactStoredEntries()
    {
        var stored = Stored();
        var body = stored.Clone();
        body.Overrides[0].Settings.AudioChain![0].Language = "ja";
        body.Overrides[0].Settings.SubtitleLanguages = new[] { "fr" };
        var edited = PersonDocuments.Merge(stored, body, true, ApiSafetyHost.Person());
        Assert.Null(edited.Overrides[0].Manual);
        var single = PersonDocuments.WithOverride(stored, Ids.Item, OverrideScope.Title, "Movie", body.Overrides[0].Settings, DateTime.UtcNow, out _)!;
        Assert.Null(single.Overrides[0].Manual);
        var kept = PersonDocuments.Merge(stored, body, true, ApiSafetyHost.Person(), keepOverrides: true);
        Assert.Equal(Json(stored.Overrides), Json(kept.Overrides));
    }

    [Fact]
    public void SubtitleSaveClearsSubtitlesOffOnlyMemory_ThenLaterHandChangeCanWin()
    {
        var store = new ApiSafetyStore();
        var doc = Stored();
        doc.Overrides[0].Manual!.Audio = null;
        doc.Overrides[0].Manual!.Subtitle = null;
        store.Inner.Save(doc);
        var layer = Choices();
        layer.WhenAudioUnderstood = SubtitleWant.Full;
        var response = ApiSafetyHost.Me(store, ApiSafetyHost.Person()).PutOverride(Ids.Item, layer, null, null);
        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Null(store.TryGet(Ids.Person)!.Overrides[0].Manual);
        store.Inner.Update(Ids.Person, current =>
        {
            var copy = current!.Clone();
            copy.Overrides[0].Manual = new ManualMemory { Audio = new RememberedTrack { Language = "de" }, At = DateTime.UtcNow };
            return copy;
        });
        var unchanged = PersonDocuments.Merge(store.TryGet(Ids.Person)!, store.TryGet(Ids.Person)!.Clone(), false, ApiSafetyHost.Person());
        Assert.Equal("de", unchanged.Overrides[0].Manual!.Audio!.Language);
    }

    [Fact]
    public void WholeDocumentRemovalClearsOnlyMemoryForRemovedExplicitChoices()
    {
        var stored = Stored();
        stored.Overrides[0].Settings = new SettingsLayer { AudioChain = Choices().AudioChain };
        var body = stored.Clone();
        body.Overrides.Clear();
        var kept = Assert.Single(PersonDocuments.Merge(stored, body, false, ApiSafetyHost.Person()).Overrides);
        Assert.Null(kept.Manual!.Audio);
        Assert.NotNull(kept.Manual.Subtitle);
        Assert.True(kept.Manual.SubtitlesOff);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MergedSavesKeepLatestServerOwnedRetiredSnapshotAndMigrationNotes(bool admin, bool keepOverrides)
    {
        var store = new ApiSafetyStore();
        var stored = Stored();
        stored.MigrationNotes.Add("earlier note");
        store.Inner.Save(stored);
        var body = stored.Clone();
        body.MigrationNotes = new List<string> { "untrusted note" };
        body.Retired.Rules.Add(new SmartRule { Name = "untrusted snapshot" });
        store.BeforeUpdate = () => store.Inner.Update(Ids.Person, current =>
        {
            var copy = current!.Clone();
            copy.MigrationNotes.Add("latest server note");
            copy.Retired.Rules.Add(new SmartRule { Name = "latest stored rule" });
            return copy;
        });
        var saved = PersonDocuments.SaveMerged(store, stored, body, admin, ApiSafetyHost.Person(), keepOverrides);
        Assert.Equal(new[] { "earlier note", "latest server note" }, saved.Merged.MigrationNotes);
        Assert.Equal("latest stored rule", Assert.Single(saved.Merged.Retired.Rules).Name);
        Assert.Equal(Json(saved.Before.Retired), Json(saved.Merged.Retired));
        saved.Merged.Retired.Rules[0].Name = "changed copy";
        Assert.Equal("latest stored rule", saved.Before.Retired.Rules[0].Name);
    }

}
