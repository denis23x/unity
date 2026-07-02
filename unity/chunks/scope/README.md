# Chunks Scope (per-chunk DI)

What sits on the `_Logic` root of **every chunk scene**. You never add
these components by hand — ChunkManager's Import attaches them
automatically (and re-running Import never resets them).

This README explains **what it does and why** in plain language.

## The mental model

If the bootstrap folder is city hall (see `bootstrap/README.md`), each
chunk scene opens a small **branch office** when it loads. The branch
office:

- knows which chunk it represents (from the scene's file name),
- keeps a list of what this chunk offers (facets: a patrol route, a
  spawn point, …),
- reports to city hall, so anything inside the chunk can request
  city-wide services (`IPlayerService`, `IChunkRegistry`) and anything
  outside can ask what this chunk contains.

## What lives here

### `ChunkLifetimeScope` — the branch office

A VContainer `LifetimeScope` for the chunk scene. Two things matter:

**It finds its parent by itself.** On load it looks up the
`RootLifetimeScope` (`FindParent` override) and becomes its child — no
inspector wiring, no per-scene setup, nothing for the streamer to do.
If you press Play directly in a chunk scene (no bootstrap scene, no
root), it logs one clear warning and runs standalone: the scene opens,
but root services won't resolve.

**It registers what the chunk contains.** `Configure` registers the
`ChunkInstaller` as this chunk's `IChunkContext`, then checks for each
known facet component under `_Logic` and registers the ones that exist.
Adding a new facet type to the system is one `TryRegisterFacet` line.

### `ChunkInstaller` — the chunk's ID card

The concrete `IChunkContext`. Its `Coord` is parsed from the scene name
(`Chunk_05_04` → `(5, 4)`) — **never** an inspector field, so renaming
or duplicating a scene can't leave a stale coordinate behind. Its
`Resolver` is handed in by the scope, and it's what outside consumers
use to `TryResolve` facets.

### `PatrolFacet` — the reference facet

The worked example of chunk content. Drop it on a GameObject under
`_Logic`, drag two waypoint Transforms into the inspector, save the
scene. When the chunk loads, any interested service finds it:

```csharp
if (ctx.Resolver.TryResolve<IPatrolFacet>(out var patrol))
    SpawnWolfBetween(patrol.PointA, patrol.PointB);
```

## Recipe: adding your own facet

1. **Interface** in `core/` — e.g. `ISpawnFacet` with the data consumers
   need. Keep it small.
2. **Component** in this folder — MonoBehaviour implementing it, with
   inspector-authored fields (copy `PatrolFacet.cs` as the template).
3. **One line** in `ChunkLifetimeScope.Configure`:
   `TryRegisterFacet<SpawnFacet, ISpawnFacet>(builder);`
4. **Author it** — put the component under `_Logic` in the chunk scenes
   that have that content. Chunks without it simply don't register it.

Consumers never change when a new facet appears — that's the point.

## Files in this folder

| File | What it is |
|---|---|
| `ChunkLifetimeScope.cs` | Per-chunk VContainer scope; self-parents to root, registers context + facets. |
| `ChunkInstaller.cs` | Concrete `IChunkContext`; coord from scene name, resolver for facet lookups. |
| `PatrolFacet.cs` | Reference facet implementation — template for your own. |

## Related

- Contracts these implement: `unity/chunks/core/`.
- The root scope these parent to: `unity/chunks/bootstrap/`.
- Who attaches the two scope components to `_Logic`:
  `unity/chunks/manager/` (Import button).
- Who loads/unloads the scenes these live in: `unity/chunks/streamer/`.
- See the project root `README.md` for the full pipeline overview.
