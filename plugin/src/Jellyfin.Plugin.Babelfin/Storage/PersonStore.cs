using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Storage;

/// <summary>
/// The per-person JSON files: <c>{dataFolder}/users/{userId:N}.json</c>, one
/// <see cref="PersonSettings"/> document each, written atomically through <see cref="AtomicJsonFile"/> and held in memory once read.
/// </summary>
/// <remarks>
/// Storage behavior:
/// <list type="bullet">
/// <item><see cref="Delete"/> preserves the original in a unique <c>{userId:N}.json.deleted-*</c> archive and drops the person from
/// <see cref="UserIds"/>, <see cref="TryGet"/> and the cache. A marker blocks later writes from recreating the document.</item>
/// <item><see cref="GetOrCreate"/> raises <see cref="Changed"/> when it created a document, like a save.</item>
/// <item>When the new document from <see cref="GetOrCreate"/> can't be written (disk full, permissions) it is logged and kept in
/// memory so playback still works; a later update can persist it. Updates throw when a write fails,
/// and the cache then keeps the last document that reached the disk.</item>
/// <item>A file that exists but can't be read right now (locked, permissions), or a corrupt file that couldn't be renamed, is never
/// overwritten by a seeded document: the seed is served from memory for one minute, then the file is tried again. An update
/// over a corrupt file that still can't be renamed fails rather than destroy it.</item>
/// <item>A readable document from a newer Babelfin is backed up before an explicit save. If its values cannot be decoded,
/// it is left at its original path: seeding, reset and all writes are refused until a compatible version can read it.</item>
/// <item>The document's <see cref="PersonSettings.UserId"/> always matches its file name; <see cref="Migrate"/> also repairs null
/// lists and null entries from hand-edited files. It drops overrides whose item id is empty, not a GUID or all zeros (a page could neither
/// apply nor remove such an entry) and says so once for the document, at information level.</item>
/// <item><see cref="Update"/> and <see cref="UpdateExplicit"/> read, change and write under one lock, so two writers can't overwrite each other. The change
/// runs inside the lock; one that throws saves nothing. <see cref="Changed"/> is raised after the lock is released.</item>
/// <item>A handler of <see cref="Changed"/> that throws is logged and doesn't fail the save.</item>
/// </list>
/// </remarks>
public sealed class PersonStore : IPersonStore, IExplicitPersonUpdates, IPersonStoreSafety, IPersonStoreInventory
{
    private static readonly long RetryAfterMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private readonly ILogger<PersonStore> _logger;
    private readonly string _usersFolder;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, PersonSettings> _cache = new();
    private readonly ConcurrentDictionary<Guid, Fallback> _fallback = new();
    private readonly ConcurrentDictionary<Guid, int> _newerSchema = new();
    private readonly ConcurrentDictionary<Guid, CorruptRead> _corruptInPlace = new();
    private readonly ConcurrentDictionary<Guid, bool> _deletedUsers = new();
    private readonly Dictionary<Guid, FileStamp> _unsupported = new();
    private readonly Dictionary<Guid, int> _pendingMigrations = new();
    private readonly Dictionary<Guid, FileStamp> _migrationBackups = new();
    private readonly Dictionary<Guid, Guid> _epochs = new();
    private readonly Dictionary<Guid, FileStamp> _cachedStamps = new();

    /// <summary>Initializes a new instance of the <see cref="PersonStore"/> class.</summary>
    /// <param name="dataFolder">Babelfin's data folder (<c>{DataPath}/babelfin</c>); person files go in its <c>users</c> folder, created on the first write.</param>
    /// <param name="logger">Logger.</param>
    public PersonStore(string dataFolder, ILogger<PersonStore> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataFolder);
        ArgumentNullException.ThrowIfNull(logger);
        DataFolder = dataFolder;
        _usersFolder = Path.Combine(dataFolder, "users");
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<PersonStoreChangedEventArgs>? Changed;

    /// <inheritdoc />
    public event Action<Guid>? Reactivating;

    /// <inheritdoc />
    public void Activate(Guid userId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("The person has no id", nameof(userId));
        lock (UserLifecycleTransitions.For(this))
        {
            lock (_gate)
            {
                if (!IsDeleted(userId)) return;
            }
            // Preserve a source left behind by a failed deletion before reopening this id.
            // Delete and lifecycle callbacks must run outside the document lock, like every Changed notification.
            Delete(userId);
            Reactivating?.Invoke(userId);
            lock (_gate)
            {
                File.Delete(FilePath(userId) + ".deleted");
                _epochs.Remove(userId);
                _deletedUsers.TryRemove(userId, out _);
            }
        }
        RaiseChanged(userId);
    }

