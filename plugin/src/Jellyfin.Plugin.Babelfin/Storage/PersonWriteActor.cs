using System;
using System.Security.Claims;
using System.Threading;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;

namespace Jellyfin.Plugin.Babelfin.Storage;

// The request actor flows through asynchronous controller work without entering exported configuration.
internal sealed class PersonWriteActor : IDisposable
{
    private static readonly AsyncLocal<PersonWriteActor?> Current = new();
    private readonly PersonWriteActor? _previous;
    private readonly string _id;
    private readonly string? _name;
    private PersonWriteActor(string id, string? name)
    {
        _previous = Current.Value; _id = id; _name = name; Current.Value = this;
    }
    internal static PersonWriteActor Admin(ClaimsPrincipal? principal) => new("admin:" + (ClaimsHelper.GetUserId(principal) ?? Guid.Empty).ToString("N"), principal?.Identity?.Name);
    internal static PersonWriteActor System() => new("system", null);
    internal static void Stamp(PersonSettings document, DateTime now)
    {
        document.LastChangedAt = now;
        document.LastChangedBy = Current.Value?._id ?? document.UserId.ToString("N");
        document.LastChangedByName = Current.Value?._name ?? (Current.Value is null ? document.UserName : null);
    }
    public void Dispose() => Current.Value = _previous;
}
