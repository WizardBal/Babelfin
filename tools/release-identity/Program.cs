// Reads metadata only. The supplied plugin assembly is never loaded or executed.
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

try
{
    if (args.Length != 1) throw new ArgumentException("Supply one managed DLL.");
    var path = Path.GetFullPath(args[0]);
    var bytes = File.ReadAllBytes(path);
    using var memory = new MemoryStream(bytes, writable: false);
    using var pe = new PEReader(memory);
    if (!pe.HasMetadata) throw new BadImageFormatException("The file has no managed metadata.");
    var reader = pe.GetMetadataReader();
    if (!reader.IsAssembly) throw new BadImageFormatException("The file is not an assembly.");
    var assembly = reader.GetAssemblyDefinition();
    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var handle in assembly.GetCustomAttributes())
    {
        var attribute = reader.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
        var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (reader.GetString(type.Namespace) != "System.Reflection") continue;
        var name = reader.GetString(type.Name);
        if (name is not ("AssemblyFileVersionAttribute" or "AssemblyInformationalVersionAttribute")) continue;
        var blob = reader.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1 || !attributes.TryAdd(name, blob.ReadSerializedString() ?? string.Empty))
            throw new BadImageFormatException("Assembly version metadata is invalid or duplicated.");
    }
    var result = new Dictionary<string, string?>
    {
        ["assembly_name"] = reader.GetString(assembly.Name),
        ["assembly_version"] = assembly.Version.ToString(),
        ["file_version"] = FileVersionInfo.GetVersionInfo(path).FileVersion,
        ["assembly_file_version"] = attributes.GetValueOrDefault("AssemblyFileVersionAttribute"),
        ["informational_version"] = attributes.GetValueOrDefault("AssemblyInformationalVersionAttribute"),
        ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine("Could not read managed DLL identity: " + ex.GetType().Name);
    return 1;
}
