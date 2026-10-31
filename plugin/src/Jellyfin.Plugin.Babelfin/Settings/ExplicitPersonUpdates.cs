using System;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>Separates an explicit settings save from background updates to a future-schema document.</summary>
public interface IExplicitPersonUpdates
{
    bool UpdateExplicit(Guid userId, Func<PersonSettings?, PersonSettings?> change);
}

internal static class ExplicitPersonUpdates
{
    internal static bool Update(IPersonStore store, Guid userId, Func<PersonSettings?, PersonSettings?> change)
        => store is IExplicitPersonUpdates explicitStore ? explicitStore.UpdateExplicit(userId, change) : store.Update(userId, change);
}
