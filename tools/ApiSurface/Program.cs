using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

string root = FindRepositoryRoot();
bool write = args.Contains("--write", StringComparer.Ordinal);
bool verify = args.Contains("--verify", StringComparer.Ordinal);
if (write == verify)
{
    Console.Error.WriteLine("Use exactly one of --write or --verify.");
    return 2;
}

string[] assemblyPaths = Directory.GetDirectories(Path.Combine(root, "src"), "Communication.*")
    .Select(directory => Path.Combine(
        directory,
        "bin",
        "Release",
        "netstandard2.1",
        $"{Path.GetFileName(directory)}.dll"))
    .Where(File.Exists)
    .Order(StringComparer.Ordinal)
    .ToArray();
if (assemblyPaths.Length == 0)
{
    Console.Error.WriteLine("No Release netstandard2.1 assemblies were found. Build the solution first.");
    return 2;
}

string[] probeDirectories = assemblyPaths.Select(Path.GetDirectoryName).OfType<string>()
    .Concat(Directory.GetDirectories(root, "net10.0", SearchOption.AllDirectories)
        .Where(directory => directory.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string? dependency = probeDirectories
        .Select(directory => Path.Combine(directory, $"{name.Name}.dll"))
        .FirstOrDefault(File.Exists);
    return dependency is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency);
};

string apiDirectory = Path.Combine(root, "api");
if (write)
{
    Directory.CreateDirectory(apiDirectory);
}

