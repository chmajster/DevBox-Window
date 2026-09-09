namespace DevBox.Core.Models;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record DiagnosticCheck(
    string Category,
    string Name,
    bool Success,
    string Details,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error);
