using Remote.Application;

namespace Remote.Application.Tests;

public sealed class RemoteSessionServiceTests
{
    [Fact]
    public void GetStatus_WhenCreated_ReturnsReady()
    {
        var service = new RemoteSessionService();

        var status = service.GetStatus();

        Assert.Equal("Ready", status.State);
        Assert.NotEmpty(status.Detail);
    }
}
