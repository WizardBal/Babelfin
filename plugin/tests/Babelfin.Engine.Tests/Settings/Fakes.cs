using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Engine.Languages;
using Jellyfin.Plugin.Babelfin.Engine.Selection;
using Jellyfin.Plugin.Babelfin.Resolution;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities;

namespace Babelfin.Engine.Tests.Settings;

/// <summary>Stable ids for the settings tests.</summary>
internal static class Ids
{
    public static readonly Guid Item = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Season = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Series = new("33333333-3333-3333-3333-333333333333");
    public static readonly Guid Cloudvale = new("44444444-4444-4444-4444-444444444444");
    public static readonly Guid Favourites = new("55555555-5555-5555-5555-555555555555");
    public static readonly Guid Anime = new("66666666-6666-6666-6666-666666666666");
    public static readonly Guid Shows = new("77777777-7777-7777-7777-777777777777");
    public static readonly Guid Movies = new("88888888-8888-8888-8888-888888888888");
    public static readonly Guid Person = new("99999999-9999-9999-9999-999999999999");

    public static string N(Guid id) => id.ToString("N");
}

/// <summary>Terse builders for item contexts, layers and rules.</summary>
internal static class Ctx
{
    /// <summary>An episode of a series, in one season, in one library, with any collections given as (id, name).</summary>
    public static ItemContext Episode(
        string libraryName = "Shows",
        Guid? libraryId = null,
        string? original = null,
        string[]? genres = null,
        (Guid Id, string Name)[]? collections = null,
        string name = "Episode 1",
        string seriesName = "Skyward Lantern Voyage",
        string? client = null,
        LibraryFact? facts = null,
        string[]? tags = null,
        string[]? studios = null,
        string[]? countries = null,
        Guid[]? extraLibraries = null) =>
        new(
            Ids.Item,
            name,
            ContentKind.Episode,
            Ids.Season,
            Ids.Series,
            seriesName,
            (collections ?? Array.Empty<(Guid, string)>()).Select(c => c.Id).ToArray(),
            (collections ?? Array.Empty<(Guid, string)>()).Select(c => c.Name).ToArray(),
            new[] { libraryId ?? LibraryIdFor(libraryName) }.Concat(extraLibraries ?? Array.Empty<Guid>()).ToArray(),
            libraryName,
            genres ?? Array.Empty<string>(),
            tags ?? Array.Empty<string>(),
            studios ?? Array.Empty<string>(),
            countries ?? Array.Empty<string>(),
            original,
            client,
            facts);

    /// <summary>A movie in a library, with any collections.</summary>
    public static ItemContext Movie(
        string libraryName = "Movies",
        string? original = null,
        (Guid Id, string Name)[]? collections = null,
        string name = "Paper Moon Ferry",
        string[]? genres = null,
        LibraryFact? facts = null) =>
        new(
            Ids.Item,
            name,
            ContentKind.Movie,
            null,
            null,
            null,
            (collections ?? Array.Empty<(Guid, string)>()).Select(c => c.Id).ToArray(),
            (collections ?? Array.Empty<(Guid, string)>()).Select(c => c.Name).ToArray(),
            new[] { LibraryIdFor(libraryName) },
            libraryName,
            genres ?? Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            original,
            null,
            facts);

    public static Guid LibraryIdFor(string name) => name switch
    {
        "Anime" => Ids.Anime,
        "Movies" => Ids.Movies,
        _ => Ids.Shows,
    };

    /// <summary>Computed facts for a library.</summary>
    public static LibraryFact Facts(Guid id, string name, bool detected, string? usual = null, int genrePercent = 0) => new()
    {
        LibraryId = id,
        Name = name,
        ComputedAt = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc),
        TitleCount = 100,
        TitlesWithOriginalLanguage = 90,
        MostCommonOriginalLanguage = usual,
        MostCommonOriginalPercent = usual is null ? 0 : 80,
        UsualOriginalLanguage = usual,
        OriginalAudioGenrePercent = genrePercent,
        NameMatchesWordList = false,
        DetectedOriginalAudio = detected,
    };

    /// <summary>A layer whose subtitle languages carry a marker, so the winning layer is easy to see.</summary>
    public static SettingsLayer Marked(string marker) => new() { SubtitleLanguages = new[] { marker } };

    public static SmartRule Rule(string name, SettingsLayer settings, params RuleCondition[] conditions) => new()
    {
        Id = name,
        Name = name,
        Conditions = conditions.ToList(),
        Settings = settings,
    };

    public static RuleCondition When(ConditionKind kind, params string[] values) => new() { Kind = kind, Values = values.ToList() };

    public static RuleCondition Not(ConditionKind kind, params string[] values) => new() { Kind = kind, Values = values.ToList(), Negate = true };

    public static ItemOverride Override(Guid id, OverrideScope scope, SettingsLayer settings, ManualMemory? manual = null) =>
        new() { ItemId = Ids.N(id), Scope = scope, Settings = settings, Manual = manual };

    public static TitleDefault Title(Guid id, OverrideScope scope, SettingsLayer settings) =>
        new() { ItemId = Ids.N(id), Scope = scope, Settings = settings };

    public static LibraryLayer MyLibrary(Guid id, SettingsLayer settings) => new() { LibraryId = Ids.N(id), Settings = settings };

    public static LibraryDefault TheirLibrary(Guid id, SettingsLayer settings, OriginalAudioChoice choice = OriginalAudioChoice.Auto) =>
        new() { LibraryId = Ids.N(id), Settings = settings, OriginalAudio = choice };

    public static PersonSettings Person(string[]? understood = null, string[]? reads = null) => new()
    {
        UserId = Ids.Person,
        UserName = "Test",
        Everyday = new SettingsLayer { Understood = understood ?? new[] { "en" }, Reads = reads ?? new[] { "en" } },
    };

    public static AudioStepSetting[] Chain(params AudioStepSetting[] steps) => steps;

    public static AudioStepSetting Lang(string language) => new(AudioStepKind.Language, language);

    public static AudioStepSetting Step(AudioStepKind kind) => new(kind);

    public static ResolvedProfileResult Resolve(PersonSettings person, PluginConfiguration? admin, ItemContext context) =>
        ProfileResolver.Resolve(person, admin ?? new PluginConfiguration(), context, LanguageTable.Default);

    public static AudioStepKind[] Kinds(this ResolvedProfile profile) => profile.AudioChain.Select(s => s.Kind).ToArray();
}

