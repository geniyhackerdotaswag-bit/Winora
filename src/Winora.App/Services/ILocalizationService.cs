namespace Winora.App.Services;

/// <summary>
/// Resolves user-facing text from resources. ViewModels hold resource keys, never literals, so the
/// UI language can change without touching them.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Returns the localized string for <paramref name="resourceKey"/>.</summary>
    /// <remarks>
    /// A missing key returns a visibly wrong marker rather than an empty string: a blank label looks
    /// like a rendering bug and hides the real cause, while a marker is obvious in a screenshot.
    /// </remarks>
    string Get(string resourceKey);

    /// <summary>True when the resource subsystem resolved at least one known key at startup.</summary>
    bool IsAvailable { get; }
}
