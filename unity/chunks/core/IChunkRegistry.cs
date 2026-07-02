// Live phone book of loaded chunks. Keyed by ChunkCoord; value is the chunk's
// IChunkContext.
//
// Mutated only by the root-scope ChunkRegistryBinder (Register on chunk load,
// Unregister right before the chunk scene unloads). Consumers subscribe to
// onReady / onGone; they never call Register/Unregister themselves.
//
// WaitForAsync is the async barrier for teleports: pairs with the binder's
// EnsureChunkContextReady to guarantee "not just scene loaded, but the chunk's
// DI scope is built and its context is in the registry".

using System;
using System.Threading.Tasks;

namespace ProjectName.World
{
    public interface IChunkRegistry
    {
        IChunkContext Get(ChunkCoord coord);
        bool TryGet(ChunkCoord coord, out IChunkContext context);

        // Binder-only. Fires onReady / onGone to all current subscribers.
        void Register(ChunkCoord coord, IChunkContext context);
        void Unregister(ChunkCoord coord);

        // If already registered — resolves immediately. Otherwise resolves on
        // the next Register call for this coord. Never resolves with null.
        Task<IChunkContext> WaitForAsync(ChunkCoord coord);

        // Dispose the returned handle to stop receiving events. onReady fires
        // once for every chunk that was already registered at subscribe time,
        // so subscribers don't miss chunks loaded before they started listening.
        IDisposable Subscribe(Action<ChunkCoord, IChunkContext> onReady, Action<ChunkCoord> onGone);
    }
}
