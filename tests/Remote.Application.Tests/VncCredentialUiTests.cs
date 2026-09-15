using Remote.Desktop.ViewModels;

namespace Remote.Application.Tests;

public sealed class VncCredentialUiTests
{
    [Fact]
    public void VncConnectionEditor_UsesPasswordOnlyLanguage()
    {
        var viewModel = new MainViewModel
        {
            EditProtocol = "vnc",
        };

        Assert.True(viewModel.IsEditingVnc);
        Assert.False(viewModel.EditCredentialUsesUsername);
        Assert.Equal("VNC 密碼", viewModel.ConnectionCredentialEditorTitle);
        Assert.Contains("不會傳送使用者名稱", viewModel.ConnectionCredentialEditorDescription);
    }

    [Fact]
    public void VncIdentityCard_ClearsUnusedUsernameAndDomain()
    {
        var viewModel = new MainViewModel
        {
            NewIdentityUsername = "unused-user",
            NewIdentityDomain = "unused-domain",
        };

        viewModel.NewIdentityProtocol = "vnc";

        Assert.False(viewModel.IdentityUsesUsername);
        Assert.Empty(viewModel.NewIdentityUsername);
        Assert.Empty(viewModel.NewIdentityDomain);
        Assert.StartsWith("VNC 密碼", viewModel.IdentitySecretPlaceholder);
    }

    [Fact]
    public void VncSessionPrompt_DoesNotRequestUsername()
    {
        var viewModel = new MainViewModel();
        viewModel.SelectedConnection = Assert.Single(viewModel.Connections, item => item.Profile.ProtocolId == "vnc");

        Assert.True(viewModel.RequiresSessionCredentialInput);
        Assert.False(viewModel.RequiresSessionUsernameInput);
        Assert.Contains("VNC 密碼", viewModel.SessionCredentialInputLabel);
        Assert.Equal("VNC 密碼來源", viewModel.SelectedCredentialSourceHeading);
        Assert.Equal("僅使用 VNC 密碼", viewModel.SelectedCredentialUsername);
    }
}
