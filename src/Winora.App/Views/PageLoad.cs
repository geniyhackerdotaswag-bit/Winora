namespace Winora.App.Views;

/// <summary>
/// Ties a page's load to the time that page is actually on screen.
/// </summary>
/// <remarks>
/// <para>
/// A page loads by probing Windows, which takes awaits, and each await is a point where the person
/// can click something else in the pane. Without this, the load keeps running against a page that
/// has been navigated away from and finishes by filling a list WinUI has already torn down. That
/// raises <c>COMException (0x80004005)</c> out of <c>OnCollectionChanged</c> and takes the whole
/// application down — and it looks, from the outside, exactly like the window closing by itself.
/// </para>
/// <para>
/// Found on 2026-08-27 by walking every screen twice at speed, which is not an unusual thing for a
/// person to do. The screens had been walked before, slowly, and slowly it never happens.
/// </para>
/// </remarks>
internal sealed class PageLoad
{
    private CancellationTokenSource? _current;

    /// <summary>
    /// Runs a load that is abandoned if the page is left.
    /// </summary>
    /// <remarks>
    /// Cancellation is not an error here and is not reported as one: leaving a page before it has
    /// finished loading is an ordinary thing to do, and the work simply stops mattering.
    /// </remarks>
    public async Task RunAsync(Func<CancellationToken, Task> load)
    {
        ArgumentNullException.ThrowIfNull(load);

        Leave();

        var source = new CancellationTokenSource();
        _current = source;

        try
        {
            await load(source.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The page was left while it was still reading. Nothing to say.
        }
        finally
        {
            if (ReferenceEquals(_current, source))
            {
                _current = null;
            }

            source.Dispose();
        }
    }

    /// <summary>Called when the page goes off screen, so a load in flight stops there.</summary>
    public void Leave()
    {
        var running = _current;
        _current = null;

        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished on its own.
        }
    }
}
