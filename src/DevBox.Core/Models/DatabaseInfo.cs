namespace DevBox.Core.Models;

public sealed record DatabaseInfo(
    string Name,
    string CharacterSet,
    string Collation,
    long SizeBytes);
