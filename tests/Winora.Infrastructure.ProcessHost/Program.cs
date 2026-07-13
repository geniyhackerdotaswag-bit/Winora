using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.ProcessHost;

public static class ProcessHostMarker;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            return 64;
        }

        var mode = args[0];
        var mutexName = args[1];
        var readyPath = args[2];
        var releasePath = args[3];
        using var mutex = new GlobalPersistenceMutex(mutexName);
        return mutex.Execute(
            () =>
            {
                File.WriteAllText(readyPath, "ready");
                if (StringComparer.Ordinal.Equals(mode, "abandon"))
                {
                    Environment.Exit(0);
                }

                while (!File.Exists(releasePath))
                {
                    Thread.Sleep(10);
                }

                return 0;
            },
            CancellationToken.None);
    }
}
