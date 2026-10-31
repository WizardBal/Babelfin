using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Babelfin.Settings;

/// <summary>What a smart-rule condition looks at.</summary>
public enum ConditionKind
{
    /// <summary>The item's library id (<see cref="RuleCondition.Values"/> are library ids as "N" or "D" GUID strings; names are also accepted).</summary>
    Library,

    /// <summary>A genre of the item (or of its series).</summary>
    Genre,

    /// <summary>A tag of the item (or of its series).</summary>
    Tag,

    /// <summary>A collection the item is in (ids or names).</summary>
    Collection,

    /// <summary>A studio or network.</summary>
    Studio,

    /// <summary>A production country.</summary>
    Country,

    /// <summary>The title's original language is one of the values (language strings resolved through the language table).</summary>
    OriginalLanguage,

    /// <summary>The title's original language is one the person understands (no values; <see cref="RuleCondition.Negate"/> gives "isn't").</summary>
    OriginalLanguageUnderstood,

    /// <summary>The content type: values "movie" or "episode" (other kinds match neither).</summary>
    ContentType,

    /// <summary>The app (client name as Jellyfin reports it, e.g. "Jellyfin Web"); without a known client the condition is false.</summary>
    Client,

    /// <summary>The item's name or series name matches: plain "contains" text for a person's rule; a regular expression for an admin rule (<see cref="RuleCondition.IsRegex"/>).</summary>
    TitleMatches,
}

/// <summary>How a rule's conditions combine.</summary>
public enum RuleMatch
{
    /// <summary>Every condition must hold (AND).</summary>
    All,

    /// <summary>Any condition suffices (OR).</summary>
    Any,
}

/// <summary>One condition of a smart rule.</summary>
public class RuleCondition
{
    /// <summary>Gets or sets what the condition looks at.</summary>
    public ConditionKind Kind { get; set; }

    /// <summary>Gets or sets the values; the condition holds when the item's value matches any of them (case-insensitive).</summary>
    public List<string> Values { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the condition is inverted.</summary>
    public bool Negate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="ConditionKind.TitleMatches"/> values are regular expressions.
    /// Only honoured in admin layers (run with NonBacktracking and a timeout); a person's patterns are always plain text.
    /// </summary>
    public bool IsRegex { get; set; }

    /// <summary>A deep copy.</summary>
    /// <returns>The copy.</returns>
    public RuleCondition Clone() => new() { Kind = Kind, Values = Values.ToList(), Negate = Negate, IsRegex = IsRegex };
}

/// <summary>A smart rule: when its conditions match the item, its settings apply (field by field, first matching rule wins).</summary>
public class SmartRule
{
    /// <summary>Gets or sets a stable id (any string; the API assigns a GUID when empty).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown in "set by your smart rule 'X'".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the rule is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how the conditions combine.</summary>
    public RuleMatch Match { get; set; } = RuleMatch.All;

    /// <summary>Gets or sets the conditions. A rule with no conditions matches nothing.</summary>
    public List<RuleCondition> Conditions { get; set; } = new();

    /// <summary>Gets or sets the settings the rule applies.</summary>
    public SettingsLayer Settings { get; set; } = new();

    /// <summary>A deep copy.</summary>
    /// <returns>The copy.</returns>
    public SmartRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Match = Match,
        Conditions = Conditions.Select(c => c.Clone()).ToList(),
        Settings = Settings.Clone(),
    };

    /// <summary>Gives the rule an id when it has none.</summary>
    public void EnsureId()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }
    }
}