    /// <inheritdoc />
    public string? Conflict(Guid userId, bool writing)
    {
        lock (_gate)
        {
            if (IsDeleted(userId)) return "This account's settings are archived. Recreate the Jellyfin account or restart Jellyfin after restoring the account; stale writes remain blocked.";
            RefreshCache(userId);
            var outcome = JsonReadOutcome.Loaded;
            var settings = _cache.TryGetValue(userId, out var cached) ? cached : Load(userId, out outcome);
            if (outcome == JsonReadOutcome.Unreadable)
                return "The existing settings are temporarily unavailable and were not replaced. Check server storage locks and permissions, then retry.";
            if (_unsupported.ContainsKey(userId) || (writing && settings is { SchemaVersion: > PersonSettings.CurrentSchemaVersion }))
                return "These settings use a newer format. Upgrade Babelfin to read or change them. The original file was left untouched.";
            if (settings is null && (outcome == JsonReadOutcome.Corrupt || HasQuarantine(userId)))
                return "These settings were damaged. The original file or a recovery archive was retained. Restore a valid settings file for this account before exporting or resetting it; the original bytes were not overwritten.";
            return null;
        }
    }

    /// <inheritdoc />
    public string DataFolder { get; }

    /// <summary>Gets the path of a person's file (admin diagnostics and tests only; never sent to a non-admin).</summary>
    /// <param name="userId">The person.</param>
    /// <returns>The path.</returns>
    public string FilePath(Guid userId) => Path.Combine(_usersFolder, userId.ToString("N", CultureInfo.InvariantCulture) + ".json");

    /// <inheritdoc />
    public PersonSettings? TryGet(Guid userId)
    {
        if (_deletedUsers.ContainsKey(userId)) return null;
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(userId, out cached))
            {
                return cached;
            }

