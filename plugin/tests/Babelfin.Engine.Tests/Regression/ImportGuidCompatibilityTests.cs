using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Babelfin.Api;
using Jellyfin.Plugin.Babelfin.Settings;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

public sealed class ImportGuidCompatibilityTests
{
    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public void ImportAcceptsWireAndFileGuidValuesWithoutChangingLibraryStrings(string format)
    {
        var id = Guid.NewGuid(); var library = Guid.NewGuid().ToString(format);
        var json = JsonSerializer.SerializeToElement(new { Users = new[] { new { UserId = id.ToString(format), Everyday = new { Reads = new[] { "fr" } }, Libraries = new[] { new { LibraryId = library } } } } });
        Assert.True(ImportFileFormat.TryRead<ExportDocument>(json, personal: false, out var document, out var error));
        Assert.Null(error);
        var person = Assert.Single(document!.Users);
        Assert.Equal(id, person.UserId);
        Assert.Equal(library, Assert.Single(person.Libraries).LibraryId);
        Assert.True(ImportFileFormat.TryRead<PersonSettings>(json.GetProperty("Users")[0], personal: true, out var personal, out error));
        Assert.Equal(id, personal!.UserId);
        Assert.Equal(library, Assert.Single(personal.Libraries).LibraryId);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public void ImportAcceptsGuidDictionaryKeysAndNullableValues(string format)
    {
        var id = Guid.NewGuid(); var emptyValueKey = Guid.NewGuid(); var value = Guid.NewGuid();
        var json = JsonSerializer.SerializeToElement(new { Users = new Dictionary<string, string?> { [id.ToString(format)] = value.ToString(format), [emptyValueKey.ToString(format)] = null } });
        Assert.True(ImportFileFormat.TryRead<GuidMap>(json, personal: false, out var document, out var error));
        Assert.Null(error);
        Assert.Equal(value, document!.Users[id]);
        Assert.Null(document.Users[emptyValueKey]);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public void ImportRejectsInvalidGuidValues(string? value)
    {
        var json = JsonSerializer.SerializeToElement(new { UserId = value, Everyday = new { Reads = new[] { "fr" } } });
        Assert.False(ImportFileFormat.TryRead<PersonSettings>(json, personal: true, out _, out var error));
        Assert.NotNull(error);
        Assert.Equal("file", Assert.Single(error.Errors).Path);
    }

    [Fact]
    public void ImportRejectsInvalidGuidDictionaryKeys()
    {
        var json = JsonSerializer.SerializeToElement(new { Users = new Dictionary<string, string> { ["invalid"] = Guid.NewGuid().ToString("N") } });
        Assert.False(ImportFileFormat.TryRead<GuidMap>(json, personal: false, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ImportCompatibilityDoesNotChangeCanonicalStorageOrExportGuids()
    {
        var id = Guid.NewGuid();
        var before = JsonSerializer.Serialize(new PersonSettings { UserId = id }, BabelfinJson.Options);
        using var incoming = JsonDocument.Parse("{\"UserId\":\"" + id.ToString("N") + "\",\"Everyday\":{\"Reads\":[\"fr\"]}}");
        Assert.True(ImportFileFormat.TryRead<PersonSettings>(incoming.RootElement, personal: true, out _, out _));
        Assert.Equal(before, JsonSerializer.Serialize(new PersonSettings { UserId = id }, BabelfinJson.Options));
        using var serialized = JsonDocument.Parse(before);
        Assert.Equal(id.ToString("D"), serialized.RootElement.GetProperty("userId").GetString());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Guid>("\"" + id.ToString("N") + "\"", BabelfinJson.Options));
    }

    private sealed class GuidMap
    {
        public Dictionary<Guid, Guid?> Users { get; set; } = new();
    }
}
