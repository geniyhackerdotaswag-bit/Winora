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
        var value = Lookup(resourceKey);
        return string.IsNullOrEmpty(value) ? $"⟦{resourceKey}⟧" : value;
    }

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
