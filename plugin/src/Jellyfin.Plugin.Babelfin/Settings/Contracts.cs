using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>Raised when a person's file changed (or every file, when <see cref="UserId"/> is null).</summary>
/// <param name="UserId">The person, or null for all.</param>
public sealed record PersonStoreChangedEventArgs(Guid? UserId);

/// <summary>
/// Per-person JSON files under the data folder: atomic writes, in-memory cache, schema migration.
/// Implemented in <c>Storage/PersonStore.cs</c>; registered as a singleton.
/// </summary>
public interface IPersonStore
{
    /// <summary>Raised after a save, reset or delete.</summary>
    event EventHandler<PersonStoreChangedEventArgs>? Changed;

    /// <summary>Gets the folder the files live in (admin diagnostics only; never sent to a non-admin).</summary>
    string DataFolder { get; }

    /// <summary>Gets a person's settings without creating them.</summary>
    /// <param name="userId">The person.</param>
    /// <returns>The settings, or null when the person has no file.</returns>
    PersonSettings? TryGet(Guid userId);

    /// <summary>Gets a person's settings, creating and saving them from <paramref name="create"/> when they have no file yet.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="create">Builds the initial document (the seed, with the template applied when one exists).</param>
    /// <returns>The settings (a shared cached instance: callers must not mutate it; clone changes inside <see cref="Update"/>).</returns>
    PersonSettings GetOrCreate(Guid userId, Func<PersonSettings> create);

    /// <summary>
    /// Changes a person's settings as one step: reads the stored document, lets <paramref name="change"/> make the new one and saves it, while no other
    /// update of any person can fall in between. Derive changes from the document passed to the callback so concurrent writers preserve each other's edits.
    /// </summary>
    /// <param name="userId">The person.</param>
    /// <param name="change">
    /// Gets the stored document (a shared cached instance: it must not be changed; clone it first) or null when the person has none, and returns the document
    /// to save for the same person, or null to save nothing. It runs inside the store's lock, so it must be quick, free of side effects and must not call
    /// the store. When it throws, nothing is saved and the exception reaches the caller.
    /// </param>
    /// <returns>True when a document was saved.</returns>
    bool Update(Guid userId, Func<PersonSettings?, PersonSettings?> change);

    /// <summary>Archives a deleted person's original file and blocks later writes from recreating it.</summary>
    /// <param name="userId">The person.</param>
    /// <returns>True when a file existed.</returns>
    bool Delete(Guid userId);

    /// <summary>Lists every person with a file.</summary>
    /// <returns>Their ids.</returns>
    IReadOnlyList<Guid> UserIds();
}

/// <summary>Optional lifecycle and diagnostic capabilities of the disk store; ordinary writers cannot reactivate deleted accounts.</summary>
public interface IPersonStoreSafety
{
    /// <summary>Raised before a trusted reactivation, allowing activity entries for the person again.</summary>
    event Action<Guid>? Reactivating;

    /// <summary>Starts fresh after authoritative account creation or startup reconciliation. Recovery archives remain archived.</summary>
    void Activate(Guid userId);

    /// <summary>Gets an actionable storage conflict, or null when the requested access is supported.</summary>
    string? Conflict(Guid userId, bool writing);
}

/// <summary>
/// Library facts computed in the background and cached in <c>library-facts.json</c>.
/// Implemented in <c>Library/LibraryFacts.cs</c>; registered as a singleton.
/// </summary>
public interface ILibraryFacts
{
    /// <summary>Raised after a refresh.</summary>
    event EventHandler? Changed;

    /// <summary>Gets when the last full refresh finished (UTC), or null.</summary>
    DateTime? ComputedAt { get; }

    /// <summary>Gets every known library's facts.</summary>
    IReadOnlyList<LibraryFact> All { get; }

    /// <summary>Gets one library's facts.</summary>
    /// <param name="libraryId">The library id.</param>
    /// <returns>The facts, or null when not computed yet.</returns>
    LibraryFact? Get(Guid libraryId);

    /// <summary>Recomputes every library's facts now.</summary>
    /// <param name="progress">Optional progress (0–100).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Schedules a background refresh soon (debounced); returns at once.</summary>
    void RequestRefresh();
}

/// <summary>Builds an <see cref="ItemContext"/> from a Jellyfin item. Implemented in <c>Library/ItemContextBuilder.cs</c>.</summary>
public interface IItemContextBuilder
{
    /// <summary>Builds the context for an item.</summary>
    /// <param name="item">The item (any <see cref="BaseItem"/>; Video, Movie, Episode, Season, Series and BoxSet are understood).</param>
    /// <param name="user">The person (needed for collection membership).</param>
    /// <param name="client">The requesting app's name when known.</param>
    /// <returns>The context.</returns>
    ItemContext Build(BaseItem item, User user, string? client = null);
}

