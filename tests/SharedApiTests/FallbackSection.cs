using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace ConfigFerret.Tests.Common;

/// <summary>
/// A section of a type of an app's own: it reads from another section and falls back to a root of defaults, so the root
/// it keeps is not the one its values come from.
/// </summary>
internal sealed class FallbackSection(IConfigurationSection inner, IConfigurationRoot defaults) : IConfigurationSection
{
    private readonly IConfigurationSection _inner = inner;
    private readonly IConfigurationRoot _defaults = defaults;

    public string? this[string key]
    {
        get => _inner[key] ?? _defaults[key];
        set => _inner[key] = value;
    }

    public string Key => _inner.Key;

    public string Path => _inner.Path;

    public string? Value
    {
        get => _inner.Value;
        set => _inner.Value = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => _inner.GetChildren();

    public IChangeToken GetReloadToken() => _inner.GetReloadToken();

    public IConfigurationSection GetSection(string key) => _inner.GetSection(key);
}
