using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Decisions;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Settings;
using Jellyfin.Plugin.Babelfin.Storage;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>Every Babelfin setting in one JSON file: the admin configuration and every person's document.</summary>
public sealed class ExportDocument
{
    /// <summary>The schema version this build writes and reads.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the schema version of the file.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets the Babelfin version that wrote the file.</summary>
    public string? BabelfinVersion { get; set; }

    /// <summary>Gets or sets when the file was written (UTC).</summary>
    public DateTime ExportedAt { get; set; }

    /// <summary>Gets or sets the admin configuration, or null when the file carries people only.</summary>
    public PluginConfiguration? Admin { get; set; }

    /// <summary>Gets or sets every person's document.</summary>
    public List<PersonSettings> Users { get; set; } = new();

    /// <summary>Gets or sets accounts omitted because their existing storage could not be safely read or reset.</summary>
    public List<SkippedAccount> SkippedUsers { get; set; } = new();

    /// <summary>Gets whether every stored account was exported. An incomplete export must not be presented as a full backup.</summary>
    public bool Complete => SkippedUsers.Count == 0;
}

/// <summary>An account omitted from an admin export or reset, with an actionable reason.</summary>
/// <param name="UserId">The account id.</param>
/// <param name="UserName">Its current Jellyfin name, or its id if the account is gone.</param>
/// <param name="Reason">Why its original settings were left untouched.</param>
public sealed record SkippedAccount(Guid UserId, string UserName, string Reason);

/// <summary>What an import would do to the admin configuration.</summary>
public sealed class ImportAdminPreview
{
    /// <summary>Gets or sets a value indicating whether the admin configuration would change.</summary>
    public bool WillChange { get; set; }

    /// <summary>Gets or sets the top-level sections that differ ("everyday", "smartRules"…).</summary>
    public List<string> ChangedSections { get; set; } = new();
}

/// <summary>What an import would do for one person in the file.</summary>
public sealed class ImportUserPreview
{
    /// <summary>The action for a person who gets a new file.</summary>
    public const string Create = "create";

    /// <summary>The action for a person whose file is replaced.</summary>
    public const string Update = "update";

    /// <summary>The action for an entry that is left out.</summary>
    public const string Skip = "skip";

    /// <summary>Gets or sets the person's id on this server (the file's id when the entry is skipped).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the person's name on this server (the file's name when the entry is skipped).</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the id the file carried.</summary>
    public Guid SourceUserId { get; set; }

    /// <summary>Gets or sets how the entry was matched: "id", "name", or null when unmatched.</summary>
    public string? MatchedBy { get; set; }

    /// <summary>Gets or sets "create", "update" or "skip".</summary>
    public string Action { get; set; } = Skip;

    /// <summary>Gets or sets why the entry is skipped (or a note), or null.</summary>
    public string? Reason { get; set; }
}

/// <summary>The preview (and, when applied, the report) of an import.</summary>
public sealed class ImportPreview
{
    // Planning refuses an inaccessible target before writes. A later storage race can produce an explicitly partial outcome.
    /// <summary>Gets or sets whether unreadable or archived settings prevent the entire import.</summary>
    public bool StorageBlocked { get; set; }
    /// <summary>Gets or sets what happens to the admin configuration.</summary>
    public ImportAdminPreview Admin { get; set; } = new();

    /// <summary>Gets or sets what happens for each person in the file.</summary>
    public List<ImportUserPreview> Users { get; set; } = new();

    /// <summary>Gets or sets warnings (validation problems, unsupported versions…).</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Gets or sets whether any settings were saved; false for a dry run or no successful writes.</summary>
    public bool Applied { get; set; }
}

/// <summary>A Jellyfin user an import can map documents onto.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Name">The user name.</param>
public sealed record ImportTargetUser(Guid Id, string Name);