/// <summary>The outcome of resolving a person's layers for an item.</summary>
/// <param name="Profile">The engine profile (its FieldSources carry the provenance).</param>
/// <param name="Sources">Provenance per field: "your Anime library rule", "the admin's everyday defaults", "an admin lock", "the smart starter"… (same dictionary as the profile's).</param>
/// <param name="PersonEnabled">Whether Babelfin acts for this person at all.</param>
/// <param name="Assumed">Languages Babelfin assumed for this person and hasn't had confirmed.</param>
/// <param name="LibraryIsOriginalAudio">Whether the item's library counts as original-audio (starter detection or the admin's choice).</param>
/// <param name="LibraryUsualOriginalLanguage">The library's usual original language, or null.</param>
/// <param name="BurnedInLanguages">Burned-in languages that apply to this item for this person (their own flags and the shared ones).</param>
/// <param name="Manual">The remembered manual change that applies (most specific scope), or null.</param>
/// <param name="Context">The item context that was resolved.</param>
public sealed record ResolvedProfileResult(
    ResolvedProfile Profile,
    IReadOnlyDictionary<string, string> Sources,
    bool PersonEnabled,
    IReadOnlyList<AssumedLanguage> Assumed,
    bool LibraryIsOriginalAudio,
    string? LibraryUsualOriginalLanguage,
    IReadOnlyList<string> BurnedInLanguages,
    ManualOverride? Manual,
    ItemContext Context);

/// <summary>
/// Turns a person's layers and the admin's into one <see cref="ResolvedProfile"/> for an item, with a compiled-profile
/// cache invalidated on every settings save. Implemented in <c>Resolution/CachedProfileResolver.cs</c>; registered as a singleton.
/// </summary>
public interface IProfileResolver
{
    /// <summary>Resolves for a Jellyfin item (cached per person and item).</summary>
    /// <param name="user">The person.</param>
    /// <param name="item">The item.</param>
    /// <param name="client">The requesting app's name when known.</param>
    /// <returns>The result.</returns>
    ResolvedProfileResult Resolve(User user, BaseItem item, string? client = null);

    /// <summary>Resolves for a prepared context (not cached).</summary>
    /// <param name="user">The person.</param>
    /// <param name="context">The context (<see cref="ItemContext.None"/> for the everyday layers alone).</param>
    /// <returns>The result.</returns>
    ResolvedProfileResult Resolve(User user, ItemContext context);

    /// <summary>Drops cached profiles for one person, or for everyone.</summary>
    /// <param name="userId">The person, or null for all.</param>
    void Invalidate(Guid? userId);
}

/// <summary>One line of Babelfin's activity log. Never carries a file path.</summary>
/// <param name="At">When (UTC).</param>
/// <param name="Kind">One of <see cref="ActivityKinds"/>.</param>
/// <param name="UserId">The person concerned, or null.</param>
/// <param name="ItemId">The item concerned, or null.</param>
/// <param name="Text">One sentence for people.</param>
/// <param name="Details">Small named values (indices, languages, counts).</param>
public sealed record ActivityEntry(DateTime At, string Kind, Guid? UserId, Guid? ItemId, string Text, IReadOnlyDictionary<string, string>? Details = null);

/// <summary>The fixed kinds of activity entries.</summary>
public static class ActivityKinds
{
    /// <summary>A manual track change was remembered as an override.</summary>
    public const string OverrideCreated = "override-created";

    /// <summary>An app's own track choice was trusted.</summary>
    public const string ManualTrusted = "manual-trusted";

    /// <summary>A track-switch command was sent after playback started.</summary>
    public const string CorrectionSent = "correction-sent";

    /// <summary>Settings were changed through the API.</summary>
    public const string SettingsChanged = "settings-changed";

    /// <summary>Babelfin changed a Jellyfin setting (remember flags or compatibility selections).</summary>
    public const string JellyfinSettingChanged = "jellyfin-setting-changed";

    /// <summary>Babelfin restored a Jellyfin setting or remembered selection it had changed.</summary>
    public const string Restored = "restored";

}

/// <summary>Babelfin's recent activity, held in memory until restart. Implemented in <c>Activity/ActivityLog.cs</c>.</summary>
public interface IActivityLog
{
    /// <summary>Appends an entry (never throws).</summary>
    /// <param name="entry">The entry.</param>
    void Add(ActivityEntry entry);

