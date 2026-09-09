using Remote.Infrastructure.Protocols.Rdp;
using Remote.Protocols;

namespace Remote.Application.Tests;

public sealed class RdpSessionPermissionPolicyTests
{
    [Fact]
    public void Resolve_ViewOnly_DisablesInputAndOutboundRedirections()
    {
        var result = RdpSessionPermissionPolicy.Resolve(SessionAccessMode.ViewOnly, new RdpConnectionSettings
        {
            RedirectClipboard = true,
            RedirectPrinters = true,
            RedirectDrives = true,
        });

        Assert.False(result.AcceptsInput);
        Assert.False(result.RedirectClipboard);
        Assert.False(result.RedirectPrinters);
        Assert.False(result.RedirectDrives);
        Assert.False(result.RedirectMicrophone);
        Assert.False(result.RedirectCamera);
    }

    [Fact]
    public void Resolve_Interactive_PreservesConnectionSettings()
    {
        var result = RdpSessionPermissionPolicy.Resolve(SessionAccessMode.Interactive, new RdpConnectionSettings
        {
            RedirectClipboard = true,
            RedirectPrinters = false,
            RedirectDrives = true,
            RedirectMicrophone = true,
            RedirectCamera = true,
        });

        Assert.True(result.AcceptsInput);
        Assert.True(result.RedirectClipboard);
        Assert.False(result.RedirectPrinters);
        Assert.True(result.RedirectDrives);
        Assert.True(result.RedirectMicrophone);
        Assert.True(result.RedirectCamera);
    }
}