/// <summary>An in-memory <see cref="IPersonStore"/>.</summary>
internal sealed class FakePersonStore : IPersonStore
{
    private readonly ConcurrentDictionary<Guid, PersonSettings> _people = new();

    public event EventHandler<PersonStoreChangedEventArgs>? Changed;

    public string DataFolder => "(memory)";

    public int Creates { get; private set; }

    public PersonSettings? TryGet(Guid userId) => _people.TryGetValue(userId, out var p) ? p : null;

    public PersonSettings GetOrCreate(Guid userId, Func<PersonSettings> create) => _people.GetOrAdd(userId, _ =>
    {
        Creates++;
        var created = create();
        created.UserId = userId;
        return created;
    });

    public void Save(PersonSettings settings)
    {
        var copy = settings.Clone();
        copy.UpdatedAt = DateTime.UtcNow;
        _people[copy.UserId] = copy;
        Changed?.Invoke(this, new PersonStoreChangedEventArgs(copy.UserId));
    }

    public bool Update(Guid userId, Func<PersonSettings?, PersonSettings?> change)
    {
        lock (_people)
        {
            var changed = change(TryGet(userId));
            if (changed is null)
            {
                return false;
            }

            Save(changed);
            return true;
        }
    }

    public bool Delete(Guid userId)
    {
        var removed = _people.TryRemove(userId, out _);
        Changed?.Invoke(this, new PersonStoreChangedEventArgs(userId));
        return removed;
    }

    public IReadOnlyList<Guid> UserIds() => _people.Keys.ToArray();
}

/// <summary>An in-memory <see cref="ILibraryFacts"/>.</summary>
internal sealed class FakeLibraryFacts : ILibraryFacts
{
    private readonly Dictionary<Guid, LibraryFact> _facts = new();

    public event EventHandler? Changed;

    public DateTime? ComputedAt { get; private set; }

    public IReadOnlyList<LibraryFact> All => _facts.Values.ToArray();

    public int RefreshRequests { get; private set; }

    public LibraryFact? Get(Guid libraryId) => _facts.TryGetValue(libraryId, out var fact) ? fact : null;

    public void Set(LibraryFact fact)
    {
        _facts[fact.LibraryId] = fact;
        ComputedAt = DateTime.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(100);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void RequestRefresh() => RefreshRequests++;
}

/// <summary>An <see cref="IItemContextBuilder"/> that returns prepared contexts by item id (the item's own id is kept).</summary>
internal sealed class FakeContextBuilder : IItemContextBuilder
{
    private readonly Dictionary<Guid, ItemContext> _contexts = new();

    public int Builds { get; private set; }

    public void Set(Guid itemId, ItemContext context) => _contexts[itemId] = context with { ItemId = itemId };

    public ItemContext Build(BaseItem item, User user, string? client = null)
    {
        Builds++;
        var context = _contexts.TryGetValue(item.Id, out var c) ? c : ItemContext.None with { ItemId = item.Id };
        return context with { Client = client };
    }
}

/// <summary>A <see cref="DispatchProxy"/> stand-in for a large Jellyfin interface: every member returns its default; event subscriptions are recorded.</summary>
public class NullProxy : DispatchProxy
{
    /// <summary>Gets the handlers subscribed through add_X, by event name.</summary>
    public Dictionary<string, List<Delegate>> Handlers { get; } = new();

    /// <summary>Creates a proxy for an interface.</summary>
    /// <typeparam name="T">The interface.</typeparam>
    /// <returns>The proxy and its recorder.</returns>
    public static (T Proxy, NullProxy Recorder) Create<T>()
        where T : class
    {
        var proxy = DispatchProxy.Create<T, NullProxy>();
        return (proxy, (NullProxy)(object)proxy);
    }

    /// <summary>Raises a recorded event.</summary>
    /// <param name="name">The event name.</param>
    /// <param name="args">The handler arguments (sender, event args).</param>
    public void Raise(string name, params object?[] args)
    {
        if (Handlers.TryGetValue(name, out var list))
        {
            foreach (var handler in list.ToArray())
            {
                handler.DynamicInvoke(args);
            }
        }
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal) && args is [Delegate add])
        {
            var name = targetMethod.Name.Substring(4);
            if (!Handlers.TryGetValue(name, out var list))
            {
                Handlers[name] = list = new List<Delegate>();
            }

            list.Add(add);
            return null;
        }

        if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal) && args is [Delegate remove])
        {
            if (Handlers.TryGetValue(targetMethod.Name.Substring(7), out var list))
            {
                list.Remove(remove);
            }

            return null;
        }

        var type = targetMethod.ReturnType;
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
