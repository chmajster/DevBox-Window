namespace DevBox.Core.Models;

public sealed record XdebugStatus(
    bool BinaryAvailable,
    bool Enabled,
    string Mode,
    int ClientPort,
    string StartWithRequest,
    string PhpIniPath,
    string BinaryPath);

public sealed record XdebugConfiguration(
    bool Enabled,
    string Mode = "debug,develop",
    int ClientPort = 9003,
    string StartWithRequest = "trigger");
