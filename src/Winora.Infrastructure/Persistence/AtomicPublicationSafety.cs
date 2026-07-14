namespace Winora.Infrastructure.Persistence;

internal interface IAtomicFileCleanup
{
    void Delete(ValidatedFileHandle file);
}

internal sealed class AtomicFileCleanup : IAtomicFileCleanup
{
    public void Delete(ValidatedFileHandle file) =>
        (file ?? throw new ArgumentNullException(nameof(file))).MarkDelete();
}

internal sealed record AtomicPublicationContext(
    string TemporaryPath,
    string FinalPath,
    string? BackupPath);

internal interface IAtomicPublicationRaceHook
{
    void AfterInitialIdentityValidation(AtomicPublicationContext context);

    void BeforePreparedHandleRelease()
    {
    }
}
