using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Admin;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>The body of <c>POST Admin/Simulate</c>.</summary>
public sealed class SimulateRequest
{
    /// <summary>Gets or sets the person to simulate.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the library, series, season, collection or title to walk.</summary>
    public Guid ItemId { get; set; }
}

/// <summary>The answer of <c>POST Admin/Simulate</c>.</summary>
/// <param name="JobId">The job id to poll.</param>
public sealed record SimulateStarted(string JobId);

/// <summary>The 409 answer of <c>POST Admin/Simulate</c> when a job is running (one at a time).</summary>
/// <param name="Message">What is wrong.</param>
/// <param name="JobId">The running job (it can be polled or cancelled).</param>
public sealed record SimulateBusy(string Message, string JobId)
{
    /// <summary>Gets the stable problem identifier while another simulation is active.</summary>
    public string Code => "simulation-busy";
}

/// <summary>The answer of <c>GET Admin/Simulate/{jobId}</c>.</summary>
/// <param name="JobId">The job id.</param>
/// <param name="UserId">The person simulated.</param>
/// <param name="UserName">Their name.</param>
/// <param name="ItemId">The item walked.</param>
/// <param name="ItemName">Its name.</param>
/// <param name="Progress">0–100.</param>
/// <param name="Done">Whether the job ended.</param>
/// <param name="Cancelled">Whether it was cancelled.</param>
/// <param name="Error">An error message, or null.</param>
/// <param name="Total">Episodes and movies found.</param>
/// <param name="Errors">Items that couldn't be evaluated.</param>
/// <param name="Rows">The rows (once done).</param>
/// <param name="Counts">The coverage counts over the rows (once done), or null.</param>
public sealed record SimulateStatus(
    string JobId,
    Guid UserId,
    string? UserName,
    Guid ItemId,
    string? ItemName,
    int Progress,
    bool Done,
    bool Cancelled,
    string? Error,
    int Total,
    int Errors,
    IReadOnlyList<CoverageRow> Rows,
    CoverageCounts? Counts);


/// <summary>
/// The title simulator, under {BaseUrl}/Babelfin/Admin. Items are checked as the chosen person;
/// rows carry names and indices only, never file paths.
/// </summary>
[ApiController]
[StorageApiErrors]
[Route("Babelfin/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces("application/json")]
public class SimulateController : ControllerBase
{
    private readonly Simulator _simulator;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryFacts _facts;

    /// <summary>Initializes a new instance of the <see cref="SimulateController"/> class.</summary>
    /// <param name="simulator">The simulator.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="facts">Library facts (the usual original language).</param>
    public SimulateController(Simulator simulator, IUserManager userManager, ILibraryManager libraryManager, ILibraryFacts facts)
    {
        _simulator = simulator;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _facts = facts;
    }

    /// <summary>Starts a simulation for a person on a library, series, season, collection or title.</summary>
    /// <param name="request">The person and the item.</param>
    /// <returns>The job id; 409 with the running job's id when one is running.</returns>
    [HttpPost("Simulate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SimulateStarted> Start([FromBody] SimulateRequest request)
    {
        if (request is null || request.UserId == Guid.Empty || request.ItemId == Guid.Empty)
        {
            return BadRequest(new ApiProblem("Choose a person and a library, series or title.", Array.Empty<ValidationError>()));
        }

        var user = _userManager.GetUserById(request.UserId);
        if (user is null)
        {
            return NotFound(new ApiProblem("That person doesn't exist.", Array.Empty<ValidationError>()));
        }

        // The access check is made as the chosen person: an item they can't see is not found.
        var item = LibraryQueries.Read(() => _libraryManager.GetItemById<BaseItem>(request.ItemId, user));
        if (item is null)
        {
            return NotFound(new ApiProblem("That person can't see this item.", Array.Empty<ValidationError>()));
        }

        if (!_simulator.TryStart(user, item, UsualLanguage(item), out var job))
        {
            return Conflict(new SimulateBusy("A simulation is already running.", job.Id));
        }

        return Accepted(new SimulateStarted(job.Id));
    }

    /// <summary>Gets a job's progress and, once done, its rows.</summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>The status, or 404 when unknown or expired (results are kept 30 minutes).</returns>
    [HttpGet("Simulate/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SimulateStatus> Status([FromRoute] string jobId)
    {
        var job = _simulator.Get(jobId);
        if (job is null)
        {
            return NotFound();
        }

        var done = job.Done;
        var rows = done ? job.Rows : Array.Empty<CoverageRow>();
        return Ok(new SimulateStatus(
            job.Id,
            job.UserId,
            job.UserName,
            job.RootId,
            job.RootName,
            job.Progress,
            done,
            job.Cancelled,
            job.Error,
            job.Total,
            job.Errors,
            rows,
            done ? Coverage.Count(rows) : null));
    }

    /// <summary>Cancels a running job.</summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>204, or 404 when unknown.</returns>
    [HttpDelete("Simulate/{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Cancel([FromRoute] string jobId) => _simulator.Cancel(jobId) ? NoContent() : NotFound();

    // The usual original language of the item's library (the item itself when it is a library).
    private string? UsualLanguage(BaseItem item)
    {
        var libraryId = item is CollectionFolder
            ? item.Id
            : LibraryQueries.Read(() => _libraryManager.GetCollectionFolders(item)?.FirstOrDefault(f => f is not null)?.Id);
        return libraryId is { } id && id != Guid.Empty ? _facts.Get(id)?.UsualOriginalLanguage : null;
    }
}
