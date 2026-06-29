# Unity Tools

Repo where I will publish my tools to work with Unity and surrounding software

## Chunk Manager & Streaming

The pipeline

1. Get data from [OpenStreetMap](https://www.openstreetmap.org)
    - Select the area and export it
2. Add Blosm Blender Add-on [Github](https://github.com/solido3d/blender-osm)
    - Open your downloaded data with add-on
3. Use `chunks_export.py` script in Blender to export chunks
4. Copy chunks **manager-no-navmesh** folder into your Unity project `Assets/Editor`
    - Open it in Unity menu Tools → Chunks → Chunk Manager
5. Configure paths in Chunk Manager and Import Files
6. Open Addressables tab and Create Addressable
7. Copy chunks streamer folder into your Unity project `Assets/Scripts`
    - Create EmptyObject on Scene and add `ChunkStreamer` component
    - Set Target parameter as your Player Controller
8. Save & Play

> [!NOTE]
> Use **manager** if you have a [A* Pathfinding Project](https://arongranberg.com/astar/)\
> Use **manager-no-navmesh** if you don’t

## Awesome Links

- [Blender To Unity FBX Exporter](https://github.com/EdyJ/blender-to-unity-fbx-exporter)