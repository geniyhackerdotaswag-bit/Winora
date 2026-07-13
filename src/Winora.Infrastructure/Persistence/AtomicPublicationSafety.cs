namespace Winora.Infrastructure.Persistence;

internal interface IAtomicFileCleanup
{
    void Delete(string path);
}

internal sealed class AtomicFileCleanup : IAtomicFileCleanup
{
    public void Delete(string path) => File.Delete(path);
}

internal sealed record AtomicPublicationContext(
    string TemporaryPath,
    string FinalPath,
    string? BackupPath);

internal interface IAtomicPublicationRaceHook
{
    void AfterInitialIdentityValidation(AtomicPublicationContext context);
}
