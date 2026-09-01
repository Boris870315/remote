namespace Remote.Application.Connections;

/// <summary>Builds a deterministic folder tree without UI dependencies.</summary>
public sealed class ConnectionTreeBuilder
{
    public IReadOnlyList<ConnectionTreeNode> Build(
        IEnumerable<ConnectionFolder> folders,
        IEnumerable<ConnectionProfile> connections)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(connections);
        var folderList = folders.ToArray();
        EnsureValidParents(folderList);
        var connectionList = connections.ToArray();

        return BuildChildren(null, folderList, connectionList);
    }

    private static IReadOnlyList<ConnectionTreeNode> BuildChildren(
        FolderId? parentId,
        IReadOnlyList<ConnectionFolder> folders,
        IReadOnlyList<ConnectionProfile> connections) =>
        folders
            .Where(folder => folder.ParentId == parentId)
            .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(folder => new ConnectionTreeNode(
                folder,
                BuildChildren(folder.Id, folders, connections),
                connections
                    .Where(connection => connection.FolderId == folder.Id)
                    .OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()))
            .ToArray();

    private static void EnsureValidParents(IReadOnlyList<ConnectionFolder> folders)
    {
        var ids = folders.Select(folder => folder.Id).ToHashSet();
        var invalid = folders.FirstOrDefault(folder => folder.ParentId is { } parent && !ids.Contains(parent));
        if (invalid is not null)
        {
            throw new InvalidOperationException($"Folder '{invalid.Name}' references a missing parent.");
        }

        foreach (var folder in folders)
        {
            var visited = new HashSet<FolderId>();
            var current = folder;
            while (current.ParentId is { } parentId)
            {
                if (!visited.Add(current.Id))
                {
                    throw new InvalidOperationException($"Folder '{folder.Name}' contains a parent cycle.");
                }

                current = folders.First(candidate => candidate.Id == parentId);
            }
        }
    }
}
