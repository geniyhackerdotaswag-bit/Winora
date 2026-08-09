using System.Runtime.InteropServices;
using System.Text;

namespace Winora.App.Services;

/// <summary>Reads this package's own shell identity.</summary>
public interface IPackageIdentityAccessor
{
    /// <summary>
    /// The Application User Model ID, which is how the shell addresses a packaged app. False when
    /// this process has no package identity.
    /// </summary>
    bool TryGetApplicationUserModelId(out string aumid);
}

/// <inheritdoc />
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentapplicationusermodelid
/// </remarks>
public sealed class PackageIdentityAccessor : IPackageIdentityAccessor
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    public bool TryGetApplicationUserModelId(out string aumid)
    {
        aumid = string.Empty;

        // Two-call pattern: the first asks how much room the identifier needs.
        uint length = 0;
        var probe = GetCurrentApplicationUserModelId(ref length, null);
        if (probe != ErrorInsufficientBuffer || length == 0)
        {
            return false;
        }

        var buffer = new StringBuilder((int)length);
        if (GetCurrentApplicationUserModelId(ref length, buffer) != ErrorSuccess)
        {
            return false;
        }

        aumid = buffer.ToString();
        return aumid.Length > 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentApplicationUserModelId(
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);
}