bool changed = false;
foreach (string path in assemblyPaths)
{
    Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    string content = BuildSurface(assembly);
    string baseline = Path.Combine(apiDirectory, $"{assembly.GetName().Name}.txt");
    if (write)
    {
        File.WriteAllText(baseline, content, new UTF8Encoding(false));
        Console.WriteLine($"Wrote {Path.GetRelativePath(root, baseline)}");
        continue;
    }

    if (!File.Exists(baseline))
    {
        Console.Error.WriteLine($"Missing API baseline: {Path.GetRelativePath(root, baseline)}");
        changed = true;
        continue;
    }

    string expected = File.ReadAllText(baseline, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
    if (!string.Equals(expected, content, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Public API changed: {assembly.GetName().Name}");
        Console.Error.WriteLine("Review the change, then run: dotnet run --project tools/ApiSurface -- --write");
        changed = true;
    }
    else
    {
        Console.WriteLine($"API OK: {assembly.GetName().Name}");
    }
}

return changed ? 1 : 0;

static string BuildSurface(Assembly assembly)
{
    var output = new StringBuilder();
    output.Append("# Public API baseline: ").Append(assembly.GetName().Name).Append('\n');
    output.Append("# Update intentionally with tools/ApiSurface --write.").Append('\n').Append('\n');
    foreach (Type type in assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
    {
        output.Append(FormatTypeDeclaration(type)).Append('\n');
        foreach (string member in GetMembers(type).Order(StringComparer.Ordinal))
        {
            output.Append("  ").Append(member).Append('\n');
        }

        output.Append('\n');
    }

    return output.ToString();
}

static string FormatTypeDeclaration(Type type)
{
    string kind = type.IsEnum ? "enum"
        : type.IsInterface ? "interface"
        : type.IsValueType ? "struct"
        : type.IsAbstract && type.IsSealed ? "static class"
        : type.IsAbstract ? "abstract class"
        : type.IsSealed ? "sealed class"
        : "class";
    var inheritance = new List<string>();
    if (type.BaseType is not null && type.BaseType != typeof(object) && !type.IsValueType && !type.IsEnum)
    {
        inheritance.Add(FormatTypeName(type.BaseType));
    }

    inheritance.AddRange(type.GetInterfaces().Select(FormatTypeName).Order(StringComparer.Ordinal));
    string suffix = inheritance.Count == 0 ? string.Empty : $" : {string.Join(", ", inheritance)}";
    return $"public {kind} {FormatTypeName(type)}{suffix}";
}

static IEnumerable<string> GetMembers(Type type)
{
    const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                               BindingFlags.Static | BindingFlags.DeclaredOnly;
    foreach (ConstructorInfo constructor in type.GetConstructors(flags))
    {
        yield return $"ctor({FormatParameters(constructor.GetParameters())})";
    }

    foreach (FieldInfo field in type.GetFields(flags))
    {
        if (field.IsSpecialName)
        {
            continue;
        }

        string modifiers = field.IsStatic ? "static " : string.Empty;
        string value = field.IsLiteral ? $" = {FormatValue(field.GetRawConstantValue())}" : string.Empty;
        yield return $"{modifiers}field {FormatTypeName(field.FieldType)} {field.Name}{value}";
    }

    foreach (PropertyInfo property in type.GetProperties(flags))
    {
        MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
        string modifiers = accessor?.IsStatic == true ? "static " : string.Empty;
        string access = $"{{{(property.GetMethod?.IsPublic == true ? " get;" : string.Empty)}" +
                        $"{(property.SetMethod?.IsPublic == true ? " set;" : string.Empty)} }}";
        string index = property.GetIndexParameters().Length == 0
            ? property.Name
            : $"this[{FormatParameters(property.GetIndexParameters())}]";
        yield return $"{modifiers}property {FormatTypeName(property.PropertyType)} {index} {access}";
    }

    foreach (EventInfo eventInfo in type.GetEvents(flags))
    {
        string modifiers = eventInfo.AddMethod?.IsStatic == true ? "static " : string.Empty;
        yield return $"{modifiers}event {FormatTypeName(eventInfo.EventHandlerType!)} {eventInfo.Name}";
    }

    foreach (MethodInfo method in type.GetMethods(flags).Where(method => !method.IsSpecialName))
    {
        string modifiers = method.IsStatic ? "static " : string.Empty;
        string generic = method.IsGenericMethodDefinition
            ? $"<{string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))}>"
            : string.Empty;
        yield return $"{modifiers}method {FormatTypeName(method.ReturnType)} {method.Name}{generic}" +
                     $"({FormatParameters(method.GetParameters())})";
    }
}

static string FormatParameters(IEnumerable<ParameterInfo> parameters) => string.Join(", ", parameters.Select(parameter =>
{
    string modifier = parameter.IsOut ? "out "
        : parameter.ParameterType.IsByRef && parameter.IsIn ? "in "
        : parameter.ParameterType.IsByRef ? "ref "
        : string.Empty;
    string optional = parameter.HasDefaultValue ? $" = {FormatValue(parameter.DefaultValue)}" : string.Empty;
    return $"{modifier}{FormatTypeName(parameter.ParameterType)} {parameter.Name}{optional}";
}));

static string FormatTypeName(Type type)
{
    if (type.IsByRef)
    {
        return FormatTypeName(type.GetElementType()!);
    }

    if (type.IsArray)
    {
        return $"{FormatTypeName(type.GetElementType()!)}[]";
    }

    if (type.IsGenericParameter)
    {
        return type.Name;
    }

    Dictionary<Type, string> aliases = new()
    {
        [typeof(void)] = "void",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(object)] = "object",
    };
    if (aliases.TryGetValue(type, out string? alias))
    {
        return alias;
    }

    string name = (type.FullName ?? type.Name).Replace('+', '.');
    int tick = name.IndexOf('`');
    if (!type.IsGenericType)
    {
        return name;
    }

    if (tick >= 0)
    {
        name = name[..tick];
    }

    return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))}>";
}

static string FormatValue(object? value) => value switch
{
    null => "null",
    string text => $"\"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
    char character => $"'{character}'",
    bool boolean => boolean ? "true" : "false",
    Enum enumeration => $"{FormatTypeName(enumeration.GetType())}.{enumeration}",
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => value.ToString() ?? "null",
};

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Communication.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Communication.sln was not found above the tool directory.");
}
