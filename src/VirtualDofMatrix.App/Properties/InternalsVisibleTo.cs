using System.Runtime.CompilerServices;

// Overview: expose internals to tests so rendering/config helpers can be verified without widening public API surface.
[assembly: InternalsVisibleTo("VirtualDofMatrix.Tests")]
