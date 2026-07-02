# Chunks Bootstrap (root DI scope)

The one-time setup that makes the whole DI layer exist. Everything in
this folder lives in your **bootstrap scene** (the scene you actually
press Play in) and stays alive for the entire session — while chunk
scenes come and go around it.

This README explains **what it does and why** in plain language.

## The mental model

Think of the root scope as **city hall**. It keeps the records every
citizen might need: where the player is (`IPlayerService`), which chunks
are currently loaded (`IChunkRegistry`). Citizens — services, NPCs,
whole chunk scenes — don't wander around looking for each other
(`GameObject.Find`, singletons); they file a request (`[Inject]`) and
city hall hands them what they asked for.

Chunk scenes each open their own small branch office
(`ChunkLifetimeScope`, see `scope/`) that reports to city hall — so
anything inside a chunk can also request the shared records.

## What lives here

### `RootLifetimeScope` — city hall itself

A VContainer `LifetimeScope` on an empty GameObject. Its `Configure`
registers everything below. **No inspector wiring**: it finds
`PlayerService` and `ChunkStreamer` in its own scene automatically at
startup (`RegisterComponentInHierarchy`), so there are no references to
drag and none to forget.

### `ChunkRegistry` — the phone book, implemented

The live `ChunkCoord → IChunkContext` map behind `IChunkRegistry`. Two
behaviors worth knowing:

- **Replay on subscribe.** Subscribing fires `onReady` once for every
  chunk already loaded — services that start late still see the full
  world state.
- **`WaitForAsync` never resolves with null.** If you wait on a chunk
  that never loads, the task just stays pending — callers who can hit
  that case should use `ChunkRegistryBinder.EnsureChunkContextReady`,
  which checks existence first.

### `ChunkRegistryBinder` — the bridge to the streamer

The **only** place where the streamer and the DI world touch. The
streamer itself is DI-free and never modified; this binder subscribes
to its two public events from the outside:

1. Chunk scene finishes loading → its `ChunkLifetimeScope` has already
   built (scene activation runs `Awake` first) → binder finds the
   `IChunkContext` on the scene roots → `registry.Register`.
2. Chunk is about to unload → binder calls `registry.Unregister`
   **while the chunk's GameObjects are still alive**, so `onGone`
   subscribers can say goodbye properly → then the scene is destroyed.

It also provides the fast-travel helper:

```csharp
var ctx = await _chunks.EnsureChunkContextReady(dest); // null = no such chunk
```

which resolves only when the destination chunk is loaded **and**
published to the registry — the safe moment to read its facets.

### `PlayerService` — the player, findable

A thin MonoBehaviour on the Player GameObject implementing
`IPlayerService`. Grow it only when something concrete needs more than
`Transform`.

### `ChunkContentLogger` — the development harness

Logs every register/unregister with two verdicts per chunk:
`facets=[…]` (what the chunk exposes) and `parentChain=OK/BROKEN`
(proof that the chunk's scope is correctly parented to root — it
resolves `IPlayerService` *through the chunk's own resolver*, which
only works across a healthy parent link). One line per chunk
transition. When you stop wanting it: delete the file and the
`RegisterEntryPoint<ChunkContentLogger>()` line in `RootLifetimeScope`.

## Scene setup (once)

1. Player GameObject → add `PlayerService`.
2. Empty GameObject → add `ChunkStreamer`, set its Target to the player.
3. Empty GameObject → add `RootLifetimeScope`. Leave its **Parent**
   field as None — it *is* the root.

Press Play: the logger's startup line confirms the graph is up.

## Files in this folder

| File | What it is |
|---|---|
| `RootLifetimeScope.cs` | The root VContainer scope; registers everything below. |
| `ChunkRegistry.cs` | `IChunkRegistry` implementation: live map + replay + async wait. |
| `ChunkRegistryBinder.cs` | Streamer events → registry; `EnsureChunkContextReady`. |
| `PlayerService.cs` | `IPlayerService` implementation on the Player GameObject. |
| `ChunkContentLogger.cs` | Dev-time logger proving the pipeline on every chunk. Removable. |

## Related

- Contracts implemented here: `unity/chunks/core/`.
- The per-chunk side: `unity/chunks/scope/` — scopes that parent
  themselves to this one.
- The load/unload engine: `unity/chunks/streamer/` — publishes the two
  events this folder listens to; contains consumer code examples.
- See the project root `README.md` for the full pipeline overview.
