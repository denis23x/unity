// Stable reference to the player. Lives in root scope so cross-scene consumers
// (NPCs in chunk scenes, HUD, audio, quest logic) can [Inject] it instead of
// GameObject.Find / singletons / SerializeField-across-scene-boundary.
//
// Kept intentionally minimal — extend as concrete needs land (Health, Inventory,
// OnDeath, …). Contract stays; PlayerService implementation can be swapped for
// a non-MonoBehaviour version without touching consumers.

using UnityEngine;

namespace ProjectName.World
{
    public interface IPlayerService
    {
        Transform Transform { get; }
    }
}
