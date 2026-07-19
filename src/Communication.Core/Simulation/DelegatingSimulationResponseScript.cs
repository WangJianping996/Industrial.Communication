using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Simulation;

/// <summary>Adapts a delegate to the protocol-independent simulator scripting contract.</summary>
public sealed class DelegatingSimulationResponseScript : ISimulationResponseScript
{
    private readonly Func<SimulationRequest, CancellationToken, ValueTask<SimulationResponseDirective>> _handler;

    /// <summary>Initializes a delegate-backed simulation script.</summary>
    public DelegatingSimulationResponseScript(
        Func<SimulationRequest, CancellationToken, ValueTask<SimulationResponseDirective>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public ValueTask<SimulationResponseDirective> GetDirectiveAsync(
        SimulationRequest request,
        CancellationToken cancellationToken = default) =>
        _handler(request, cancellationToken);
}
