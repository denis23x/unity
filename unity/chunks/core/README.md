# Chunks Core (shared contracts)

The vocabulary of the chunk DI system. This folder is almost entirely
**interfaces** — promises about what things can do, with no code that
does anything. Every other folder (`bootstrap/`, `scope/`, and your own
gameplay code) talks to the world through these words.

This README explains **what each contract means and why it exists**, in
plain language.

## Why keep interfaces in their own folder?

Because consumers should depend on *words*, not *implementations*. A
wolf NPC that needs the player writes `[Inject] IPlayerService` — it
doesn't know or care that today the implementation is a MonoBehaviour
sitting on the Player GameObject. Swap the implementation tomorrow
(networked player, test dummy, replay ghost) and the wolf never
recompiles, never changes, never notices.

It also keeps dependency direction clean: `bootstrap/` and `scope/`
both depend on `core/`, never on each other.

## The contracts, one by one

### `IChunkContext` — a chunk's reception desk

Every chunk scene has one point of entry for the outside world. Ask it
two things: *which chunk are you* (`Coord`) and *what do you contain*
(`Resolver` — try to resolve facets from it). Root-scope services never
dig through a chunk's GameObject hierarchy; they walk up to the desk
and ask.

### `IChunkRegistry` — the phone book of loaded chunks

Who's currently in the world? The registry maps `ChunkCoord` → that
chunk's `IChunkContext`, live. It's maintained by the DI layer
(`ChunkRegistryBinder` in `bootstrap/`) — consumers only read it:

- `Get` / `TryGet` — look up a chunk right now.
- `Subscribe(onReady, onGone)` — react to every chunk that loads or
  unloads. `onReady` also **replays** chunks that were already loaded
  when you subscribed, so late-starting services don't miss the world
  they walked into.
- `WaitForAsync(coord)` — an async barrier: resolves the moment a chunk
  gets registered (or immediately, if it already is).

### `IPlayerService` — a stable handle on the player

Instead of `GameObject.Find("Player")` or dragging the player into
every inspector, anything anywhere injects `IPlayerService` and reads
`Transform`. Deliberately tiny — it grows (Health, Inventory, …) only
when something concrete needs it.

### `IPatrolFacet` — the first facet

A **facet** is how a chunk describes a piece of content it contains —
without `IChunkContext` ever changing. A chunk that has a patrol route
carries a `PatrolFacet` component (see `scope/`); a chunk that doesn't,
doesn't. Consumers check with `Resolver.TryResolve<IPatrolFacet>()`.
New content types (spawn points, quest givers, checkpoints) become new
facet interfaces; existing ones are never modified.

### `ChunkCoordParser` — scene name → coordinate

The one piece of real code here. Extracts `(X, Y)` from a scene name
like `Chunk_05_04` by matching the **trailing** `_XX_YY` pair, so any
scene-name prefix works. It's the reason a chunk's coordinate can never
drift from the truth: the scene *file name* is the single source, not
an inspector field someone forgot to update after duplicating a scene.

## Files in this folder

| File | What it is |
|---|---|
| `IChunkContext.cs` | Per-chunk entry point: coord + facet resolver. |
| `IChunkRegistry.cs` | Live map of loaded chunks, with subscribe/replay and async wait. |
| `IPlayerService.cs` | Stable player reference for cross-scene consumers. |
| `IPatrolFacet.cs` | Reference facet interface — the extension pattern. |
| `ChunkCoordParser.cs` | Parses `ChunkCoord` from a chunk scene name. |

> `ChunkCoord` itself (the struct) lives in `streamer/ChunkStreamer.cs`,
> since the streamer is usable entirely without the DI layer.

## Related

- Implementations of these contracts: `unity/chunks/bootstrap/` (root
  scope services) and `unity/chunks/scope/` (per-chunk components).
- The component that drives loading/unloading: `unity/chunks/streamer/`.
- See the project root `README.md` for the full pipeline overview.
