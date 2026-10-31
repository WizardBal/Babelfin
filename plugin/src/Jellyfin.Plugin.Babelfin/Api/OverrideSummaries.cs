using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>What the "Show and movie overrides" list knows about the item an override is stored for.</summary>
/// <param name="Name">The item's current name.</param>
/// <param name="Type">The item's current type ("Movie", "Series"…).</param>
/// <param name="ProductionYear">The production year, or null.</param>
/// <param name="SeriesName">The show's name for a season or an episode, or null.</param>
/// <param name="LibraryId">The library's id, or null when the item is in none.</param>
/// <param name="LibraryName">The library's name, or null.</param>
public sealed record OverrideItemFacts(string? Name, string? Type, int? ProductionYear, string? SeriesName, Guid? LibraryId, string? LibraryName);

/// <summary>
/// Builds the rows of the "Show and movie overrides" list from a person's stored overrides. The person's page and the admin's
/// People editor both ask for them, so the rows are made in this one place. The items are looked up as the person: an item they can no longer see
/// is "gone" here (its row keeps the stored name and has no type or library).
/// </summary>
public static class OverrideSummaries
{
    /// <summary>The name a row shows: the item's current name, else the name stored with the override, else nothing.</summary>
    /// <param name="current">The item's current name, or null when it is gone.</param>
    /// <param name="stored">The name stored with the override.</param>
    /// <returns>The name (empty when neither is known).</returns>
    public static string NameOf(string? current, string? stored)
        => !string.IsNullOrWhiteSpace(current) ? current : !string.IsNullOrWhiteSpace(stored) ? stored : string.Empty;

    /// <summary>The name an activity entry about an override uses: <see cref="NameOf"/>, else "a title".</summary>
    /// <param name="current">The item's current name, or null when it is gone.</param>
    /// <param name="stored">The name stored with the override.</param>
    /// <returns>The name; never empty.</returns>
    public static string ActivityNameOf(string? current, string? stored)
    {
        var name = NameOf(current, stored);
        return name.Length > 0 ? name : "a title";
    }

    /// <summary>Makes one row from a stored override and what is known about its item.</summary>
    /// <param name="stored">The stored override.</param>
    /// <param name="facts">What is known about the item now, or null when it is gone (or the person can't see it).</param>
    /// <returns>The row; its settings and remembered track change are copies of the stored ones.</returns>
    public static OverrideSummary Compose(ItemOverride stored, OverrideItemFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var id = Guid.TryParse(stored.ItemId, out var parsed) && parsed != Guid.Empty ? parsed.ToString("N") : stored.ItemId;
        return new OverrideSummary(
            id,
            stored.Scope,
            NameOf(facts?.Name, stored.Name),
            facts?.Type,
            facts?.ProductionYear,
            facts?.SeriesName,
            facts?.LibraryId?.ToString("N"),
            facts?.LibraryName,
            stored.Settings?.Clone() ?? new SettingsLayer(),
            stored.Manual?.Clone(),
            stored.UpdatedAt);
    }

    /// <summary>
    /// Makes the rows for a person's stored overrides, in the order they are stored. Remembered track changes are not capped (only explicit settings are, at
    /// <see cref="SettingsValidator.MaxOverrides"/>), so a person can have thousands of entries: the items are fetched in batches and a library is looked up
    /// once for the items under one parent (<see cref="PersonLookups"/>), never once per entry. An entry with no usable item id (it can never apply, and a page
    /// has no id to remove it by) gets no row.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="user">The person (the items are looked up as them).</param>
    /// <param name="document">The person's stored document, or null when they have none.</param>
    /// <returns>The rows.</returns>
    internal static List<OverrideSummary> Build(ILibraryManager libraryManager, JellyfinUser user, PersonSettings? document)
    {
        var entries = new List<(ItemOverride Stored, Guid Id)>();
        foreach (var stored in document?.Overrides ?? new List<ItemOverride>())
        {
            if (stored is not null && Guid.TryParse(stored.ItemId, out var id) && id != Guid.Empty)
            {
                entries.Add((stored, id));
            }
        }

        var rows = new List<OverrideSummary>(entries.Count);
        if (entries.Count == 0)
        {
            return rows;
        }

        var lookups = new PersonLookups(libraryManager, user);
        var items = lookups.Items(entries.Select(e => e.Id));
        foreach (var (stored, id) in entries)
        {
            rows.Add(Compose(stored, items.TryGetValue(id, out var item) ? FactsOf(lookups, item) : null));
        }

        return rows;
    }

    private static OverrideItemFacts FactsOf(PersonLookups lookups, BaseItem item)
    {
        var (libraryIds, libraryNames) = lookups.LibrariesOf(item);
        var seriesName = item switch
        {
            Episode episode => episode.SeriesName,
            Season season => season.SeriesName,
            _ => null,
        };
        return new OverrideItemFacts(
            item.Name,
            item.GetBaseItemKind().ToString(),
            item.ProductionYear,
            seriesName,
            libraryIds.Length > 0 ? libraryIds[0] : null,
            libraryNames.Length > 0 ? libraryNames[0] : null);
    }
}
