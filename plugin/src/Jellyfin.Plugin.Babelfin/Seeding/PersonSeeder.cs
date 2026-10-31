using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Seeding;

/// <summary>What the seeder reads. Pure data so the seeder and its tests need no server.</summary>
/// <param name="UserId">The person.</param>
/// <param name="UserName">Their name.</param>
/// <param name="AudioLanguagePreference">Jellyfin's audio language preference (may be the "OriginalLanguage" sentinel, a code, or empty).</param>
/// <param name="SubtitleLanguagePreference">Jellyfin's subtitle language preference.</param>
/// <param name="SubtitleMode">Jellyfin's subtitle mode name: "Default", "Always", "OnlyForced", "None" or "Smart".</param>
/// <param name="ReportedUiLanguage">The web client's UI language when it has been reported, else null.</param>
/// <param name="ServerMetadataLanguage">The server's preferred metadata language ("en" by default in Jellyfin).</param>
/// <param name="Now">The seed time (UTC).</param>
public sealed record SeedInputs(
    Guid UserId,
    string? UserName,
    string? AudioLanguagePreference,
    string? SubtitleLanguagePreference,
    string? SubtitleMode,
    string? ReportedUiLanguage,
    string? ServerMetadataLanguage,
    DateTime Now)
{
    /// <summary>Jellyfin's sentinel meaning "the title's original language" in <see cref="AudioLanguagePreference"/>.</summary>
    public const string OriginalLanguageSentinel = "OriginalLanguage";

    /// <summary>Reads the inputs from a Jellyfin user.</summary>
    /// <param name="user">The user.</param>
    /// <param name="serverMetadataLanguage">The server's preferred metadata language.</param>
    /// <param name="reportedUiLanguage">The reported UI language, if any.</param>
    /// <returns>The inputs.</returns>
    public static SeedInputs FromUser(User user, string? serverMetadataLanguage, string? reportedUiLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new SeedInputs(
            user.Id,
            user.Username,
            user.AudioLanguagePreference,
            user.SubtitleLanguagePreference,
            user.SubtitleMode.ToString(),
            reportedUiLanguage,
            serverMetadataLanguage,
            DateTime.UtcNow);
    }
}

/// <summary>
/// Seeds a person's starting point from their Jellyfin settings, recording every guess as "assumed".
/// <list type="number">
/// <item>Understood: the Jellyfin audio preference (the "OriginalLanguage" sentinel is skipped and instead sets
/// <see cref="SettingsLayer.ForeignTitles"/> to <see cref="ForeignTitlesRule.OriginalWithSubtitles"/>, so the chain starts with the original).</item>
/// <item>Reads: the Jellyfin subtitle preference.</item>
/// <item>An empty list is filled from the other list ("other-field", or that list's own source when it was assumed too), else the reported
/// UI language ("ui"), else the server's metadata language ("server"); each of those is recorded in <see cref="SeedRecord.Assumed"/>.</item>
/// <item>Subtitle mode: None → off in both cases; OnlyForced → partial only in both; Always → full in both; Smart and Default leave the starter to decide.</item>
/// <item>"Play default audio track regardless of language" is deliberately not seeded.</item>
/// </list>
/// </summary>
public static class PersonSeeder
{
    /// <summary>Builds the seeded document.</summary>
    /// <param name="inputs">What Jellyfin knows.</param>
    /// <param name="languages">The language table.</param>
    /// <returns>A new document with <see cref="PersonSettings.Seed"/> filled in.</returns>
    public static PersonSettings Seed(SeedInputs inputs, LanguageTable languages)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(languages);

        var seed = new SeedRecord
        {
            SeededAt = inputs.Now,
            JellyfinAudioLanguage = inputs.AudioLanguagePreference,
            JellyfinSubtitleLanguage = inputs.SubtitleLanguagePreference,
            JellyfinSubtitleMode = inputs.SubtitleMode,
        };
        var everyday = new SettingsLayer();

        var understood = new List<string>();
        var originalFirst = false;
        foreach (var raw in Split(inputs.AudioLanguagePreference))
        {
            if (string.Equals(raw, SeedInputs.OriginalLanguageSentinel, StringComparison.OrdinalIgnoreCase))
            {
                originalFirst = true;
                continue;
            }

            AddCode(understood, languages.Resolve(raw));
        }

        var reads = new List<string>();
        foreach (var raw in Split(inputs.SubtitleLanguagePreference))
        {
            AddCode(reads, languages.Resolve(raw));
        }

        if (understood.Count == 0)
        {
            Fill(understood, SettingsFields.Understood, reads, inputs, languages, seed);
        }

        if (reads.Count == 0)
        {
            Fill(reads, SettingsFields.Reads, understood, inputs, languages, seed);
        }

        // Only the languages are detected. Which tracks play stays the video's (and Jellyfin's) until the person chooses: their Jellyfin
        // subtitle mode and "original language first" are kept in the seed record for reference, not copied into Babelfin's settings.
        _ = originalFirst;
        everyday.Understood = understood.ToArray();
        everyday.Reads = reads.ToArray();

        return new PersonSettings
        {
            UserId = inputs.UserId,
            UserName = inputs.UserName,
            Seed = seed,
            Everyday = everyday,
            UpdatedAt = inputs.Now,
        };
    }

    private static void Fill(List<string> target, string field, List<string> other, SeedInputs inputs, LanguageTable languages, SeedRecord seed)
    {
        if (other.Count > 0)
        {
            var otherField = field == SettingsFields.Understood ? SettingsFields.Reads : SettingsFields.Understood;
            foreach (var code in other)
            {
                target.Add(code);

                // A list copied from a list that was itself assumed carries the original source, so the banner names the server (or the UI) for both.
                var origin = seed.Assumed.Find(a => a.Field == otherField && string.Equals(a.Language, code, StringComparison.OrdinalIgnoreCase));
                seed.Assumed.Add(new AssumedLanguage { Field = field, Language = code, From = origin?.From ?? "other-field" });
            }

            return;
        }

        var ui = languages.Resolve(inputs.ReportedUiLanguage);
        if (ui is not null)
        {
            target.Add(ui.Code);
            seed.Assumed.Add(new AssumedLanguage { Field = field, Language = ui.Code, From = "ui" });
            return;
        }

        var server = languages.Resolve(inputs.ServerMetadataLanguage);
        if (server is not null)
        {
            target.Add(server.Code);
            seed.Assumed.Add(new AssumedLanguage { Field = field, Language = server.Code, From = "server" });
        }
    }

    private static void AddCode(List<string> list, LanguageTag? tag)
    {
        if (tag is not null && !list.Contains(tag.Code, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(tag.Code);
        }
    }

    private static IEnumerable<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var part in raw.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }
}
