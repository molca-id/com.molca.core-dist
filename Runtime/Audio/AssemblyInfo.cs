using System.Runtime.CompilerServices;

// Lets the EditMode test assembly exercise internal members without widening the public API.
[assembly: InternalsVisibleTo("Molca.Core.Tests")]
