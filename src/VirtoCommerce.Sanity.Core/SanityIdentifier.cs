using System.Text.RegularExpressions;

namespace VirtoCommerce.Sanity.Core;

/// <summary>
/// Validates values that are interpolated into GROQ queries. GROQ has no parameter binding in the
/// query string the module sends, so a document id or type containing a quote could rewrite the
/// filter; anything outside Sanity's own identifier charset is rejected instead of escaped.
/// </summary>
public static partial class SanityIdentifier
{
    /// <summary>
    /// Sanity document ids and type names consist of letters, digits, '_', '-' and '.', where the dot
    /// separates path segments such as the "drafts." prefix, and they do not start with '-' or '.'.
    /// </summary>
    public static bool IsValid(string value)
    {
        return !string.IsNullOrEmpty(value) && IdentifierRegex().IsMatch(value);
    }

    // \z rather than $: in .NET "$" also matches before a trailing newline
    [GeneratedRegex(@"^[a-zA-Z0-9_][a-zA-Z0-9._-]{0,199}\z")]
    private static partial Regex IdentifierRegex();
}
