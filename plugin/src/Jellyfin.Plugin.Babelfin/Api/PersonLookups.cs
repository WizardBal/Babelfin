using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Babelfin.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// Looks up items and their libraries as one person sees them, for the "Show and movie overrides" list and the dialog's target. Made for one request: the
/// libraries the person can see are found once, and a library answer is kept for every item under the same parent.
/// </summary>
/// <remarks>
/// Behavior:
/// <list type="bullet">
/// <item>Items come from <see cref="ILibraryManager.GetItemList(InternalItemsQuery)"/> in batches of <see cref="BatchSize"/> ids (stored columns only, no images
/// or user data), not one <c>GetItemById</c> each. The user query applies item rating and tag restrictions. Episode and season parents are
/// fetched in batches and checked once per parent with <see cref="BaseItem.IsVisibleStandalone"/> for ancestor and library restrictions;
/// other item types retain the standalone visibility check. Hidden parents are never treated as missing restrictions.</item>
/// <item>An item's libraries are the ones <see cref="ItemContextBuilder.Libraries"/> finds, minus the libraries the person can't see: an item that sits in
/// two libraries never shows the name of one the person has no access to. Playback's own context still sees every library.</item>
/// <item>Items with the same parent share their libraries (a show's episodes, a library's movies), so a list of thousands of overrides asks Jellyfin for
/// the libraries of a handful of parents.</item>
/// </list>
/// </remarks>
internal sealed class PersonLookups
{
    /// <summary>The most item ids asked for in one query (a query with thousands of ids is slow in some databases).</summary>
    internal const int BatchSize = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly JellyfinUser _user;
    private readonly Lazy<HashSet<Guid>> _visibleLibraries;
    private readonly Dictionary<Guid, (Guid[] Ids, string[] Names)> _byParent = new();
    private readonly Dictionary<Guid, bool> _visibleParents = new();

    /// <summary>Initializes a new instance of the <see cref="PersonLookups"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="user">The person (the items are looked up as them).</param>
    internal PersonLookups(ILibraryManager libraryManager, JellyfinUser user)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(user);
        _libraryManager = libraryManager;
        _user = user;
        _visibleLibraries = new Lazy<HashSet<Guid>>(() => PersonDocuments.VisibleLibraryIds(libraryManager, user));
    }

    /// <summary>Finds the items among some ids that the person can see.</summary>
    /// <param name="ids">The ids (empty ones and repeats are skipped).</param>
    /// <param name="kinds">Only items of these types (none: any type).</param>
    /// <returns>The items by id; an id that is gone, hidden from the person or of another type is missing.</returns>
    internal Dictionary<Guid, BaseItem> Items(IEnumerable<Guid> ids, params BaseItemKind[] kinds)
    {
        var found = new Dictionary<Guid, BaseItem>();
        foreach (var batch in ids.Where(id => id != Guid.Empty).Distinct().Chunk(BatchSize))
        {
            var query = new InternalItemsQuery(_user)
            {
                ItemIds = batch,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
            };
            if (kinds.Length > 0)
            {
                query.IncludeItemTypes = kinds;
            }

            var items = _libraryManager.GetItemList(query);
            var shared = items.Where(UsesParentVisibility).ToArray();
            var sharedIds = shared.Select(item => item.Id).ToHashSet();
            var parents = shared.Select(item => item.ParentId)
                .Where(id => !_visibleParents.ContainsKey(id)).Distinct().ToArray();
            foreach (var parentBatch in parents.Chunk(BatchSize))
            {
                var parentQuery = new InternalItemsQuery(_user)
                {
                    ItemIds = parentBatch,
                    DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
                };
                foreach (var id in parentBatch) _visibleParents[id] = false;
                foreach (var parent in _libraryManager.GetItemList(parentQuery))
                    _visibleParents[parent.Id] = parent.IsVisibleStandalone(_user);
            }

            foreach (var item in items)
            {
                if (item is not null && (sharedIds.Contains(item.Id) ? _visibleParents[item.ParentId] : item.IsVisibleStandalone(_user)))
                {
                    found[item.Id] = item;
                }
            }
        }

        return found;
    }

    // Jellyfin's user query filters the item's own ratings/tags. The extra standalone check is needed for its ancestors and libraries,
    // which are shared by siblings. Keep overrides of standalone visibility, channel items and root items on Jellyfin's full path.
    private static bool UsesParentVisibility(BaseItem item)
        => item is not null && item.ParentId != Guid.Empty && item.SourceType == SourceType.Library
            && (item.GetType() == typeof(Episode) || item.GetType() == typeof(Season));

    /// <summary>The libraries an item is in that the person can see, with their names (usually one).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The library ids and names, same order (empty when the item is in no library the person can see).</returns>
    internal (Guid[] Ids, string[] Names) LibrariesOf(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = item.ParentId;
        if (key != Guid.Empty && _byParent.TryGetValue(key, out var known))
        {
            return known;
        }

        var (ids, names) = ItemContextBuilder.Libraries(_libraryManager, item);
        var visible = _visibleLibraries.Value;
        var keep = Enumerable.Range(0, ids.Length).Where(i => visible.Contains(ids[i])).ToArray();
        var result = (keep.Select(i => ids[i]).ToArray(), keep.Select(i => names[i]).ToArray());
        if (key != Guid.Empty)
        {
            _byParent[key] = result;
        }

        return result;
    }
}
