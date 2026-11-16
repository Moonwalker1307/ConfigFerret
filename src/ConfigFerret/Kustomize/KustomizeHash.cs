using System.Buffers;

namespace ConfigFerret.Kustomize;

/// <summary>The hash suffix kustomize appends to a generated ConfigMap or Secret name.</summary>
internal static class KustomizeHash
{
    // The characters of kustomize's hash: hex digits with 0, 1, 3, a and e swapped for g, h, k, m and t.
    private static readonly SearchValues<char> Alphabet = SearchValues.Create("2456789bcdfghkmt");

    /// <summary>
    /// The name without its hash, when it ends with <c>-</c> and ten characters of kustomize's alphabet; otherwise
    /// null. A last word such as <c>production</c> is not a hash.
    /// </summary>
    public static string? Strip(string name)
    {
        var dash = name.LastIndexOf('-');
        return dash > 0 && name.Length - dash - 1 == 10 && name.AsSpan(dash + 1).IndexOfAnyExcept(Alphabet) < 0
            ? name[..dash]
            : null;
    }
}
