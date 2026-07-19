using Communication.Abstractions.Models;

namespace Communication.Protocols.OpcUa.Models;

/// <summary>Selects an OPC UA message security mode without exposing SDK types.</summary>
public enum OpcUaMessageSecurityMode
{
    /// <summary>No message signing or encryption.</summary>
    None,
    /// <summary>Messages are signed.</summary>
    Sign,
    /// <summary>Messages are signed and encrypted.</summary>
    SignAndEncrypt,
}

/// <summary>Selects a supported OPC UA security policy.</summary>
public enum OpcUaSecurityPolicy
{
    /// <summary>No secure-channel policy.</summary>
    None,
    /// <summary>Basic256Sha256.</summary>
    Basic256Sha256,
    /// <summary>Aes128_Sha256_RsaOaep.</summary>
    Aes128Sha256RsaOaep,
    /// <summary>Aes256_Sha256_RsaPss.</summary>
    Aes256Sha256RsaPss,
}

/// <summary>Selects anonymous, username/password, or X.509 user authentication.</summary>
public enum OpcUaIdentityKind
{
    /// <summary>Anonymous user identity.</summary>
    Anonymous,
    /// <summary>Username and password identity.</summary>
    UsernamePassword,
    /// <summary>X.509 user certificate identity.</summary>
    Certificate,
}

/// <summary>Configures OPC UA user authentication.</summary>
public sealed record OpcUaIdentityOptions
{
    /// <summary>Gets the identity kind.</summary>
    public OpcUaIdentityKind Kind { get; init; }

    /// <summary>Gets the username.</summary>
    public string? Username { get; init; }

    /// <summary>Gets the password. Callers should avoid logging this object.</summary>
    public string? Password { get; init; }

    /// <summary>Gets the path to a PFX user certificate.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Gets the optional PFX password.</summary>
    public string? CertificatePassword { get; init; }
}

/// <summary>Configures application certificates and trust stores.</summary>
public sealed record OpcUaCertificateOptions
{
    /// <summary>Gets the directory containing the application certificate/private key.</summary>
    public string ApplicationCertificateStorePath { get; init; } = "pki/own";

    /// <summary>Gets the trusted peer certificate directory.</summary>
    public string TrustedPeerStorePath { get; init; } = "pki/trusted";

    /// <summary>Gets the trusted issuer certificate directory.</summary>
    public string TrustedIssuerStorePath { get; init; } = "pki/issuer";

    /// <summary>Gets the rejected certificate directory.</summary>
    public string RejectedCertificateStorePath { get; init; } = "pki/rejected";

    /// <summary>Gets whether a missing application certificate may be created.</summary>
    public bool AutoCreateApplicationCertificate { get; init; } = true;

    /// <summary>Gets whether untrusted server certificates are explicitly accepted.</summary>
    public bool AllowUntrustedCertificates { get; init; }

    /// <summary>Gets whether the certificate host name must match the endpoint.</summary>
    public bool ValidateCertificateHost { get; init; } = true;
}

/// <summary>Configures endpoint selection, session authentication and recovery.</summary>
public sealed record OpcUaConnectionOptions
{
    /// <summary>Gets the OPC UA discovery or endpoint URL.</summary>
    public required string EndpointUrl { get; init; }

    /// <summary>Gets the application name.</summary>
    public string ApplicationName { get; init; } = "Industrial.Communication.Client";

    /// <summary>Gets an optional application URI.</summary>
    public string? ApplicationUri { get; init; }

    /// <summary>Gets the requested message security mode.</summary>
    public OpcUaMessageSecurityMode SecurityMode { get; init; } = OpcUaMessageSecurityMode.SignAndEncrypt;

    /// <summary>Gets the requested security policy.</summary>
    public OpcUaSecurityPolicy SecurityPolicy { get; init; } = OpcUaSecurityPolicy.Basic256Sha256;

    /// <summary>Gets user authentication settings.</summary>
    public OpcUaIdentityOptions Identity { get; init; } = new();

    /// <summary>Gets application certificate and trust-store settings.</summary>
    public OpcUaCertificateOptions Certificates { get; init; } = new();

    /// <summary>Gets the requested session timeout.</summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the reconnect delay after a bad keep-alive.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Describes one discovered endpoint.</summary>
public sealed record OpcUaEndpointDescription(
    string EndpointUrl,
    OpcUaMessageSecurityMode SecurityMode,
    string SecurityPolicyUri,
    byte SecurityLevel,
    IReadOnlyList<OpcUaIdentityKind> SupportedIdentities);

/// <summary>Contains one raw OPC UA node sample.</summary>
public sealed record OpcUaNodeValue(
    string NodeId,
    object? Value,
    VariableQuality Quality,
    DateTimeOffset SourceTimestamp,
    DateTimeOffset ServerTimestamp,
    CommunicationError? Error = null);

/// <summary>Associates a node identifier with a value to write.</summary>
public sealed record OpcUaNodeWrite(string NodeId, object? Value);

/// <summary>Configures one monitored OPC UA node.</summary>
public sealed record OpcUaMonitoredNode(string NodeId, double SamplingIntervalMilliseconds = 250, uint QueueSize = 1);

/// <summary>Configures an OPC UA variable subscription.</summary>
public sealed record OpcUaSubscriptionOptions
{
    /// <summary>Gets the publishing interval.</summary>
    public TimeSpan PublishingInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the per-node sampling interval.</summary>
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the server-side monitored-item queue size.</summary>
    public uint MonitoredItemQueueSize { get; init; } = 1;

    /// <summary>Gets the client-side bounded update capacity.</summary>
    public int UpdateQueueCapacity { get; init; } = 1024;
}

/// <summary>Contains an explicit warning emitted when insecure certificate behavior is enabled.</summary>
public sealed class OpcUaSecurityWarningEventArgs : EventArgs
{
    /// <summary>Initializes a security warning.</summary>
    public OpcUaSecurityWarningEventArgs(string message) => Message = message;

    /// <summary>Gets the warning text.</summary>
    public string Message { get; }
}
