// A per-chunk "reception desk": one point of entry for root-scope services
// (spawners, quest system, minimap, ambience, …) to see what a specific chunk
// exposes at runtime.
//
// Lives as a MonoBehaviour on the chunk scene's _Logic root (see ChunkInstaller,
// added in a later step). The chunk's own VContainer LifetimeScope registers it
// As<IChunkContext>(), and the root-scope ChunkRegistryBinder publishes it to the
// global IChunkRegistry (keyed by ChunkCoord) so root services can find it
// without FindObjectsOfType or singletons.
//
// Extending: don't add fields here. Add a new facet interface and register it in
// that chunk's LifetimeScope. Consumers reach it via Resolver.TryResolve<TFacet>()
// — this keeps IChunkContext flat while each chunk registers only what it
// actually contains.

using VContainer;

namespace ProjectName.World
{
    public interface IChunkContext
    {
        ChunkCoord Coord { get; }
        IObjectResolver Resolver { get; }
    }
}
