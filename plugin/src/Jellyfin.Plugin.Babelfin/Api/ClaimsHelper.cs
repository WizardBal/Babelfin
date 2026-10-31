// Claim names adapted from Jellyfin's InternalClaimTypes.cs:
// https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/Jellyfin.Api/Constants/InternalClaimTypes.cs
// See NOTICE for provenance and licensing.
using System;
using System.Linq;
using System.Security.Claims;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>
/// Reads Jellyfin's own claims. The server's extension methods live in Jellyfin.Api, which plugins can't
/// reference, so the claim names are copied from Jellyfin.Api/Constants/InternalClaimTypes.cs.
/// </summary>
public static class ClaimsHelper
{
    /// <summary>The user id claim.</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>The device id claim.</summary>
    public const string DeviceIdClaim = "Jellyfin-DeviceId";

    /// <summary>The client name claim.</summary>
    public const string ClientClaim = "Jellyfin-Client";

    /// <summary>
    /// Gets the signed-in user's id.
    /// </summary>
    /// <param name="principal">The request's user.</param>
    /// <returns>The id, or null when absent, empty or unparsable.</returns>
    public static Guid? GetUserId(ClaimsPrincipal? principal)
    {
        var value = GetClaim(principal, UserIdClaim);
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;
    }

    /// <summary>
    /// Gets the device id.
    /// </summary>
    /// <param name="principal">The request's user.</param>
    /// <returns>The device id, or null.</returns>
    public static string? GetDeviceId(ClaimsPrincipal? principal) => GetClaim(principal, DeviceIdClaim);

    /// <summary>
    /// Gets the client (app) name.
    /// </summary>
    /// <param name="principal">The request's user.</param>
    /// <returns>The client name, or null.</returns>
    public static string? GetClient(ClaimsPrincipal? principal) => GetClaim(principal, ClientClaim);

    private static string? GetClaim(ClaimsPrincipal? principal, string name)
        => principal?.Claims.FirstOrDefault(c => c.Type.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
