using System.Security.Cryptography;
using System.Text;

namespace DevBox.Core.Services;

internal static class LocalDomainName
{
    public static string FromName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        var label = new string(normalized
            .Select(character => char.IsLetterOrDigit(character) || character == '-' ? character : '-')
            .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(label))
            label = "project";

        if (label.Length > 63)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..8];
            var prefix = label[..54].TrimEnd('-');
            if (prefix.Length == 0)
                prefix = "project";
            label = $"{prefix}-{hash}";
        }

        return $"{label}.test";
    }
}
