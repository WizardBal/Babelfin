using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Storage;

/// <summary>Backup inventory includes durably quarantined documents until a valid active document repairs them; active UserIds semantics remain unchanged.</summary>
public interface IPersonStoreInventory
{
    /// <summary>Lists active and quarantined account ids, excluding archived/deleted accounts.</summary>
    IReadOnlyList<Guid> KnownUserIds();
}

internal static class PersonStoreInventory
{
    internal static IReadOnlyList<Guid> KnownIds(IPersonStore store) => store is IPersonStoreInventory inventory ? inventory.KnownUserIds() : store.UserIds();
}
