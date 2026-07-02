// First example of a chunk facet — the mechanism for extending IChunkContext
// without touching it.
//
// A chunk that contains a patrol route carries a MonoBehaviour implementing
// IPatrolFacet under its _Logic root; ChunkLifetimeScope discovers and
// registers it. Consumers (e.g. a Wolf NPC in _Logic) get it via
// [Inject] IPatrolFacet — or, if they need to run in chunks that may or may
// not have a patrol, via IChunkContext.Resolver.TryResolve.
//
// New content types (spawners, quest givers, checkpoints, …) become new facets;
// old ones are never modified.

using UnityEngine;

namespace ProjectName.World
{
    public interface IPatrolFacet
    {
        Transform PointA { get; }
        Transform PointB { get; }
    }
}
