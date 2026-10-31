using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Babelfin.Api;

/// <summary>Checks import containers before cloning, migration, seeding or saving can run.</summary>
internal static class ImportStructure
{
    /// <summary>Rejects explicit null required lists, list entries and model objects throughout an import.</summary>
    internal static IReadOnlyList<ValidationError> Validate(object document)
    {
        var errors = new List<ValidationError>();
        var nullability = new NullabilityInfoContext();
        Visit(document, string.Empty);
        return errors;

        void Visit(object value, string path)
        {
            foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0
                    || property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                var child = property.GetValue(value);
                var type = property.PropertyType;
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                var childPath = path.Length == 0 ? name : path + "." + name;
                var model = type.Assembly == typeof(ImportStructure).Assembly && type.IsClass;
                var list = typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string);
                if (child is null)
                {
                    if ((model || list) && nullability.Create(property).ReadState == NullabilityState.NotNull)
                        errors.Add(new ValidationError(childPath, "Must not be null; use an empty list or settings object."));
                    continue;
                }

                if (list && child is IEnumerable entries)
                {
                    var index = 0;
                    foreach (var entry in entries)
                    {
                        var entryPath = childPath + "[" + index++ + "]";
                        if (entry is null) errors.Add(new ValidationError(entryPath, "Must not be null."));
                        else if (entry.GetType().Assembly == typeof(ImportStructure).Assembly) Visit(entry, entryPath);
                    }
                }
                else if (model) Visit(child, childPath);
            }
        }
    }
}
