using System.Reflection;
using System.Runtime.Versioning;
using Communication.Abstractions.Interfaces;

namespace Communication.UnitTests;

public sealed class PublicApiArchitectureTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Net.Sockets",
        "System.IO.Ports",
        "S7.Net",
        "Opc.Ua",
        "Melsec",
        "Mitsubishi",
    ];

    [Fact]
    public void Net10_test_host_consumes_the_netstandard_2_1_asset()
    {
        Assembly assembly = typeof(ITransportChannel).Assembly;
        TargetFrameworkAttribute attribute = Assert.Single(
            assembly.GetCustomAttributes<TargetFrameworkAttribute>());

        Assert.Equal(".NETStandard,Version=v2.1", attribute.FrameworkName);
    }

    [Fact]
    public void Public_api_does_not_expose_transport_or_vendor_sdk_types()
    {
        Assembly assembly = typeof(ITransportChannel).Assembly;
        IEnumerable<Type> exposedTypes = assembly
            .GetExportedTypes()
            .SelectMany(GetApiTypes)
            .Distinct();

        string[] forbidden = exposedTypes
            .Where(type => ForbiddenNamespacePrefixes.Any(
                prefix => (type.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Every_async_contract_method_accepts_cancellation_as_its_last_parameter()
    {
        Assembly assembly = typeof(ITransportChannel).Assembly;
        MethodInfo[] invalidMethods = assembly
            .GetExportedTypes()
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMethods())
            .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Where(method => method.GetParameters().LastOrDefault()?.ParameterType != typeof(CancellationToken))
            .ToArray();

        Assert.Empty(invalidMethods);
    }

    private static IEnumerable<Type> GetApiTypes(Type declaringType)
    {
        yield return declaringType;

        foreach (Type interfaceType in declaringType.GetInterfaces())
        {
            yield return Unwrap(interfaceType);
        }

        foreach (PropertyInfo property in declaringType.GetProperties())
        {
            yield return Unwrap(property.PropertyType);
        }

        foreach (MethodInfo method in declaringType.GetMethods())
        {
            yield return Unwrap(method.ReturnType);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return Unwrap(parameter.ParameterType);
            }
        }
    }

    private static Type Unwrap(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
    }
}
