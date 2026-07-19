using System.Reflection;
using Communication.Abstractions.Models;
using Communication.Protocols.OpcUa;
using Communication.Protocols.OpcUa.Models;
using Communication.Protocols.OpcUa.Simulator;

namespace Communication.ProtocolTests;

public sealed class OpcUaTests
{
    private static readonly VariableDefinition Temperature = new(
        "Temperature", "ns=2;s=Temperature", PlcDataType.Float64, Scale: 0.1);

    [Fact]
    public async Task Memory_session_supports_endpoint_discovery_and_unified_read_write()
    {
        await using var session = CreateSession(new Dictionary<string, object?>
        {
            [Temperature.Address] = 235d,
        });
        var client = new OpcUaClient(session);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        CommunicationResult<IReadOnlyList<OpcUaEndpointDescription>> discovered =
            await client.DiscoverAsync();
        Assert.True(discovered.IsSuccess);
        Assert.Single(discovered.Value!);
        Assert.Equal(OpcUaMessageSecurityMode.SignAndEncrypt, discovered.Value![0].SecurityMode);

        CommunicationResult<VariableValue> read = await client.ReadAsync(Temperature);
        Assert.True(read.IsSuccess);
        Assert.Equal(23.5d, Assert.IsType<double>(read.Value!.Value), 6);
        Assert.Equal(VariableQuality.Good, read.Value.Quality);
        Assert.True(read.Value.Timestamp <= DateTimeOffset.UtcNow);

        Assert.True((await client.WriteAsync(new PlcWriteRequest(Temperature, 31.2d))).IsSuccess);
        CommunicationResult<OpcUaNodeValue> raw = Assert.Single(await session.ReadAsync(
            new[] { Temperature.Address }));
        Assert.True(raw.IsSuccess);
        Assert.Equal(312d, Convert.ToDouble(raw.Value!.Value), 6);
    }

    [Fact]
    public async Task Batch_read_preserves_per_node_failures()
    {
        await using var session = CreateSession(new Dictionary<string, object?>
        {
            [Temperature.Address] = 200d,
        });
        var client = new OpcUaClient(session);
        await client.ConnectAsync();
        var missing = new VariableDefinition("Missing", "ns=2;s=Missing", PlcDataType.Int32);

        IReadOnlyList<CommunicationResult<VariableValue>> results = await client.ReadAsync(
            new[] { Temperature, missing });

        Assert.True(results[0].IsSuccess);
        Assert.False(results[1].IsSuccess);
        Assert.Equal(CommunicationErrorCode.InvalidAddress, results[1].Error!.Code);
    }

    [Fact]
    public async Task Subscription_maps_quality_and_timestamp_and_rebuilds_after_reconnect()
    {
        await using var session = CreateSession(new Dictionary<string, object?>
        {
            [Temperature.Address] = 200d,
        });
        var client = new OpcUaClient(session);
        await client.ConnectAsync();
        CommunicationResult<OpcUaVariableSubscription> created = await client.SubscribeAsync(
            new[] { Temperature },
            new OpcUaSubscriptionOptions { PublishingInterval = TimeSpan.FromMilliseconds(10) });
        Assert.True(created.IsSuccess);
        await using OpcUaVariableSubscription subscription = created.Value!;

        await session.SetValueAsync(Temperature.Address, 255d, VariableQuality.Uncertain);
        VariableValue first = await ReadNextAsync(subscription);
        Assert.Equal(25.5d, Assert.IsType<double>(first.Value), 6);
        Assert.Equal(VariableQuality.Uncertain, first.Quality);
        Assert.True(first.Timestamp <= DateTimeOffset.UtcNow);

        await session.SimulateReconnectAsync();
        await WaitUntilAsync(() => subscription.RestoreCount == 1);
        Assert.Equal(2, session.CreatedSubscriptionCount);
        Assert.Equal(1, session.ActiveSubscriptionCount);

        await session.SetValueAsync(Temperature.Address, 300d);
        VariableValue restored = await ReadNextAsync(subscription);
        Assert.Equal(30d, Assert.IsType<double>(restored.Value), 6);
    }

    [Fact]
    public async Task Untrusted_certificate_behavior_is_opt_in_and_emits_warning()
    {
        int secureWarnings = 0;
        await using (MemoryOpcUaSession secure = CreateSession())
        {
            secure.SecurityWarning += (_, _) => secureWarnings++;
            await secure.ConnectAsync();
        }

        int insecureWarnings = 0;
        await using (MemoryOpcUaSession insecure = CreateSession(allowUntrusted: true))
        {
            insecure.SecurityWarning += (_, args) =>
            {
                Assert.Contains("explicitly allowed", args.Message, StringComparison.OrdinalIgnoreCase);
                insecureWarnings++;
            };
            await insecure.ConnectAsync();
        }

        Assert.Equal(0, secureWarnings);
        Assert.Equal(1, insecureWarnings);
    }

    [Fact]
    public void Opcua_public_api_does_not_expose_official_sdk_types()
    {
        Assembly assembly = typeof(OpcUaClient).Assembly;
        Type[] exposed = assembly.GetExportedTypes()
            .SelectMany(GetPublicSignatureTypes)
            .Where(type => (type.Namespace ?? string.Empty).StartsWith("Opc.Ua", StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.Empty(exposed);
    }

    private static MemoryOpcUaSession CreateSession(
        IReadOnlyDictionary<string, object?>? values = null,
        bool allowUntrusted = false) => new(
        new OpcUaConnectionOptions
        {
            EndpointUrl = "opc.tcp://localhost:4840",
            Certificates = new OpcUaCertificateOptions
            {
                AllowUntrustedCertificates = allowUntrusted,
            },
        },
        values);

    private static async Task<VariableValue> ReadNextAsync(OpcUaVariableSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using IAsyncEnumerator<VariableValue> enumerator = subscription.WatchAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static IEnumerable<Type> GetPublicSignatureTypes(Type type)
    {
        yield return type;
        foreach (Type implemented in type.GetInterfaces())
        {
            foreach (Type nested in Unwrap(implemented))
            {
                yield return nested;
            }
        }

        foreach (ConstructorInfo constructor in type.GetConstructors())
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                foreach (Type nested in Unwrap(parameter.ParameterType))
                {
                    yield return nested;
                }
            }
        }

        foreach (PropertyInfo property in type.GetProperties())
        {
            foreach (Type nested in Unwrap(property.PropertyType))
            {
                yield return nested;
            }
        }

        foreach (MethodInfo method in type.GetMethods())
        {
            foreach (Type nested in Unwrap(method.ReturnType))
            {
                yield return nested;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                foreach (Type nested in Unwrap(parameter.ParameterType))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type nested in Unwrap(argument))
                {
                    yield return nested;
                }
            }
        }
    }
}