/// <summary>
/// Plans and applies an import, and builds an export. Pure apart from the abstractions it is given
/// (the user list, an <see cref="IPersonStore"/> and a callback that replaces the admin configuration), so it is tested without a server.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>A file whose <see cref="ExportDocument.SchemaVersion"/> is not <see cref="ExportDocument.CurrentSchemaVersion"/> is refused as a whole (warning; nothing changes).</item>
/// <item>Matching runs in two passes: every id match first, then name matches (case-insensitive) for the rest, so a name match can't take a person
/// another entry names by id. A name shared by several users, or a second entry mapping to an already-taken person, is skipped with a reason.</item>
/// <item>Every document is prepared and validated like a PUT (<see cref="SettingsValidator"/>); an invalid person is skipped with the first problems as the
/// reason; an invalid admin section is not imported (warning) while valid people still are.</item>
/// <item>The admin configuration keeps this server's <see cref="PluginConfiguration.ConfigVersion"/> (always current) and <see cref="PluginConfiguration.LastUpgradeNote"/>
/// (server-local history); the retired probe fields are never imported.</item>
/// <item>A person's document is replaced as a whole with <see cref="PersonSettings.UserId"/> remapped and <see cref="PersonSettings.UserName"/> set to the
/// server's current name. Retired archives and migration notes are restored from the backup; the latest local Jellyfin undo record is retained under the store lock. People not in the file are left alone (nothing is ever deleted by an import).</item>
/// <item>Validation uses the language table built from the imported admin aliases when the admin section is imported, else the current one.</item>
/// <item>A person document with a newer <see cref="PersonSettings.SchemaVersion"/> than this build knows is skipped. Older person and admin versions migrate before their version is advanced, preserving inactive settings in the archives.</item>
/// </list>
/// </remarks>
public static class ImportPlanner
{
    // Property names drive both the comparison and the regression coverage audit.
    internal static readonly (string Name, string[] Properties)[] Sections =
    {
        ("enabled", new[] { nameof(PluginConfiguration.Enabled) }),
        ("dryRun", new[] { nameof(PluginConfiguration.DryRun) }),
        ("perLibraryDefaults", new[] { nameof(PluginConfiguration.PerLibraryDefaults) }),
        ("unknownAppMode", new[] { nameof(PluginConfiguration.UnknownAppMode) }),
        ("correctAfterStart", new[] { nameof(PluginConfiguration.CorrectAfterStart) }),
        ("compatibilityMode", new[] { nameof(PluginConfiguration.CompatibilityMode) }),
        ("appModes", new[] { nameof(PluginConfiguration.AppModes) }),
        ("playbackTimings", new[] { nameof(PluginConfiguration.ManualChangeAfterSeconds), nameof(PluginConfiguration.ServedWindowMinutes), nameof(PluginConfiguration.AutoplayWindowSeconds) }),
        ("everyday", new[] { nameof(PluginConfiguration.Everyday) }),
        ("libraries", new[] { nameof(PluginConfiguration.Libraries) }),
        ("smartRules", new[] { nameof(PluginConfiguration.SmartRules) }),
        ("titles", new[] { nameof(PluginConfiguration.Titles) }),
        ("burnedIn", new[] { nameof(PluginConfiguration.BurnedIn) }),
        ("newUserTemplate", new[] { nameof(PluginConfiguration.NewUserTemplate) }),
        ("starter", new[] { nameof(PluginConfiguration.Starter) }),
        ("keywords", new[] { nameof(PluginConfiguration.Keywords) }),
        ("languageAliases", new[] { nameof(PluginConfiguration.LanguageAliases) }),
        ("libraryFactsRefreshDays", new[] { nameof(PluginConfiguration.LibraryFactsRefreshDays) }),
        ("retired", new[] { nameof(PluginConfiguration.Retired) }),
    };

