using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Globalization;

namespace NativePolicyCheck;

internal static class NormalizationContract
{
    internal static void Inspect(Assembly assembly)
    {
        var normalize = CreateNormalizer(assembly);
        foreach (var raw in new string?[] { null, "", "en", "eng", "de", "ger", "deu", "pt-BR", "pt-br", "pt-PT", "bf-unknown", " en " })
            Console.WriteLine($"NORMALIZE [{raw ?? "<null>"}] => [{string.Join(", ", normalize(raw))}]");
        foreach (var type in assembly.GetTypes().Where(t => t.Name is "MediaSourceManager" or "LocalizationManager"))
        {
            Console.WriteLine("TYPE " + type.FullName);
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Where(f => f.Name.Contains("local", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("FIELD " + field);
            foreach (var ctor in type.GetConstructors()) Console.WriteLine("CTOR " + ctor);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(m => m.Name.Contains("NormalizeLanguage", StringComparison.Ordinal) || m.Name.Contains("FindLanguageInfo", StringComparison.Ordinal)))
            {
                Console.WriteLine("METHOD " + method);
                PrintIL(method);
            }
        }
    }

    internal static ILocalizationManager CreateLocalization(Assembly assembly)
    {
        var localizationType = assembly.GetType("Emby.Server.Implementations.Localization.LocalizationManager", true)!;
        var constructor = localizationType.GetConstructors().Single();
        var configurationType = constructor.GetParameters()[0].ParameterType;
        var config = DispatchProxy.Create(configurationType, typeof(ConfigurationProxy));
        var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(localizationType));
        var localization = constructor.Invoke(new[] { config, logger });
        ((Task)localizationType.GetMethod("LoadCultures", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(localization, null)!).GetAwaiter().GetResult();
        return (ILocalizationManager)localization;
    }

    internal static Func<string?, IReadOnlyList<string>> CreateNormalizer(Assembly assembly)
    {
        var localization = CreateLocalization(assembly);
        var managerType = assembly.GetType("Emby.Server.Implementations.Library.MediaSourceManager", true)!;
        var manager = RuntimeHelpers.GetUninitializedObject(managerType);
        managerType.GetField("_localizationManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, localization);
        var normalize = managerType.GetMethod("NormalizeLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return language => (IReadOnlyList<string>)normalize.Invoke(manager, new object?[] { language })!;
    }

    public class ConfigurationProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var resultType = targetMethod!.ReturnType;
            return resultType == typeof(void) ? null : resultType.IsValueType || (resultType.IsClass && resultType.GetConstructor(Type.EmptyTypes) is not null) ? Activator.CreateInstance(resultType) : null;
        }
    }

    private static void PrintIL(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var start = offset;
            ushort key = bytes[offset++];
            if (key == 0xfe) key = (ushort)(0xfe00 | bytes[offset++]);
            var op = opcodes[key];
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
            object? operand = size == 4 ? BitConverter.ToInt32(bytes, offset) : size == 1 ? bytes[offset] : null;
            if (size == 4 && op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
                operand = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset));
            if (size == 4 && op.OperandType == OperandType.InlineString) operand = method.Module.ResolveString(BitConverter.ToInt32(bytes, offset));
            Console.WriteLine($"IL_{start:x4}: {op.Name} {operand}");
            offset += size;
        }
    }
}