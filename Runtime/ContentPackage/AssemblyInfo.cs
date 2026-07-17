using System.Runtime.CompilerServices;

// Lets the EditMode/PlayMode test assemblies exercise internal members without
// widening the public API.
[assembly: InternalsVisibleTo("Molca.Core.Tests")]
[assembly: InternalsVisibleTo("Molca.Core.PlayModeTests")]
