namespace Winora.Infrastructure.Paths;

public abstract class JsonDestination
{
    private protected JsonDestination(
        WinoraDataPaths owner,
        string filePath,
        string documentId)
    {
        Owner = owner;
        FilePath = filePath;
        DocumentId = documentId;
    }

    public string DocumentId { get; }

    internal WinoraDataPaths Owner { get; }

    internal string FilePath { get; }
}

public sealed class AuthoritativeJsonDestination : JsonDestination
{
    internal AuthoritativeJsonDestination(
        WinoraDataPaths owner,
        string filePath,
        string documentId)
        : base(owner, filePath, documentId)
    {
    }
}

public sealed class ProjectionJsonDestination : JsonDestination
{
    internal ProjectionJsonDestination(
        WinoraDataPaths owner,
        string filePath,
        string documentId)
        : base(owner, filePath, documentId)
    {
    }

    internal string LastKnownGoodFilePath => $"{FilePath}.last-known-good";
}
