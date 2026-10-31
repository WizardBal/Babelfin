using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Babelfin.Library;

/// <summary>
/// Dashboard &gt; Scheduled Tasks &gt; Babelfin &gt; "Babelfin: refresh library facts": recomputes every library's
/// facts (title count, usual original language, original-audio detection) at startup and every few days.
/// </summary>
/// <remarks>
/// Jellyfin reads <see cref="GetDefaultTriggers"/> only while the task has no saved triggers, so a later change of
/// <c>LibraryFactsRefreshDays</c> applies after the admin resets the task's triggers (or edits them on the dashboard).
/// A value below one day counts as one day.
/// </remarks>
public class LibraryFactsTask : IScheduledTask
{
    private readonly ILibraryFacts _facts;

    /// <summary>Initializes a new instance of the <see cref="LibraryFactsTask"/> class.</summary>
    /// <param name="facts">The library facts service.</param>
    public LibraryFactsTask(ILibraryFacts facts)
    {
        _facts = facts;
    }

    /// <inheritdoc />
    public string Name => "Babelfin: refresh library facts";

    /// <inheritdoc />
    public string Key => "BabelfinLibraryFacts";

    /// <inheritdoc />
    public string Description => "Counts each library's titles and original languages so Babelfin knows which libraries are original-audio and which language they usually are in.";

    /// <inheritdoc />
    public string Category => "Babelfin";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var days = Math.Max(1, Plugin.Instance?.Configuration.LibraryFactsRefreshDays ?? 1);
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(days).Ticks },
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _facts.RefreshAsync(progress, cancellationToken);
    }
}