            return Load(userId, out _);
        }
    }

    /// <inheritdoc />
    public PersonSettings GetOrCreate(Guid userId, Func<PersonSettings> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (_deletedUsers.ContainsKey(userId)) throw new IOException("This person's settings have been archived after user deletion.");
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        if (_fallback.TryGetValue(userId, out var fallback) && Environment.TickCount64 < fallback.RetryAt)
        {
            return fallback.Settings;
        }

        PersonSettings created;
        lock (_gate)
        {
            if (_cache.TryGetValue(userId, out cached))
            {
                return cached;
            }

            if (IsDeleted(userId)) throw new IOException("This person's settings have been archived after user deletion.");
            var loaded = Load(userId, out var outcome);
            if (outcome == JsonReadOutcome.Unsupported)
            {
                throw new IOException("These settings were written by a newer Babelfin and cannot be read by this version. Upgrade Babelfin; the file was left untouched.");
            }

            if (loaded is not null)
            {
                return loaded;
            }

            var candidate = create() ?? throw new InvalidOperationException("The settings factory returned null");
            if (candidate.StorageEpoch != Guid.Empty && candidate.StorageEpoch != Epoch(userId))
                throw new IOException("The account was recreated. Reload its settings before trying again.");
            created = Migrate(candidate, out var dropped);
            created.UserId = userId;
            created.StorageEpoch = Epoch(userId);
            created.UpdatedAt = DateTime.UtcNow;
            LogDropped(userId, dropped);

            var path = FilePath(userId);
            if (outcome == JsonReadOutcome.Unreadable || (outcome == JsonReadOutcome.Corrupt && File.Exists(path)))
            {
                // The file is there but unusable right now: never write a seed over it.
                _fallback[userId] = new Fallback(created, Environment.TickCount64 + RetryAfterMs);
                return created;
            }

            try
            {
                var pending = created.Clone();
                using (PersonWriteActor.System()) PersonWriteActor.Stamp(pending, pending.UpdatedAt);
                AtomicJsonFile.Write(path, pending, BabelfinJson.Options);
                created = pending;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "[Babelfin] Couldn't save the new settings of person {UserId}; they are kept in memory until the next save", userId);
            }

            _fallback.TryRemove(userId, out _);
            _cache[userId] = created;
            _cachedStamps[userId] = Stamp(path);
        }

        RaiseChanged(userId);
        return created;
    }

    /// <inheritdoc />
    public bool Update(Guid userId, Func<PersonSettings?, PersonSettings?> change) => UpdateCore(userId, change, allowNewerSchema: false);

    /// <inheritdoc />
    public bool UpdateExplicit(Guid userId, Func<PersonSettings?, PersonSettings?> change) => UpdateCore(userId, change, allowNewerSchema: true);

    private bool UpdateCore(Guid userId, Func<PersonSettings?, PersonSettings?> change, bool allowNewerSchema)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The person has no id", nameof(userId));
        }

        lock (_gate)
        {
            if (IsDeleted(userId)) return false;
            RefreshCache(userId);
            var outcome = JsonReadOutcome.Loaded;
            var current = _cache.TryGetValue(userId, out var cached) ? cached : Load(userId, out outcome);
            if (outcome == JsonReadOutcome.Unsupported) return false;
            if (!allowNewerSchema && current is { SchemaVersion: > PersonSettings.CurrentSchemaVersion }) return false;
            var changed = change(current);
            if (changed is null)
            {
                return false;
            }

            if (changed.UserId != userId)
            {
                throw new ArgumentException("The changed settings are for another person", nameof(change));
            }

            if (changed.StorageEpoch != Guid.Empty && changed.StorageEpoch != Epoch(userId)) return false;

            Write(changed);
        }

        RaiseChanged(userId);
        return true;
    }

    /// <inheritdoc />
    public bool Delete(Guid userId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("The person has no id", nameof(userId));
        var existed = false;
        try
        {
            lock (_gate)
            {
                var path = FilePath(userId);
                existed = File.Exists(path) || _cache.ContainsKey(userId) || _fallback.ContainsKey(userId);
                // Stop late writers before any I/O can fail. A failed archive must not leave the account writable.
                _deletedUsers[userId] = true;
                _cache.TryRemove(userId, out _);
                _fallback.TryRemove(userId, out _);
                _newerSchema.TryRemove(userId, out _);
                _corruptInPlace.TryRemove(userId, out _);
                _unsupported.Remove(userId);
                _pendingMigrations.Remove(userId);
                _migrationBackups.Remove(userId);
                _epochs.Remove(userId);
                _cachedStamps.Remove(userId);
                if (!File.Exists(path + ".deleted")) AtomicJsonFile.Write(path + ".deleted", userId, BabelfinJson.Options);
                if (File.Exists(path))
                {
                    var kept = path + ".deleted-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                    File.Move(path, kept);
                    _logger.LogInformation("[Babelfin] Settings of deleted person {UserId} archived as {Kept}; recovery copies require manual review and removal", userId, kept);
                }
            }
        }
        finally
        {
            // Resolver invalidation must also happen when preservation fails; the original file stays untouched.
            if (existed) RaiseChanged(userId);
        }
        return existed;
    }

    private bool IsDeleted(Guid userId)
    {
        if (_deletedUsers.ContainsKey(userId)) return true;
        var marker = FilePath(userId) + ".deleted";
        if (!File.Exists(marker) && !Directory.Exists(marker)) return false;
        _deletedUsers[userId] = true;
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> UserIds()
    {
        var ids = new HashSet<Guid>(_cache.Keys);
        try
        {
            if (Directory.Exists(_usersFolder))
            {
                foreach (var file in Directory.EnumerateFiles(_usersFolder, "*.json"))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        && Guid.TryParse(name.AsSpan(0, name.Length - ".json".Length), out var id))
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[Babelfin] Couldn't list the person files in {Folder}", _usersFolder);
        }

        return ids.Where(id => !IsDeleted(id)).OrderBy(id => id).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> KnownUserIds()
    {
        lock (_gate)
        {
            var ids = UserIds().ToHashSet();
            if (Directory.Exists(_usersFolder))
                foreach (var path in Directory.EnumerateFiles(_usersFolder, "*.json.corrupt-*"))
                {
                    var name = Path.GetFileName(path);
                    var boundary = name.IndexOf(".json.corrupt-", StringComparison.OrdinalIgnoreCase);
                    if (boundary > 0 && Guid.TryParse(name.AsSpan(0, boundary), out var id) && !IsDeleted(id) && HasQuarantine(id)) ids.Add(id);
                }
            return ids.OrderBy(id => id).ToArray();
        }
    }

    private string? QuarantineFingerprint(Guid id)
    {
        if (!Directory.Exists(_usersFolder)) return null;
        var archives = Directory.EnumerateFiles(_usersFolder, id.ToString("N") + ".json.corrupt-*")
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        // A set fingerprint, rather than the newest timestamp, also detects quarantine after a clock correction.
        return archives.Length == 0 ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", archives))));
    }

    private string? QuarantineAcknowledgement(Guid id)
    {
        var path = FilePath(id) + ".quarantine-ack";
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) return null;
        try { return JsonSerializer.Deserialize<string>(File.ReadAllBytes(path), BabelfinJson.Options); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private bool HasQuarantine(Guid id) => QuarantineFingerprint(id) is { } latest && latest != QuarantineAcknowledgement(id);

    private void AcknowledgeRepairedQuarantine(Guid id)
    {
        var latest = QuarantineFingerprint(id);
        if (latest is null || latest == QuarantineAcknowledgement(id)) return;
        try { AtomicJsonFile.Write(FilePath(id) + ".quarantine-ack", latest, BabelfinJson.Options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[Babelfin] The repaired settings of {UserId} are readable, but their quarantine acknowledgement could not be saved; recovery archives were retained", id);
        }
    }

    /// <summary>
    /// Brings a document read from disk (or imported) up to the current schema and repairs what a hand-edited file can break:
    /// null lists and settings become empty, null list entries are dropped, and so are overrides whose item id is not a usable id (empty, not a GUID or
    /// all zeros): they can never apply, and a page has no id to remove them by. Unknown JSON properties were already ignored by the reader.
    /// Schema 1 is the first version, so nothing moves yet; later versions add their steps here.
    /// </summary>
    /// <param name="settings">The document (changed in place).</param>
    /// <returns>The same document.</returns>
    public static PersonSettings Migrate(PersonSettings settings) => Migrate(settings, out _);

    private static PersonSettings Migrate(PersonSettings settings, out int droppedOverrides)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Everyday = Repair(settings.Everyday);
        settings.Locks = Repair(settings.Locks);
        settings.Libraries = (settings.Libraries ?? new List<LibraryLayer>()).Where(l => l is not null).ToList();
        var droppedLibraries = settings.Libraries.RemoveAll(row => !Guid.TryParse(row.LibraryId, out var id) || id == Guid.Empty);
        foreach (var library in settings.Libraries)
        {
            library.LibraryId ??= string.Empty;
            library.Settings = Repair(library.Settings);
        }

        settings.SmartRules = (settings.SmartRules ?? new List<SmartRule>()).Where(r => r is not null).ToList();
        foreach (var rule in settings.SmartRules)
        {
            rule.Id ??= string.Empty;
            rule.Name ??= string.Empty;
            rule.Settings = Repair(rule.Settings);
            rule.Conditions = (rule.Conditions ?? new List<RuleCondition>()).Where(c => c is not null).ToList();
            foreach (var condition in rule.Conditions)
            {
                condition.Values = (condition.Values ?? new List<string>()).Where(v => v is not null).ToList();
            }
        }

        settings.Overrides = (settings.Overrides ?? new List<ItemOverride>()).Where(o => o is not null).ToList();
        droppedOverrides = droppedLibraries + settings.Overrides.RemoveAll(o => !Guid.TryParse(o.ItemId, out var id) || id == Guid.Empty);
        foreach (var o in settings.Overrides)
        {
            o.Settings = Repair(o.Settings);
        }

        settings.CollectionOrder = (settings.CollectionOrder ?? new List<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        settings.BurnedIn = (settings.BurnedIn ?? new List<BurnedInFlag>()).Where(b => b is not null).ToList();
        foreach (var flag in settings.BurnedIn)
        {
            flag.ItemId ??= string.Empty;
            flag.Languages = (flag.Languages ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        }

        if (settings.Seed is not null)
        {
            settings.Seed.Assumed = (settings.Seed.Assumed ?? new List<AssumedLanguage>()).Where(a => a is not null).ToList();
        }

        settings.Retired = settings.Retired?.Clone() ?? new RetiredPersonSettings();
        settings.MigrationNotes ??= new List<string>();
        PersonMigrations.Upgrade(settings);
        return settings;
    }

    private static SettingsLayer Repair(SettingsLayer? layer)
    {
        layer ??= new SettingsLayer();
        layer.Understood = layer.Understood?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        layer.Reads = layer.Reads?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        layer.SubtitleLanguages = layer.SubtitleLanguages?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        layer.AudioChain = layer.AudioChain?.Where(s => s is not null).ToArray();
        return layer;
    }

    /// <summary>Writes a document and caches the saved copy. Call while holding the store's lock.</summary>
    private void Write(PersonSettings settings)
    {
        var userId = settings.UserId;
        if (IsDeleted(userId)) throw new IOException("This person's settings have been archived after user deletion.");
        var now = DateTime.UtcNow;
        settings.UpdatedAt = now;
        var copy = Migrate(settings.Clone(), out var dropped);
        copy.StorageEpoch = Epoch(userId);
        copy.SchemaVersion = PersonSettings.CurrentSchemaVersion;
        copy.UpdatedAt = now;
        PersonWriteActor.Stamp(copy, now);
        LogDropped(userId, dropped);

        var path = FilePath(userId);
        if (_pendingMigrations.TryGetValue(userId, out var oldSchema)) PreserveMigration(userId, path, oldSchema);
        if (!_cache.ContainsKey(userId) && File.Exists(path))
        {
            var outcome = AtomicJsonFile.Read<PersonSettings>(path, BabelfinJson.Options, _logger, out var onDisk, PersonSettings.CurrentSchemaVersion);
            if (outcome is JsonReadOutcome.Unreadable or JsonReadOutcome.Unsupported)
            {
                throw new IOException("The existing settings could not be read and were not overwritten");
            }

            if (onDisk is not null && onDisk.SchemaVersion != PersonSettings.CurrentSchemaVersion)
            {
                File.Copy(path, path + ".schema-" + onDisk.SchemaVersion.ToString(CultureInfo.InvariantCulture) + "-" + now.Ticks.ToString(CultureInfo.InvariantCulture) + ".bak", overwrite: false);
            }
        }

        if (_corruptInPlace.ContainsKey(userId) && File.Exists(path)
            && AtomicJsonFile.QuarantineCorrupt(path, null, _logger) is null)
        {
            throw new IOException("The person's settings file is damaged and couldn't be set aside, so it wasn't overwritten");
        }

        if (_newerSchema.TryGetValue(userId, out var newer) && File.Exists(path))
        {
            var backup = path + ".schema-" + newer.ToString(CultureInfo.InvariantCulture) + "-" + now.Ticks.ToString(CultureInfo.InvariantCulture) + ".bak";
            File.Copy(path, backup, overwrite: false);
            _logger.LogWarning("[Babelfin] Settings of person {UserId} were written by a newer Babelfin (schema {Version}); a copy was kept as {Backup}", userId, newer, backup);
        }

        try
        {
            AtomicJsonFile.Write(path, copy, BabelfinJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[Babelfin] Couldn't save the settings of person {UserId}", userId);
            throw;
        }

        _newerSchema.TryRemove(userId, out _);
        _pendingMigrations.Remove(userId);
        _migrationBackups.Remove(userId);
        _corruptInPlace.TryRemove(userId, out _);
        _fallback.TryRemove(userId, out _);
        _cache[userId] = copy;
        _cachedStamps[userId] = Stamp(path);
    }

    /// <summary>Says once, for one document, that overrides with no usable item id were dropped.</summary>
    private void LogDropped(Guid userId, int dropped)
    {
        if (dropped > 0)
        {
            _logger.LogInformation("[Babelfin] Settings of person {UserId} held {Count} override(s) or library reference(s) with no usable id; those entries were dropped after preserving any original file", userId, dropped);
        }
    }

    /// <summary>Reads a person's file into the cache. Call while holding the store's lock.</summary>
    private PersonSettings? Load(Guid userId, out JsonReadOutcome outcome)
    {
        if (IsDeleted(userId))
        {
            outcome = JsonReadOutcome.Missing;
            return null;
        }
        var path = FilePath(userId);
        if (_unsupported.TryGetValue(userId, out var stamp))
        {
            if (stamp == Stamp(path))
            {
                outcome = JsonReadOutcome.Unsupported;
                return null;
            }
            _unsupported.Remove(userId);
        }
        var sourceStamp = Stamp(path);
        if (_corruptInPlace.TryGetValue(userId, out var corrupt))
        {
            // Suppress repeated errors only for the same source, and retry a transient rename blocker after a minute.
            if (corrupt.Stamp == sourceStamp && Environment.TickCount64 < corrupt.RetryAt)
            {
                outcome = JsonReadOutcome.Corrupt;
                return null;
            }
            _corruptInPlace.TryRemove(userId, out _);
        }

        outcome = AtomicJsonFile.Read<PersonSettings>(path, BabelfinJson.Options, _logger, out var settings, PersonSettings.CurrentSchemaVersion);
        if (outcome == JsonReadOutcome.Unsupported) _unsupported[userId] = Stamp(path);
        if (outcome == JsonReadOutcome.Corrupt && File.Exists(path))
        {
            _corruptInPlace[userId] = new CorruptRead(sourceStamp, Environment.TickCount64 + RetryAfterMs);
        }

        if (outcome != JsonReadOutcome.Loaded || settings is null)
        {
            return null;
        }

        if (settings.SchemaVersion > PersonSettings.CurrentSchemaVersion)
        {
            _newerSchema[userId] = settings.SchemaVersion;
            _logger.LogWarning("[Babelfin] Settings of person {UserId} were written by a newer Babelfin (schema {Version}); settings it doesn't know are ignored", userId, settings.SchemaVersion);
        }

        var oldSchema = settings.SchemaVersion;
        var migrationNeeded = oldSchema < PersonSettings.CurrentSchemaVersion;
        var needsRepair = (settings.Overrides?.Any(row => row is null || !Guid.TryParse(row.ItemId, out var id) || id == Guid.Empty) == true)
            || (settings.Libraries?.Any(row => row is null || !Guid.TryParse(row.LibraryId, out var id) || id == Guid.Empty) == true);
        if (needsRepair)
        {
            var original = File.ReadAllBytes(path);
            var hash = SHA256.HashData(original);
            var kept = Directory.EnumerateFiles(_usersFolder, userId.ToString("N") + ".json.repaired-*.bak")
                .Any(backup => SHA256.HashData(File.ReadAllBytes(backup)).SequenceEqual(hash));
            if (!kept)
                File.Copy(path, path + ".repaired-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ".bak", overwrite: false);
        }
        Migrate(settings, out var dropped);
        settings.UserId = userId;
        settings.StorageEpoch = Epoch(userId);
        if (migrationNeeded)
        {
            _pendingMigrations[userId] = oldSchema;
            try
            {
                PreserveMigration(userId, path, oldSchema);
                var migrated = settings.Clone();
                using (PersonWriteActor.System()) PersonWriteActor.Stamp(migrated, DateTime.UtcNow);
                AtomicJsonFile.Write(path, migrated, BabelfinJson.Options);
                settings = migrated;
                _pendingMigrations.Remove(userId);
                _migrationBackups.Remove(userId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Serve the decoded document without repeating backups or writes on every read. A save retries under the lock.
                _logger.LogWarning(ex, "[Babelfin] Migration of person {UserId} could not be saved; original data is retained. Check storage permissions and retry a settings save", userId);
            }
        }
        LogDropped(userId, dropped);
        _fallback.TryRemove(userId, out _);
        _cache[userId] = settings;
        _cachedStamps[userId] = Stamp(path);
        if (settings.SchemaVersion <= PersonSettings.CurrentSchemaVersion) AcknowledgeRepairedQuarantine(userId);
        return settings;
    }

    private void RaiseChanged(Guid userId)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new PersonStoreChangedEventArgs(userId);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<PersonStoreChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Babelfin] A settings-changed handler failed for person {UserId}", userId);
            }
        }
    }

    private sealed record Fallback(PersonSettings Settings, long RetryAt);

    private sealed record CorruptRead(FileStamp Stamp, long RetryAt);

    private Guid Epoch(Guid userId)
    {
        if (!_epochs.TryGetValue(userId, out var epoch)) _epochs[userId] = epoch = Guid.NewGuid();
        return epoch;
    }

    private void RefreshCache(Guid userId)
    {
        if (_cachedStamps.TryGetValue(userId, out var stamp) && stamp != Stamp(FilePath(userId)))
        {
            _cache.TryRemove(userId, out _);
            _cachedStamps.Remove(userId);
            _pendingMigrations.Remove(userId);
            _migrationBackups.Remove(userId);
            _newerSchema.TryRemove(userId, out _);
        }
    }

    private void PreserveMigration(Guid userId, string path, int schema)
    {
        var stamp = Stamp(path);
        if (_migrationBackups.TryGetValue(userId, out var kept) && kept == stamp) return;
        File.Copy(path, path + ".schema-" + schema.ToString(CultureInfo.InvariantCulture) + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ".bak", overwrite: false);
        _migrationBackups[userId] = stamp;
    }

    private static FileStamp Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc) : new FileStamp(-1, default);
    }

    private sealed record FileStamp(long Length, DateTime WrittenAt);
}
