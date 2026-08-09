using System.Runtime.CompilerServices;

// A durable boundary is built by the journal, never by a caller, so its factory is internal. Test
// assemblies are the one legitimate exception: they have to be able to stage an operation that was
// left unfinished, which is the state the recovery path exists to get out of.
[assembly: InternalsVisibleTo("Winora.App.Tests")]
[assembly: InternalsVisibleTo("Winora.Core.Tests")]
[assembly: InternalsVisibleTo("Winora.ElevatedHost")]
[assembly: InternalsVisibleTo("Winora.Infrastructure")]
[assembly: InternalsVisibleTo("Winora.Infrastructure.Tests")]
[assembly: InternalsVisibleTo("Winora.Infrastructure.ProcessHost")]
