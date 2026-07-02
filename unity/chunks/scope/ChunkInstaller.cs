// Concrete IChunkContext, sits on the _Logic root of every chunk scene.
//
// Coord is parsed from the scene name (Chunk_XX_YY → (X, Y)) — never a
// SerializeField. That eliminates the class of bugs where a scene gets renamed
// or duplicated and the inspector value drifts from the truth on disk.
//
// Resolver is injected by VContainer post-construction (ChunkLifetimeScope
// registers this component in its container). Consumers read it via
// IChunkContext.Resolver and TryResolve<TFacet> the specific facets they care
// about — instead of forcing every chunk to know about every facet type.

using UnityEngine;
using VContainer;

namespace ProjectName.World
{
    [DisallowMultipleComponent]
    public sealed class ChunkInstaller : MonoBehaviour, IChunkContext
    {
        ChunkCoord _coord;
        IObjectResolver _resolver;
        bool _coordParsed;

        public ChunkCoord Coord
        {
            get
            {
                // Awake may not have run yet when a consumer looks us up (edge
                // case with additive-load hierarchy scanning). Parse lazily; it's
                // trivial and safe to repeat.
                if (!_coordParsed) ParseCoord();
                return _coord;
            }
        }

        public IObjectResolver Resolver => _resolver;

        [Inject]
        public void Construct(IObjectResolver resolver)
        {
            _resolver = resolver;
        }

        void Awake()
        {
            if (!_coordParsed) ParseCoord();
        }

        void ParseCoord()
        {
            _coordParsed = true;
            if (!ChunkCoordParser.TryParseFromSceneName(gameObject.scene.name, out _coord))
                Debug.LogError($"[ChunkInstaller] Could not parse chunk coord from scene name '{gameObject.scene.name}'. Expected trailing _XX_YY.");
        }
    }
}
