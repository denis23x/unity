// Root VContainer scope, sitting on a GameObject in the bootstrap scene.
//
// Registers the long-lived services:
//   - IChunkRegistry (singleton, plain C#) — the phone book of live chunks.
//   - IPlayerService (MonoBehaviour) — stable player reference.
//   - ChunkStreamer (MonoBehaviour) — registered so ChunkRegistryBinder can
//     receive it; NOTHING is injected into the streamer itself, it stays a
//     self-contained component. The DI layer only listens to its public events.
//   - ChunkRegistryBinder (entry point) — streamer events → registry.
//   - ChunkContentLogger (entry point) — temporary verification harness.
//
// Uses RegisterComponentInHierarchy for the MonoBehaviours instead of
// [SerializeField] refs on purpose: VContainer's LifetimeScope custom editor
// does NOT show subclass SerializedFields in every version, and dragging refs
// by hand is error-prone anyway. RegisterComponentInHierarchy finds the
// components in this scope's own scene at Container.Build time — no inspector
// wiring, no null-ref traps.
//
// Chunk scenes carry their own ChunkLifetimeScope; those find this scope as
// their parent via a FindParent() override — no EnqueueParent, no streamer
// involvement.

using VContainer;
using VContainer.Unity;

namespace ProjectName.World
{
    public sealed class RootLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            builder.Register<IChunkRegistry, ChunkRegistry>(Lifetime.Singleton);

            builder.RegisterComponentInHierarchy<PlayerService>().As<IPlayerService>();
            builder.RegisterComponentInHierarchy<ChunkStreamer>();

            // AsSelf so gameplay code can [Inject] ChunkRegistryBinder for
            // EnsureChunkContextReady (fast travel, respawn).
            builder.RegisterEntryPoint<ChunkRegistryBinder>().AsSelf();

            // Verification hook — see ChunkContentLogger. Comment out (or remove)
            // once the DI pipeline is trusted.
            builder.RegisterEntryPoint<ChunkContentLogger>();
        }
    }
}
