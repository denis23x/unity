// Verification harness for the DI pipeline. Logs container startup plus every
// register/unregister event fired by IChunkRegistry.
//
// Purpose: eyeball each pipeline stage on Play.
//   Stage 1 (root graph only): prints the startup line, nothing else — the
//     registry stays empty because nothing publishes to it yet.
//   Stage 3 (binder wired): expected sequence when walking into a fresh chunk:
//       1. ChunkStreamer starts LoadSceneAsync for Chunk_XX_YY.
//       2. ChunkLifetimeScope inside that scene builds its container.
//       3. ChunkRegistryBinder publishes to IChunkRegistry.
//       4. This logger prints "► register (X, Y)".
//     And on walking away:
//       5. Binder unregisters on OnBeforeChunkUnload.
//       6. This logger prints "◄ unregister (X, Y)".
//       7. UnloadSceneAsync destroys the scene's GameObjects.
//
// Remove or comment out the RegisterEntryPoint call in RootLifetimeScope once
// the pipeline is trusted — this is spam in a production build.

using System;
using System.Text;
using VContainer;
using VContainer.Unity;

namespace ProjectName.World
{
    public sealed class ChunkContentLogger : IStartable, IDisposable
    {
        readonly IChunkRegistry _registry;
        IDisposable _subscription;

        public ChunkContentLogger(IChunkRegistry registry) { _registry = registry; }

        public void Start()
        {
            _subscription = _registry.Subscribe(OnReady, OnGone);
            UnityEngine.Debug.Log("[ChunkContentLogger] Root DI graph up — subscribed to IChunkRegistry.");
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        void OnReady(ChunkCoord coord, IChunkContext ctx)
        {
            var sb = new StringBuilder();
            sb.Append("[ChunkContentLogger] ► register ").Append(coord).Append(" facets=[");
            bool any = false;
            if (ctx.Resolver.TryResolve<IPatrolFacet>(out _)) { sb.Append("Patrol"); any = true; }
            if (!any) sb.Append("none");
            sb.Append(']');

            // Parent-chain proof: IPlayerService is registered ONLY in the root
            // scope, so resolving it through the chunk's own resolver succeeds
            // only if ChunkLifetimeScope is correctly parented to
            // RootLifetimeScope (FindParent worked).
            sb.Append(" parentChain=").Append(ctx.Resolver.TryResolve<IPlayerService>(out _) ? "OK" : "BROKEN");

            UnityEngine.Debug.Log(sb.ToString());
        }

        void OnGone(ChunkCoord coord)
        {
            UnityEngine.Debug.Log($"[ChunkContentLogger] ◄ unregister {coord}");
        }
    }
}
