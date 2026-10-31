using System;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>Seeds test and harness documents through the store's normal update and preservation behavior.</summary>
public static class PersonStoreSeedExtensions
{
    public static void Save(this IPersonStore store, PersonSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        var saved = store is IExplicitPersonUpdates explicitStore
            ? explicitStore.UpdateExplicit(settings.UserId, _ => settings)
            : store.Update(settings.UserId, _ => settings);
        if (!saved)
        {
            throw new InvalidOperationException("The store refused to seed this test document.");
        }
    }
}
