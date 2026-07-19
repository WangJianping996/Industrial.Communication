using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Produces reusable delay, timeout, disconnect, corruption, or override simulator behavior.</summary>
public interface ISimulationResponseScript
{
    /// <summary>Gets the directive for one raw simulator request.</summary>
    ValueTask<SimulationResponseDirective> GetDirectiveAsync(
        SimulationRequest request,
        CancellationToken cancellationToken = default);
}
