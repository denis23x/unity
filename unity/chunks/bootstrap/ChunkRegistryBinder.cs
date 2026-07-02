// Bridges ChunkStreamer's plain C# events into IChunkRegistry — the ONLY place
// where the streamer and the DI world touch. The streamer has no VContainer
// dependency and nothing is injected into it; this binder listens to its public
// events from the outside. Keep it that way: entangling DI into the streamer's
// load loop is what broke the previous attempt.
//
// Timing guarantees this relies on (both hold in the committed streamer):
//   * OnAfterChunkLoad fires from the Addressables Completed callback, which
//     runs after scene activation — i.e. after every root GameObject's Awake,
//     so the chunk's ChunkLifetimeScope has already built its container and
//     ChunkInstaller.Resolver is populated by the time we Register.
//   * OnBeforeChunkUnload fires before UnloadSceneAsync — chunk GameObjects
//     are still alive while registry subscribers' onGone handlers run.
//
// This binder subscribes during entry-point Start (before any chunk loads), so
// its Unregister runs before any later-subscribed OnBeforeChunkUnload handler —
// registry consumers hear about the unload first, then persistence hooks.

using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer.Unity;

namespace ProjectName.World
{
    public sealed class ChunkRegistryBinder : IStartable, IDisposable
    {
        readonly ChunkStreamer _streamer;
        readonly IChunkRegistry _registry;

        public ChunkRegistryBinder(ChunkStreamer streamer, IChunkRegistry registry)
        {
            _streamer = streamer;
            _registry = registry;
        }

        public void Start()
        {
            _streamer.OnAfterChunkLoad += HandleChunkLoaded;
            _streamer.OnBeforeChunkUnload += HandleChunkUnloading;
        }

        public void Dispose()
        {
            _streamer.OnAfterChunkLoad -= HandleChunkLoaded;
            _streamer.OnBeforeChunkUnload -= HandleChunkUnloading;
        }

        // Locates the IChunkContext component on the newly-loaded scene's root
        // GameObjects and publishes it. Any root GameObject is acceptable,
        // though in practice it sits on _Logic (see ChunkInstaller). A missing
        // context is a warning, not an error — geometry-only chunks are legal.
        void HandleChunkLoaded(ChunkCoord coord, Scene scene)
        {
            IChunkContext ctx = null;
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].TryGetComponent(out IChunkContext found)) { ctx = found; break; }
            }

            if (ctx == null)
            {
                Debug.LogWarning($"[ChunkRegistryBinder] No IChunkContext in '{scene.name}' — registry consumers won't see this chunk. Re-run ChunkManager Import to attach the scope components.");
                return;
            }

            _registry.Register(coord, ctx);
        }

        void HandleChunkUnloading(ChunkCoord coord, Scene scene)
        {
            _registry.Unregister(coord);
        }

        /// <summary>
        /// Like ChunkStreamer.EnsureChunkLoaded, but also waits until the
        /// chunk's IChunkContext is in the registry. Use this for teleports /
        /// fast travel that need to read facets (spawn points, quest data)
        /// from the destination chunk — EnsureChunkLoaded alone returns when
        /// the scene is loaded, which is one event-dispatch earlier than the
        /// registry publish.
        ///
        /// Returns null if the chunk doesn't exist, the load failed, or the
        /// chunk is geometry-only (no IChunkContext).
        /// </summary>
        public async Task<IChunkContext> EnsureChunkContextReady(ChunkCoord coord)
        {
            if (!await _streamer.EnsureChunkLoaded(coord)) return null;

            // The streamer fires OnAfterChunkLoad (→ our Register) before it
            // resolves the EnsureChunkLoaded barrier, so a chunk that has a
            // context is in the registry by now. Absent here = geometry-only
            // scene — return null rather than wait for a publish that will
            // never come.
            return _registry.TryGet(coord, out var ctx) ? ctx : null;
        }
    }
}
