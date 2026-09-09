namespace DevBox.Core.Models;

public sealed record DatabaseConnectionOptions(
    string Host = "127.0.0.1",
    int Port = 3306,
    string User = "root",
    string? Password = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(User);
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535.");
        }
    }
}
