using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.ProcessHost;
using Xunit;

namespace Winora.Infrastructure.Tests.Persistence;

public sealed class GlobalPersistenceMutexTests
{
    [Fact]
    public void Created_mutex_has_a_protected_exact_user_and_system_acl()
    {
        var name = UniqueName();
        using var persistenceMutex = new GlobalPersistenceMutex(name);
        using var opened = MutexAcl.OpenExisting(name, MutexRights.ReadPermissions);

        var security = opened.GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .ToArray();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User!;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(MutexRights.FullControl, rule.MutexRights);
            Assert.False(rule.IsInherited);
        });
        Assert.Contains(rules, rule => userSid.Equals(rule.IdentityReference));
        Assert.Contains(rules, rule => systemSid.Equals(rule.IdentityReference));
    }

    [Fact]
    public void Existing_mutex_with_a_broader_acl_is_rejected()
    {
        var name = UniqueName();
        var permissive = new MutexSecurity();
        permissive.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        using var adversarial = MutexAcl.Create(false, name, out _, permissive);

        Assert.Throws<InvalidOperationException>(() => new GlobalPersistenceMutex(name));
    }

    [Fact]
    public async Task A_real_second_process_wait_is_cancellable_while_owner_holds_mutex()
    {
        using var directory = new TemporaryDirectory();
        var name = UniqueName();
        var ready = directory.File("ready");
        var release = directory.File("release");
        using var owner = StartHost("hold", name, ready, release);
        await WaitForFileAsync(ready);
        using var contender = new GlobalPersistenceMutex(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Assert.Throws<OperationCanceledException>(() =>
            contender.Execute(() => 42, cancellation.Token));

        await File.WriteAllTextAsync(release, "release");
        await owner.WaitForExitAsync();
        Assert.Equal(0, owner.ExitCode);
    }

    [Fact]
    public async Task Abandoned_mutex_from_a_crashed_process_is_acquired_and_released_normally()
    {
        using var directory = new TemporaryDirectory();
        var name = UniqueName();
        var ready = directory.File("ready");
        var release = directory.File("unused");
        using var owner = StartHost("abandon", name, ready, release);
        await WaitForFileAsync(ready);
        await owner.WaitForExitAsync();
        using var contender = new GlobalPersistenceMutex(name);

        Assert.Equal(42, contender.Execute(() => 42, CancellationToken.None));
        Assert.Equal(43, contender.Execute(() => 43, CancellationToken.None));
    }

    private static Process StartHost(string mode, string name, string ready, string release)
    {
        var testAssembly = typeof(GlobalPersistenceMutexTests).Assembly.Location;
        var info = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--runtimeconfig");
        info.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
        info.ArgumentList.Add("--depsfile");
        info.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
        info.ArgumentList.Add(typeof(ProcessHostMarker).Assembly.Location);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(ready);
        info.ArgumentList.Add(release);
        return Process.Start(info) ?? throw new InvalidOperationException("Unable to start process host.");
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static string UniqueName() => $"Global\\Winora.Persistence.Tests.{Guid.NewGuid():N}";
}