    /// <summary>Builds the export document.</summary>
    /// <param name="admin">The current admin configuration (copied).</param>
    /// <param name="people">Every stored person document (copied).</param>
    /// <param name="babelfinVersion">The plugin version.</param>
    /// <param name="now">The export time (UTC).</param>
    /// <returns>The document.</returns>
    public static ExportDocument BuildExport(PluginConfiguration admin, IEnumerable<PersonSettings> people, string babelfinVersion, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(people);
        return new ExportDocument
        {
            SchemaVersion = ExportDocument.CurrentSchemaVersion,
            BabelfinVersion = babelfinVersion,
            ExportedAt = now,
            Admin = CloneAdmin(admin),
            Users = people.Where(p => p is not null).Select(p => p.Clone()).OrderBy(p => p.UserName, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>A deep copy of an admin configuration (through Babelfin's JSON convention), without the retired probe fields.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The copy.</returns>
    public static PluginConfiguration CloneAdmin(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var copy = CopyAdmin(config);
        copy.ProbeAudioLanguage = null;
        copy.ProbeSubtitleLanguage = null;
        copy.ProbeReplaceRequestIndices = null;
        copy.ReplaceRequestIndices = null;
        return copy;
    }

    private static PluginConfiguration CopyAdmin(PluginConfiguration config)
        => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config, BabelfinJson.Options), BabelfinJson.Options) ?? new PluginConfiguration();

    /// <summary>Works out what an import would do; changes nothing.</summary>
    /// <param name="document">The file.</param>
    /// <param name="current">The current admin configuration.</param>
    /// <param name="users">The users on this server.</param>
    /// <param name="store">The person store (read only here).</param>
    /// <param name="languages">The language table for validation.</param>
    /// <returns>The preview.</returns>
    public static ImportPreview Plan(ExportDocument document, PluginConfiguration current, IReadOnlyList<ImportTargetUser> users, IPersonStore store, LanguageTable languages)
        => Build(document, current, users, store, languages).Preview;

    /// <summary>Plans an import and, unless <paramref name="dryRun"/>, applies it.</summary>
    /// <param name="document">The file.</param>
    /// <param name="current">The current admin configuration (never changed in place).</param>
    /// <param name="users">The users on this server.</param>
    /// <param name="store">The person store.</param>
    /// <param name="languages">The language table for validation.</param>
    /// <param name="replaceAdmin">Replaces the admin configuration (called only when it changes and the run is not a dry run).</param>
    /// <param name="dryRun">True to only preview.</param>
    /// <param name="personImported">Receives the locked before/after documents after each successful person save.</param>
    /// <returns>The preview, with <see cref="ImportPreview.Applied"/> set when applied.</returns>
    public static ImportPreview Run(
        ExportDocument document,
        PluginConfiguration current,
        IReadOnlyList<ImportTargetUser> users,
        IPersonStore store,
        LanguageTable languages,
        Action<PluginConfiguration> replaceAdmin,
        bool dryRun,
        Action<PersonSettings?, PersonSettings>? personImported = null)
    {
        ArgumentNullException.ThrowIfNull(replaceAdmin);
        var plan = Build(document, current, users, store, languages);
        if (dryRun || plan.Refused)
        {
            return plan.Preview;
        }

        var anySucceeded = false;
        if (plan.Admin is not null && plan.Preview.Admin.WillChange)
        {
            try
            {
                replaceAdmin(plan.Admin);
                anySucceeded = true;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                plan.Preview.Warnings.Add("The admin settings could not be saved. Check storage availability and permissions, then retry.");
            }
        }

        foreach (var person in plan.People)
        {
            var row = plan.Preview.Users.Single(r => r.UserId == person.UserId && r.Action != ImportUserPreview.Skip);
            PersonSettings? before = null;
            PersonSettings? imported = null;
            try
            {
                PersonStorageAccess.Require(store, person.UserId, writing: true);
                // A backup replaces the chosen document fields, but another server's Jellyfin undo record must never travel.
                if (!PersonStorageAccess.Update(store, person.UserId, currentPerson =>
                {
                    PersonStorageAccess.RequireSnapshot(person, currentPerson);
                    before = currentPerson;
                    imported = person.Clone();
                    imported.JellyfinChanges = currentPerson?.JellyfinChanges?.Clone();
                    return imported;
                }))
                {
                    throw new System.IO.IOException("The store did not save the imported settings.");
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                if (ex is PersonStorageConflictException) plan.Preview.StorageBlocked = true;
                row.Action = ImportUserPreview.Skip;
                row.Reason = ex is PersonStorageConflictException ? ex.Message : "The settings could not be saved. Check storage availability and permissions, then retry.";
                plan.Preview.Warnings.Add($"{row.UserName}: {row.Reason}");
                continue;
            }

            anySucceeded = true;
            personImported?.Invoke(before, imported!);
        }

        plan.Preview.Applied = anySucceeded;
        return plan.Preview;
    }

    private static ImportPlan Build(ExportDocument document, PluginConfiguration current, IReadOnlyList<ImportTargetUser> users, IPersonStore store, LanguageTable languages)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(languages);

        var preview = new ImportPreview();
        var structureErrors = ImportStructure.Validate(document);
        if (structureErrors.Count > 0)
        {
            preview.Warnings.Add("Nothing can be imported: " + Describe(structureErrors));
            return new ImportPlan(preview, null, new List<PersonSettings>(), Refused: true);
        }

        var entries = (document.Users ?? new List<PersonSettings>()).Where(p => p is not null).ToList();

        if (document.SchemaVersion != ExportDocument.CurrentSchemaVersion)
        {
            var reason = $"the file's schema version {document.SchemaVersion} is not supported (this Babelfin reads version {ExportDocument.CurrentSchemaVersion})";
            preview.Warnings.Add("Nothing can be imported: " + reason + ".");
            preview.Users.AddRange(entries.Select(p => Skipped(p, reason)));
            return new ImportPlan(preview, null, new List<PersonSettings>(), Refused: true);
        }

        // Admin.
        PluginConfiguration? admin = null;
        if (document.Admin is null)
        {
            preview.Warnings.Add("The file has no admin settings; the admin settings stay as they are.");
        }
        else
        {
            // Preserve the file's version and historical switches until its migration has archived hidden settings.
            var candidate = CopyAdmin(document.Admin);
            candidate.LastUpgradeNote = string.Empty;
            SettingsValidator.PrepareAdmin(candidate);
            if (candidate.NewUserTemplate is { SchemaVersion: < PersonSettings.CurrentSchemaVersion } template)
            {
                PersonStore.Migrate(template);
            }

            if (candidate.ConfigVersion < PluginConfiguration.CurrentVersion)
            {
                candidate.Upgrade();
            }

            candidate.ConfigVersion = PluginConfiguration.CurrentVersion;
            candidate = CloneAdmin(candidate);
            var candidateLanguages = new EngineServices(candidate).Languages;
            var errors = SettingsValidator.ValidateAdmin(candidate, candidateLanguages);
            if (errors.Count > 0)
            {
                preview.Warnings.Add("The admin settings are not imported: " + Describe(errors));
            }
            else
            {
                admin = candidate;

                // The people are checked against the language table the imported aliases will build.
                languages = candidateLanguages;
                preview.Admin.ChangedSections = ChangedSections(current, candidate);
                preview.Admin.WillChange = preview.Admin.ChangedSections.Count > 0;
            }
        }

        // People: ids first, then names.
        var byId = users.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());
        var targets = new (ImportTargetUser? User, string? MatchedBy)[entries.Count];
        var taken = new HashSet<Guid>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].UserId != Guid.Empty && byId.TryGetValue(entries[i].UserId, out var user) && taken.Add(user.Id))
            {
                targets[i] = (user, "id");
            }
        }

        var people = new List<PersonSettings>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var (target, matchedBy) = targets[i];
            if (target is null)
            {
                if (entry.UserId != Guid.Empty && byId.ContainsKey(entry.UserId))
                {
                    preview.Users.Add(Skipped(entry, "another entry in the file already maps to this person"));
                    continue;
                }

                var name = entry.UserName?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    preview.Users.Add(Skipped(entry, "no user on this server has this id, and the entry has no name to match"));
                    continue;
                }

                var named = users.Where(u => string.Equals(u.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 0)
                {
                    preview.Users.Add(Skipped(entry, "no user on this server has this id or name"));
                    continue;
                }

                if (named.Count > 1)
                {
                    preview.Users.Add(Skipped(entry, "several users on this server have this name"));
                    continue;
                }

                if (!taken.Add(named[0].Id))
                {
                    preview.Users.Add(Skipped(entry, "another entry in the file already maps to this person"));
                    continue;
                }

                target = named[0];
                matchedBy = "name";
            }

            var row = new ImportUserPreview
            {
                UserId = target.Id,
                UserName = target.Name,
                SourceUserId = entry.UserId,
                MatchedBy = matchedBy,
            };

            if (entry.SchemaVersion > PersonSettings.CurrentSchemaVersion)
            {
                row.Action = ImportUserPreview.Skip;
                row.Reason = $"the person's settings were written by a newer Babelfin (schema {entry.SchemaVersion})";
                preview.Users.Add(row);
                continue;
            }

            var doc = entry.Clone();
            doc.UserId = target.Id;
            doc.UserName = target.Name;
            SettingsValidator.PreparePerson(doc);
            if (doc.SchemaVersion < PersonSettings.CurrentSchemaVersion)
            {
                PersonStore.Migrate(doc);
            }

            doc.SchemaVersion = PersonSettings.CurrentSchemaVersion;
            var errors = SettingsValidator.ValidatePerson(doc, languages, includeLocks: true);
            if (errors.Count > 0)
            {
                row.Action = ImportUserPreview.Skip;
                row.Reason = "the settings are not valid: " + Describe(errors);
                preview.Users.Add(row);
                continue;
            }

            if (store is IPersonStoreSafety safety && safety.Conflict(target.Id, writing: true) is { } conflict)
            {
                preview.StorageBlocked = true;
                preview.Warnings.Add($"{row.UserName}: {conflict}");
                row.Action = ImportUserPreview.Skip;
                row.Reason = conflict;
                preview.Users.Add(row);
                continue;
            }
            var existing = store.TryGet(target.Id);
            doc.StorageEpoch = existing?.StorageEpoch ?? Guid.Empty;
            row.Action = existing is null ? ImportUserPreview.Create : ImportUserPreview.Update;
            preview.Users.Add(row);
            people.Add(doc);
        }

        return new ImportPlan(preview, admin, people, Refused: preview.StorageBlocked);
    }

    internal static List<string> ChangedSections(PluginConfiguration current, PluginConfiguration incoming)
    {
        var changed = new List<string>();
        foreach (var (name, properties) in Sections)
        {
            var a = JsonSerializer.Serialize(properties.Select(p => typeof(PluginConfiguration).GetProperty(p)!.GetValue(current)), BabelfinJson.Options);
            var b = JsonSerializer.Serialize(properties.Select(p => typeof(PluginConfiguration).GetProperty(p)!.GetValue(incoming)), BabelfinJson.Options);
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        }

        return changed;
    }

    private static ImportUserPreview Skipped(PersonSettings entry, string reason) => new()
    {
        UserId = entry.UserId,
        UserName = entry.UserName,
        SourceUserId = entry.UserId,
        MatchedBy = null,
        Action = ImportUserPreview.Skip,
        Reason = reason,
    };

    private static string Describe(IReadOnlyList<ValidationError> errors)
    {
        var shown = errors.Take(5).Select(e => e.Path.Length == 0 ? e.Message : e.Path + ": " + e.Message);
        var text = string.Join("; ", shown);
        return errors.Count > 5 ? $"{text}; and {errors.Count - 5} more" : text;
    }

    private sealed record ImportPlan(ImportPreview Preview, PluginConfiguration? Admin, List<PersonSettings> People, bool Refused);
}
