namespace DevBox.Core.Models;

public sealed record PhpExtensionState(
    string Name,
    bool Enabled,
    bool BinaryAvailable);
