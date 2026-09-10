namespace DevBox.Core.Services;

internal static class StringComparisonExtensions
{
    public static bool EndsWith(this string value, char suffix, StringComparison comparison) =>
        value.EndsWith(suffix.ToString(), comparison);

    public static bool Contains(this string value, char character, StringComparison comparison) =>
        value.Contains(character.ToString(), comparison);
}
