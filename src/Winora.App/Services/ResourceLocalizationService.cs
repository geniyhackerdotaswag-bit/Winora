using Microsoft.Windows.ApplicationModel.Resources;

namespace Winora.App.Services;

/// <inheritdoc />
public sealed class ResourceLocalizationService : ILocalizationService
{
    private readonly ResourceMap? _map;

    public ResourceLocalizationService()
    {
        try
        {
            _map = new ResourceManager().MainResourceMap.TryGetSubtree("Resources");
        }
        catch (Exception)
        {
            // Unpackaged launches can fail to locate the generated .pri. Report it through
            // IsAvailable instead of taking the whole shell down at construction time.
            _map = null;
        }

        IsAvailable = _map is not null && !string.IsNullOrEmpty(Lookup("App_Title"));
    }

    public bool IsAvailable { get; }

    public string Get(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        var value = Lookup(Normalize(resourceKey));
        return string.IsNullOrEmpty(value) ? $"⟦{resourceKey}⟧" : value;
    }

    /// <summary>
    /// Stable reason codes look like <c>winora.capability.unreadable-state</c>, but MRT treats a dot
    /// as a resource-map path separator, so such a key never resolves as written. Domain code should
    /// not have to know that, so the separators are normalised here instead of forcing every caller
    /// to invent a second spelling of its own identifier.
    /// </summary>
    private static string Normalize(string resourceKey) =>
        resourceKey.Replace('.', '_').Replace('-', '_');

    private string Lookup(string resourceKey)
    {
        if (_map is null)
        {
            return string.Empty;
        }

        try
        {
            return _map.TryGetValue(resourceKey)?.ValueAsString ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