    /// <summary>Reads the newest entries first.</summary>
    /// <param name="limit">At most this many.</param>
    /// <param name="kind">Only this kind, or null.</param>
    /// <param name="userId">Only this person, or null.</param>
    /// <returns>The entries.</returns>
    IReadOnlyList<ActivityEntry> Query(int limit, string? kind = null, Guid? userId = null);
}

/// <summary>Compatibility mode: writes Babelfin's choice into Jellyfin's remembered selection and can undo every write. Implemented in <c>Compat/CompatibilityWriter.cs</c>.</summary>
public interface ICompatibilityWriter
{
    /// <summary>Gets the number of remembered selections Babelfin has written and not yet undone.</summary>
    int LedgerCount { get; }

    /// <summary>
    /// Permanently rejects queued and future writes for this instance without waiting for an in-flight write.
    /// Call before shutdown undo; undo waits for any admitted write to finish before reading its recovery record.
    /// Ordinary undo does not stop writes.
    /// </summary>
    void StopWrites();

    /// <summary>Writes the choice for a person and item, recording the previous values the first time.</summary>
    /// <param name="user">The person.</param>
    /// <param name="item">The item.</param>
    /// <param name="audioIndex">The audio index to remember.</param>
    /// <param name="subtitleIndex">The subtitle index to remember (-1 = off).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task WriteAsync(User user, BaseItem item, int? audioIndex, int? subtitleIndex, CancellationToken cancellationToken);

    /// <summary>Restores every remembered selection Babelfin wrote (where it is still Babelfin's value) and clears the ledger.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task UndoAllAsync(CancellationToken cancellationToken);
}

/// <summary>The person's "let Babelfin handle next-episode tracks" switch: turns their Jellyfin remember flags off and restores them later. Implemented in <c>Compat/RememberFlags.cs</c>.</summary>
public interface IRememberFlags
{
    /// <summary>Turns the person's remember flags off, keeping the old values in their document.</summary>
    /// <param name="user">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task ApplyAsync(User user, CancellationToken cancellationToken);

    /// <summary>Restores the person's remember flags from their document, if Babelfin changed them.</summary>
    /// <param name="user">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RestoreAsync(User user, CancellationToken cancellationToken);

    /// <summary>Stops accepting changes to Jellyfin's remember flags for this service instance.</summary>
    void StopWrites();

    /// <summary>Restores every person's flags and clears their records, leaving each person's next-episode preference as stored.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RestoreAllAsync(CancellationToken cancellationToken);
}

/// <summary>One live play as the registry sees it, for the dashboard.</summary>
/// <param name="SessionId">The Jellyfin session id.</param>
/// <param name="UserId">The person.</param>
/// <param name="Client">The app name.</param>
/// <param name="DeviceName">The device name.</param>
/// <param name="ItemId">The item playing, or the last one.</param>
/// <param name="ItemName">Its name.</param>
/// <param name="MediaSourceId">The media source.</param>
/// <param name="ExpectedAudio">The expected audio index.</param>
/// <param name="ExpectedSubtitle">The expected subtitle index.</param>
/// <param name="LastAudio">The last reported audio index.</param>
/// <param name="LastSubtitle">The last reported subtitle index.</param>
/// <param name="StartedAt">When the play started (UTC).</param>
/// <param name="LastProgressAt">The last progress report (UTC).</param>
/// <param name="Trusted">Whether the app's own choice was trusted (never corrected).</param>
/// <param name="ManualChanged">Whether a manual change was recorded during this play.</param>
/// <param name="CorrectionSentAt">When a track-switch command was sent, or null.</param>
public sealed record PlayEntry(
    string SessionId,
    Guid UserId,
    string? Client,
    string? DeviceName,
    Guid? ItemId,
    string? ItemName,
    string? MediaSourceId,
    int? ExpectedAudio,
    int? ExpectedSubtitle,
    int? LastAudio,
    int? LastSubtitle,
    DateTime? StartedAt,
    DateTime? LastProgressAt,
    bool Trusted,
    bool ManualChanged,
    DateTime? CorrectionSentAt);

/// <summary>Read access to the play registry. Implemented in <c>Playback/PlayRegistry.cs</c>.</summary>
public interface IPlayRegistry
{
    /// <summary>A snapshot of the live entries.</summary>
    /// <returns>The entries.</returns>
    IReadOnlyList<PlayEntry> Snapshot();
}
