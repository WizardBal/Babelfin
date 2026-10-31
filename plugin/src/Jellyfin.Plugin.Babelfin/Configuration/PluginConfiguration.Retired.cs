using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Configuration;

/// <summary>Inactive configuration retained for recovery and export.</summary>
public class RetiredAdminSettings
{
    public List<SmartRule> Rules { get; set; } = new();
    public List<SmartRule> GenreRules { get; set; } = new();
    public List<TitleDefault> Titles { get; set; } = new();
    public List<BurnedInFlag> BurnedIn { get; set; } = new();
    public List<RetiredLayer> Layers { get; set; } = new();
    public StarterSettings? Starter { get; set; }
    public bool? DryRun { get; set; }
    public PersonSettings? NewUserTemplate { get; set; }
}

public partial class PluginConfiguration
{
    public RetiredAdminSettings Retired { get; set; } = new();

    private void ActivateArchivedGenreRows(IReadOnlyList<SmartRule>? legacyOrder = null)
    {
        Retired ??= new();
        Retired.GenreRules ??= new();
        Retired.Rules ??= new();
        var notes = new List<string>();
        PersonMigrations.ActivateGenreRows(SmartRules, Retired.GenreRules, Retired.Rules, notes, legacyOrder);
        if (notes.Count > 0) LastUpgradeNote = string.Join("\n", new[] { LastUpgradeNote }.Where(n => !string.IsNullOrWhiteSpace(n)).Concat(notes));
    }
    private void RetireHiddenSettings()
    {
        Retired ??= new RetiredAdminSettings();
        var notes = new List<string>();
        foreach (var rule in SmartRules)
        {
            (PersonMigrations.IsLegacyTableGenreRule(rule) ? Retired.GenreRules : Retired.Rules).Add(rule.Clone());
            notes.Add("Hidden server rule '" + rule.Name + "' was preserved outside active settings.");
        }
        SmartRules.Clear();
        if (Titles.Count > 0)
        {
            Retired.Titles.AddRange(Titles);
            Titles = new();
            notes.Add("Hidden server title defaults were archived and no longer apply.");
        }
        if (BurnedIn.Count > 0)
        {
            Retired.BurnedIn.AddRange(BurnedIn.Select(b => b.Clone()));
            BurnedIn.Clear();
            notes.Add("Hidden server burned-in flags were archived and no longer apply.");
        }
        if (Starter.Enabled)
        {
            Retired.Starter = Starter;
            Starter = new StarterSettings { Enabled = false };
            notes.Add("The hidden original-language starter was archived and switched off.");
        }
        if (DryRun)
        {
            Retired.DryRun = true;
            DryRun = false;
            notes.Add("Hidden test mode was switched off.");
        }
        var visible = new[] { SettingsFields.UnlabelledAudio }.Concat(TrackDefaultFields).ToHashSet();
        ArchiveLayer(Everyday, visible, "server defaults", notes);
        foreach (var library in Libraries) ArchiveLayer(library.Settings, TrackDefaultFields.ToHashSet(), "library " + library.LibraryId, notes);
        if (NewUserTemplate is { } template && (template.Everyday.SetFields().Any(f => f != SettingsFields.Understood)
            || template.Libraries.Count > 0 || template.SmartRules.Count > 0 || template.Overrides.Count > 0
            || template.CollectionOrder.Count > 0 || template.BurnedIn.Count > 0 || !template.Locks.IsEmpty
            || !template.Enabled || template.HandleNextEpisode || template.PerLibrary.HasValue))
        {
            Retired.NewUserTemplate = template.Clone();
            NewUserTemplate = new PersonSettings { Everyday = new SettingsLayer { Understood = template.Everyday.Understood?.ToArray() } };
            notes.Add("Hidden new-user defaults were archived; My languages was kept.");
        }
        if (notes.Count > 0) LastUpgradeNote = string.Join("\n", notes);
    }

    private void ArchiveLayer(SettingsLayer layer, HashSet<string> visible, string location, List<string> notes)
    {
        var archived = new SettingsLayer();
        foreach (var field in layer.SetFields().ToArray())
        {
            if (visible.Contains(field)) continue;
            archived.CopyField(layer, field);
            layer.Clear(field);
        }
        if (archived.IsEmpty) return;
        Retired.Layers.Add(new RetiredLayer { Location = location, Settings = archived });
        notes.Add("Hidden settings at " + location + " were archived and no longer apply.");
    }
}
