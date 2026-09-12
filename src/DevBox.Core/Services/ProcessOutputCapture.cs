using System.Text;

namespace DevBox.Core.Services;

internal static class ProcessOutputCapture
{
    public const int DefaultMaximumCharacters = 1_048_576;

    public static async Task<string> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters = DefaultMaximumCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var buffer = new char[8192];
        var builder = new StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
                builder.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining)
                truncated = true;
        }

        if (truncated)
            builder.Append(Environment.NewLine).Append("[output truncated by DevBox]");
        return builder.ToString();
    }
}
