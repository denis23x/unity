// VContainer LifetimeScope for a chunk scene. Sits on the _Logic root next to
// ChunkInstaller (both attached automatically by ChunkManager's Import).
//
// Parenting: FindParent() locates the RootLifetimeScope living in the bootstrap
// scene, so this scope inherits access to IPlayerService, IChunkRegistry, etc.
// without any lookup code in consumers. This deliberately replaces the
// LifetimeScope.EnqueueParent approach: EnqueueParent is a global static stack,
// and holding it open across an async scene load can mis-parent any unrelated
// LifetimeScope that happens to Awake in that window (chunk loads run several
// at a time). FindParent has no such window and needs zero streamer code.
//
// If no RootLifetimeScope exists — e.g. a chunk scene is played directly in the
// editor — we warn and build parentless: the scene still runs, but root
// services won't resolve. Start from the bootstrap scene for the full graph.

using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace ProjectName.World
{
    public sealed class ChunkLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            builder.RegisterComponentInHierarchy<ChunkInstaller>().As<IChunkContext>();

            // Register known facets that may or may not be present in this
            // chunk. Absent facets simply aren't registered; TryResolve is the
            // consumer-side check. Add one line here per new facet type.
            TryRegisterFacet<PatrolFacet, IPatrolFacet>(builder);
        }

        // Facets live anywhere under _Logic (this scope's own GameObject), so a
        // child search is enough — no scene-wide scan.
        void TryRegisterFacet<TComponent, TFacet>(IContainerBuilder builder)
            where TComponent : Component, TFacet
            where TFacet : class
        {
            var found = GetComponentInChildren<TComponent>(includeInactive: true);
            if (found != null)
                builder.RegisterComponent(found).As<TFacet>();
        }

        protected override LifetimeScope FindParent()
        {
            var root = Find<RootLifetimeScope>();
            if (root == null)
                Debug.LogWarning($"[ChunkLifetimeScope] No RootLifetimeScope found — '{gameObject.scene.name}' builds parentless; root services (IPlayerService, IChunkRegistry, …) won't resolve. Play from the bootstrap scene for the full DI graph.");
            return root;
        }
    }
}
