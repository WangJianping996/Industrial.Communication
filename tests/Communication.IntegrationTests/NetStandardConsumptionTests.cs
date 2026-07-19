using Communication.Abstractions.Models;

namespace Communication.IntegrationTests;

public sealed class NetStandardConsumptionTests
{
    [Fact]
    public void Net10_host_can_consume_the_netstandard_contract_assembly()
    {
        CommunicationResult<string> result = CommunicationResult<string>.Success("compatible");

        Assert.Equal("compatible", result.GetValueOrThrow());
    }
}
