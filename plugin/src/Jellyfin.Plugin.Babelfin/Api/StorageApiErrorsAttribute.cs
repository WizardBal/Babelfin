using System;
using System.IO;
using Jellyfin.Plugin.Babelfin.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>Reports validation problems, storage conflicts and temporary library failures without exposing server paths.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StorageApiErrorsAttribute : ExceptionFilterAttribute
{
    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is SettingsConflictException fields)
        {
            context.Result = new ObjectResult(fields.Problem) { StatusCode = StatusCodes.Status409Conflict };
            context.ExceptionHandled = true;
            return;
        }
        if (context.Exception is LibraryUnavailableException unavailable)
        {
            if (context.HttpContext.RequestServices?.GetService(typeof(ILogger<StorageApiErrorsAttribute>)) is ILogger<StorageApiErrorsAttribute> libraryLogger)
                libraryLogger.LogWarning(unavailable, "[Babelfin] Library API read failed; returning temporary unavailability with retry guidance");
            context.Result = new ObjectResult(new ApiProblem(unavailable.Message, Array.Empty<ValidationError>()) { Code = "library-unavailable" }) { StatusCode = StatusCodes.Status503ServiceUnavailable };
            context.ExceptionHandled = true;
            return;
        }
        if (context.Exception is SettingsValidationException invalid)
        {
            context.Result = new BadRequestObjectResult(invalid.Problem);
            context.ExceptionHandled = true;
            return;
        }
        if (context.Exception is not (IOException or UnauthorizedAccessException)) return;
        if (context.Exception is not PersonStorageConflictException
            && context.HttpContext.RequestServices?.GetService(typeof(ILogger<StorageApiErrorsAttribute>)) is ILogger<StorageApiErrorsAttribute> logger)
            logger.LogError(context.Exception, "[Babelfin] Storage API request failed; returning a conflict without changing the preserved settings");
        var message = context.Exception is PersonStorageConflictException conflict ? conflict.Message
            : "The settings could not be read or saved. Check server storage availability and permissions, then retry. The server log has the details.";
        context.Result = new ObjectResult(new ApiProblem(message, Array.Empty<ValidationError>()) { Code = context.Exception is PersonStorageConflictException storage ? storage.Code : "storage-unavailable" }) { StatusCode = StatusCodes.Status409Conflict };
        context.ExceptionHandled = true;
    }
}

internal sealed class SettingsConflictException(ApiProblem problem) : Exception(problem.Message)
{
    internal ApiProblem Problem { get; } = problem;
}

internal sealed class PersonStorageConflictException(string message, string code = "settings-changed") : IOException(message)
{
    internal string Code { get; } = code;
}

internal sealed class LibraryUnavailableException(Exception inner)
    : Exception("The library is temporarily unavailable. Retry loading these settings and check the server log.", inner);

internal static class LibraryQueries
{
    internal static T Read<T>(Func<T> query)
    {
        try { return query(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or LibraryUnavailableException))
        {
            throw new LibraryUnavailableException(ex);
        }
    }
}

internal sealed class SettingsValidationException(ApiProblem problem) : Exception(problem.Message)
{
    internal ApiProblem Problem { get; } = problem;
}

internal static class PersonStorageAccess
{
    // Check the snapshot observed by this request, rather than the clone the callback is about to give the store.
    internal static void RequireSnapshot(PersonSettings? expected, PersonSettings? current)
    {
        if (expected is { StorageEpoch: var epoch } && epoch != Guid.Empty && epoch != current?.StorageEpoch)
            throw new PersonStorageConflictException("This Jellyfin account was recreated while the request was running. Reload its settings and retry; nothing was saved by this request.", "account-recreated");
    }

    internal static void Require(IPersonStore store, Guid id, bool writing)
    {
        if (store is IPersonStoreSafety safety && safety.Conflict(id, writing) is { } reason)
            throw new PersonStorageConflictException(reason, reason.Contains("newer format", StringComparison.Ordinal) ? "newer-format"
                : reason.Contains("archived", StringComparison.Ordinal) ? "account-archived"
                : reason.Contains("damaged", StringComparison.Ordinal) ? "damaged-settings" : "storage-unavailable");
    }

    internal static bool Update(IPersonStore store, Guid id, Func<PersonSettings?, PersonSettings?> change, bool explicitly = false)
    {
        Require(store, id, writing: true);
        var called = false;
        var proposed = false;
        PersonSettings? Change(PersonSettings? current)
        {
            if (current is { SchemaVersion: > PersonSettings.CurrentSchemaVersion })
                throw new PersonStorageConflictException("These settings use a newer format. Upgrade Babelfin to change them. The original file was left untouched.", "newer-format");
            called = true;
            var result = change(current);
            proposed = result is not null;
            return result;
        }
        var saved = explicitly ? ExplicitPersonUpdates.Update(store, id, Change) : store.Update(id, Change);
        if (!saved && (!called || proposed))
        {
            Require(store, id, writing: true);
            throw new PersonStorageConflictException("The account or its settings changed while this request was running. Reload the settings and retry.");
        }
        return saved;
    }
}
