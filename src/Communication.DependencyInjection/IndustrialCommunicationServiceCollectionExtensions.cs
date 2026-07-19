using Communication.Abstractions.Interfaces;
using Communication.Protocols.Mc;
using Communication.Protocols.Mc.Models;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.OpcUa;
using Communication.Protocols.OpcUa.Models;
using Communication.Protocols.S7;
using Communication.Protocols.S7.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Communication.DependencyInjection;

/// <summary>Registers industrial communication clients without exposing vendor SDK types.</summary>
public static class IndustrialCommunicationServiceCollectionExtensions
{
    /// <summary>Registers a unified Modbus TCP or RTU PLC client.</summary>
    public static IServiceCollection AddModbusPlcClient(
        this IServiceCollection services,
        Func<IServiceProvider, ITransportChannel> channelFactory,
        ModbusTransportMode mode,
        Func<IServiceProvider, ModbusClientOptions>? clientOptions = null,
        Func<IServiceProvider, ModbusPlcClientOptions>? plcOptions = null,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        services = NotNull(services, nameof(services));
        channelFactory = NotNull(channelFactory, nameof(channelFactory));
        ValidateLifetime(lifetime);

        services.Add(ServiceDescriptor.Describe(
            typeof(ModbusPlcClient),
            provider => new ModbusPlcClient(
                new ModbusClient(
                    channelFactory(provider),
                    mode,
                    clientOptions?.Invoke(provider)),
                plcOptions?.Invoke(provider)),
            lifetime));
        return AddUnifiedAlias<ModbusPlcClient>(services, lifetime);
    }

    /// <summary>Registers a unified Siemens S7 ISO-on-TCP PLC client.</summary>
    public static IServiceCollection AddS7PlcClient(
        this IServiceCollection services,
        Func<IServiceProvider, ITransportChannel> channelFactory,
        Func<IServiceProvider, S7ClientOptions>? options = null,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        services = NotNull(services, nameof(services));
        channelFactory = NotNull(channelFactory, nameof(channelFactory));
        ValidateLifetime(lifetime);

        services.Add(ServiceDescriptor.Describe(
            typeof(S7PlcClient),
            provider =>
            {
                S7ClientOptions? configured = options?.Invoke(provider);
                return new S7PlcClient(
                    new S7IsoTcpDataAccess(channelFactory(provider), configured),
                    configured);
            },
            lifetime));
        return AddUnifiedAlias<S7PlcClient>(services, lifetime);
    }

    /// <summary>Registers a unified Mitsubishi MC 3E Binary PLC client.</summary>
    public static IServiceCollection AddMc3EPlcClient(
        this IServiceCollection services,
        Func<IServiceProvider, ITransportChannel> channelFactory,
        Func<IServiceProvider, McClientOptions>? options = null,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        services = NotNull(services, nameof(services));
        channelFactory = NotNull(channelFactory, nameof(channelFactory));
        ValidateLifetime(lifetime);

        services.Add(ServiceDescriptor.Describe(
            typeof(McPlcClient),
            provider =>
            {
                McClientOptions? configured = options?.Invoke(provider);
                return new McPlcClient(
                    new Mc3ETcpDataAccess(channelFactory(provider), configured),
                    configured);
            },
            lifetime));
        return AddUnifiedAlias<McPlcClient>(services, lifetime);
    }

    /// <summary>Registers a unified OPC UA client backed by the official OPC Foundation SDK.</summary>
    public static IServiceCollection AddOpcUaPlcClient(
        this IServiceCollection services,
        Func<IServiceProvider, OpcUaConnectionOptions> options,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        services = NotNull(services, nameof(services));
        options = NotNull(options, nameof(options));
        ValidateLifetime(lifetime);

        services.Add(ServiceDescriptor.Describe(
            typeof(OpcUaClient),
            provider => new OpcUaClient(new OpcFoundationSession(options(provider))),
            lifetime));
        return AddUnifiedAlias<OpcUaClient>(services, lifetime);
    }

    private static IServiceCollection AddUnifiedAlias<TClient>(
        IServiceCollection services,
        ServiceLifetime lifetime)
        where TClient : class, IPlcClient
    {
        services.Add(ServiceDescriptor.Describe(
            typeof(IPlcClient),
            provider => provider.GetRequiredService<TClient>(),
            lifetime));
        return services;
    }

    private static void ValidateLifetime(ServiceLifetime lifetime)
    {
        if (!Enum.IsDefined(typeof(ServiceLifetime), lifetime))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
    }

    private static T NotNull<T>(T? value, string parameterName)
        where T : class => value ?? throw new ArgumentNullException(parameterName);
}
