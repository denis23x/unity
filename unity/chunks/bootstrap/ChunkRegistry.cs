// IChunkRegistry implementation. Main-thread only (all events fire from the
// ChunkRegistryBinder's handlers, which run inside ChunkStreamer's load/unload
// callbacks on Unity's main thread — no locking needed).
//
// Two subtleties to be aware of:
//
// 1) Register replay for late subscribers. Subscribe fires onReady synchronously
//    for every chunk that is already registered at that moment. This lets
//    services that start later (e.g. loaded on demand) see the world state
//    without waiting for the next chunk transition. onGone never replays.
//
// 2) WaitForAsync stores the TCS per coord. If the chunk is never registered
//    (e.g. streamer cancelled the load), the TCS stays pending forever. That's
//    intentional — the caller who awaits was asking "wake me when this chunk
//    becomes available"; if it never does, the caller should have a timeout /
//    cancellation of its own. The binder's EnsureChunkContextReady handles the
//    "chunk doesn't exist" case up front by checking EnsureChunkLoaded's bool
//    result first.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ProjectName.World
{
    public sealed class ChunkRegistry : IChunkRegistry
    {
        readonly Dictionary<ChunkCoord, IChunkContext> _contexts = new();
        readonly Dictionary<ChunkCoord, List<TaskCompletionSource<IChunkContext>>> _waiters = new();
        readonly List<Subscription> _subscriptions = new();

        public IChunkContext Get(ChunkCoord coord)
            => _contexts.TryGetValue(coord, out var ctx) ? ctx : null;

        public bool TryGet(ChunkCoord coord, out IChunkContext context)
            => _contexts.TryGetValue(coord, out context);

        public void Register(ChunkCoord coord, IChunkContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            // Double-register would leak the previous context's consumers. Treat as
            // a bug in the binder, not something to silently paper over.
            if (_contexts.ContainsKey(coord))
                throw new InvalidOperationException($"[ChunkRegistry] {coord} already registered.");

            _contexts[coord] = context;

            for (int i = 0; i < _subscriptions.Count; i++)
                _subscriptions[i].OnReady?.Invoke(coord, context);

            if (_waiters.TryGetValue(coord, out var list))
            {
                _waiters.Remove(coord);
                for (int i = 0; i < list.Count; i++)
                    list[i].TrySetResult(context);
            }
        }

        public void Unregister(ChunkCoord coord)
        {
            if (!_contexts.Remove(coord)) return;

            for (int i = 0; i < _subscriptions.Count; i++)
                _subscriptions[i].OnGone?.Invoke(coord);
        }

        public Task<IChunkContext> WaitForAsync(ChunkCoord coord)
        {
            if (_contexts.TryGetValue(coord, out var ctx))
                return Task.FromResult(ctx);

            if (!_waiters.TryGetValue(coord, out var list))
            {
                list = new List<TaskCompletionSource<IChunkContext>>();
                _waiters[coord] = list;
            }

            // RunContinuationsAsynchronously: awaited continuation must not run
            // synchronously inside Register, which itself runs during the
            // streamer's load callback.
            var tcs = new TaskCompletionSource<IChunkContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            list.Add(tcs);
            return tcs.Task;
        }

        public IDisposable Subscribe(Action<ChunkCoord, IChunkContext> onReady, Action<ChunkCoord> onGone)
        {
            var sub = new Subscription(this, onReady, onGone);
            _subscriptions.Add(sub);

            // Replay currently-registered chunks so late subscribers don't miss
            // the world state that already loaded before they subscribed.
            if (onReady != null)
                foreach (var kv in _contexts)
                    onReady(kv.Key, kv.Value);

            return sub;
        }

        sealed class Subscription : IDisposable
        {
            ChunkRegistry _owner;
            public Action<ChunkCoord, IChunkContext> OnReady;
            public Action<ChunkCoord> OnGone;

            public Subscription(ChunkRegistry owner,
                                Action<ChunkCoord, IChunkContext> onReady,
                                Action<ChunkCoord> onGone)
            {
                _owner = owner;
                OnReady = onReady;
                OnGone = onGone;
            }

            public void Dispose()
            {
                if (_owner == null) return;
                _owner._subscriptions.Remove(this);
                _owner = null;
                OnReady = null;
                OnGone = null;
            }
        }
    }
}
