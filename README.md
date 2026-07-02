# Unity Tools

Repo where I will publish my tools to work with Unity and surrounding software

## Chunk Manager & Streaming

The pipeline

1. Get data from [OpenStreetMap](https://www.openstreetmap.org)
    - Select the area and export it
2. Add Blosm Blender Add-on [Github](https://github.com/solido3d/blender-osm)
    - Open your downloaded data with add-on
3. Use `chunks_export.py` script in Blender to export chunks
4. Install [VContainer](https://github.com/hadashiA/VContainer) via UPM — the
   runtime uses it for chunk-scope DI
5. Copy runtime folders into your Unity project `Assets/Scripts/`:
    - `unity/chunks/core/` — shared contracts
    - `unity/chunks/bootstrap/` — RootLifetimeScope, PlayerService, ChunkRegistry, ChunkRegistryBinder
    - `unity/chunks/scope/` — ChunkLifetimeScope, ChunkInstaller, PatrolFacet
    - `unity/chunks/streamer/` — ChunkStreamer component (DI-free, self-contained)
6. Copy chunks **manager** folder into your Unity project `Assets/Editor`
    - Open it in Unity menu Tools → Chunks → Chunk Manager
7. Configure paths in Chunk Manager and Import Files (this also attaches the DI
   scope components to each chunk's `_Logic` root automatically)
8. Open Addressables tab and Create Addressable
9. In your bootstrap scene:
    - Add the `PlayerService` component to your Player GameObject
    - Create an EmptyObject and add the `ChunkStreamer` component; set Target
      parameter as your Player Controller
    - Create an EmptyObject and add the `RootLifetimeScope` component (no
      inspector wiring — it discovers `PlayerService` and `ChunkStreamer` in
      the same scene at boot)
10. Save & Play

> [!NOTE]
> The pipeline requires [A* Pathfinding Project](https://arongranberg.com/astar/)
> for navmesh, and [VContainer](https://github.com/hadashiA/VContainer) for the
> runtime DI graph.

## Awesome Links

- [Blender To Unity FBX Exporter](https://github.com/EdyJ/blender-to-unity-fbx-exporter)
- [VContainer](https://github.com/hadashiA/VContainer)