using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Storage;

/// <summary>What <see cref="AtomicJsonFile.Read{T}"/> found.</summary>
public enum JsonReadOutcome
{
    /// <summary>No file: "no data".</summary>
    Missing,

    /// <summary>The file was read and parsed.</summary>
    Loaded,

    /// <summary>The file didn't parse; it was renamed <c>*.corrupt-{ticks}</c> (never deleted) and is treated as missing.</summary>
    Corrupt,

    /// <summary>The file exists but couldn't be read right now (locked, permissions); it was left alone and must not be overwritten.</summary>
    Unreadable,

    /// <summary>A newer schema contains values this version cannot decode; the original is left untouched.</summary>
    Unsupported,
}

/// <summary>
/// Reads and writes Babelfin's JSON files safely.
/// <list type="bullet">
/// <item>Writes go to <c>{file}.{random}.tmp</c> in the same folder, are flushed to disk, then moved over the file with
/// <see cref="File.Move(string, string, bool)"/>, so a reader sees the old file or the new one, never half of one.</item>
/// <item>Temp files left behind by a crash are removed on the next write of that same file only.</item>
/// <item>Backups go through <see cref="WriteNew{T}"/>, which never replaces a file: it takes the first free name.</item>
/// <item>A file that fails to parse is logged once and renamed <c>{file}.corrupt-{utc ticks}</c>; it is never deleted.</item>
/// </list>
/// </summary>
/// <remarks>
/// Behavior: a JSON <c>null</c> document counts as corrupt; an I/O error while reading (a locked file,
/// missing permissions) is <see cref="JsonReadOutcome.Unreadable"/>, not corrupt, so the file is neither renamed nor overwritten.
/// Callers serialise writes to one file themselves (one lock per store).
/// </remarks>
public static class AtomicJsonFile
{
    /// <summary>Reads and parses a JSON file.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="options">The serializer options.</param>
    /// <param name="logger">Logger (errors for corrupt files, warnings for unreadable ones).</param>
    /// <param name="value">The document when <see cref="JsonReadOutcome.Loaded"/>, else null.</param>
    /// <param name="supportedSchemaVersion">When supplied, preserves undecodable documents with a newer schema instead of quarantining them.</param>
    /// <returns>What was found.</returns>
    public static JsonReadOutcome Read<T>(string path, JsonSerializerOptions options, ILogger logger, out T? value, int? supportedSchemaVersion = null)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        value = null;

        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return JsonReadOutcome.Missing;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return JsonReadOutcome.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return JsonReadOutcome.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[Babelfin] Couldn't read {File}; it was left as it is", path);
            return JsonReadOutcome.Unreadable;
        }

        var newerSchema = false;
        var schemaDeclarations = 0;
        try
        {
            // Inspect the envelope before enum converters or changed field types can fail. Use the same bytes and
            // parsing conventions as the typed reader. Any declared newer version requires preservation.
            if (supportedSchemaVersion.HasValue)
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = options.AllowTrailingCommas,
                    CommentHandling = options.ReadCommentHandling,
                });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase))
                        {
                            schemaDeclarations++;
                            var version = property.Value;
                            var number = version.ValueKind == JsonValueKind.Number ? version.GetDouble()
                                : version.ValueKind == JsonValueKind.String && double.TryParse(version.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                            newerSchema |= number > supportedSchemaVersion.Value;
                        }
                    }
                }
            }

            if (newerSchema && schemaDeclarations > 1)
            {
                logger.LogWarning("[Babelfin] {File} declares a newer schema more than once; ambiguous settings were left untouched", path);
                return JsonReadOutcome.Unsupported;
            }

            var readOptions = options;
            if (newerSchema)
            {
                // Numeric enum values can otherwise deserialize successfully even when this build has no such member.
                readOptions = new JsonSerializerOptions(options);
                readOptions.Converters.Insert(0, new JsonStringEnumConverter(allowIntegerValues: false));
            }
            value = JsonSerializer.Deserialize<T>(bytes, readOptions);
            if (value is not null)
            {
                return JsonReadOutcome.Loaded;
            }

            QuarantineCorrupt(path, null, logger);
            return JsonReadOutcome.Corrupt;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            value = null;
            if (newerSchema)
            {
                logger.LogWarning(ex, "[Babelfin] {File} uses a newer settings schema with values this version cannot read; it was left untouched. Upgrade Babelfin to access it", path);
                return JsonReadOutcome.Unsupported;
            }

            QuarantineCorrupt(path, ex, logger);
            return JsonReadOutcome.Corrupt;
        }
    }

    /// <summary>Serializes a document and replaces the file atomically. Creates the folder when needed.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="value">The document.</param>
    /// <param name="options">The serializer options.</param>
    /// <exception cref="IOException">The file couldn't be written (the old file is left untouched).</exception>
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        RemoveStaleTempFiles(folder, Path.GetFileName(path));

        var temp = Path.Combine(folder, Path.GetFileName(path) + "." + Path.GetRandomFileName().Replace(".", string.Empty, StringComparison.Ordinal) + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Serializes a document to a new file, never replacing one: <c>{name}.json</c> in the folder, else <c>{name}-2.json</c>, <c>{name}-3.json</c>…
    /// The content is written to a temp file and flushed first, so the new file is never half written. Creates the folder when needed.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="folder">The folder.</param>
    /// <param name="name">The file name without its ".json" extension.</param>
    /// <param name="value">The document.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The full path of the new file.</returns>
    /// <exception cref="IOException">The file couldn't be written (no file is left behind).</exception>
    public static string WriteNew<T>(string folder, string name, T value, JsonSerializerOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);

        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, name + ".json." + Path.GetRandomFileName().Replace(".", string.Empty, StringComparison.Ordinal) + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            for (var n = 1; ; n++)
            {
                var path = Path.Combine(folder, n == 1 ? name + ".json" : name + "-" + n.ToString(CultureInfo.InvariantCulture) + ".json");
                if (File.Exists(path))
                {
                    continue;
                }

                try
                {
                    File.Move(temp, path, overwrite: false);
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another file took the name in the meantime: the next name is tried.
                }
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Renames a file that failed to parse to <c>{file}.corrupt-{utc ticks}</c> and logs it. Never deletes it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="error">The parse error, or null.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The new name, or null when the rename failed (the file is then left where it is).</returns>
    public static string? QuarantineCorrupt(string path, Exception? error, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var target = path + ".corrupt-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        for (var n = 1; File.Exists(target); n++)
        {
            target = path + ".corrupt-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            File.Move(path, target);
            logger.LogError(error, "[Babelfin] {File} couldn't be read as settings; it was kept as {Kept} and Babelfin starts from defaults for it", path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(error ?? ex, "[Babelfin] {File} couldn't be read as settings and couldn't be renamed ({Reason}); it was left as it is", path, ex.Message);
            return null;
        }
    }

    private static void RemoveStaleTempFiles(string folder, string fileName)
    {
        string[] stale;
        try
        {
            stale = Directory.GetFiles(folder, fileName + ".*.tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in stale)
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file that can't be removed now is removed on the next write.
        }
    }
}
