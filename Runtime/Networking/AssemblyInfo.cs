using System.Runtime.CompilerServices;

// Lets the EditMode test assembly exercise internal members without widening the public API.
[assembly: InternalsVisibleTo("Molca.Core.Tests")]

// Lets Core's own editor layer author catalog entities. The network catalog types expose their
// mutators (AddEnvironment, AddService, Initialize, SetSchemaVersion, …) as internal rather than
// public: authoring is Molca.Editor's job, and a consumer project must not be able to rewrite
// configuration through the runtime API. Hub, MCP, and migration reach them through
// NetworkCatalogEditingService.
[assembly: InternalsVisibleTo("Molca.Editor")]
