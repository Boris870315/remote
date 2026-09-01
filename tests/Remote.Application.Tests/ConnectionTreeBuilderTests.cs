using Remote.Application.Connections;

namespace Remote.Application.Tests;

public sealed class ConnectionTreeBuilderTests
{
    [Fact]
    public void Build_WithNestedFolders_SortsAndPlacesConnections()
    {
        var infrastructureId = FolderId.New();
        var productionId = FolderId.New();
        var folders = new[]
        {
            new ConnectionFolder { Id = productionId, ParentId = infrastructureId, Name = "Production" },
            new ConnectionFolder { Id = infrastructureId, Name = "Infrastructure" },
        };
        var connection = new ConnectionProfile
        {
            Id = ConnectionId.New(),
            Name = "Windows Prod",
            Endpoint = new Uri("rdp://10.20.0.24"),
            ProtocolId = "rdp",
            FolderId = productionId,
        };

        var tree = new ConnectionTreeBuilder().Build(folders, [connection]);

        var root = Assert.Single(tree);
        var child = Assert.Single(root.Children);
        Assert.Equal("Infrastructure", root.Folder.Name);
        Assert.Same(connection, Assert.Single(child.Connections));
    }

    [Fact]
    public void Build_WithFolderCycle_RejectsTree()
    {
        var firstId = FolderId.New();
        var secondId = FolderId.New();
        var folders = new[]
        {
            new ConnectionFolder { Id = firstId, ParentId = secondId, Name = "First" },
            new ConnectionFolder { Id = secondId, ParentId = firstId, Name = "Second" },
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ConnectionTreeBuilder().Build(folders, []));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
