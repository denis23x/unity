# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository purpose

A collection of tools for working with Unity and Blender. Currently contains one paired tool: a Blender script that splits meshes into a grid of FBX chunks, and a Unity Editor script that imports those chunks into individual Unity scenes.

## Tool pair: chunks

### Blender side (`blender/chunks/chunks.py`)

Run as a script inside Blender (Text Editor → Run Script) with mesh objects selected in the viewport. Configure the constants at the top of the file before running:

- `GRID_X` / `GRID_Y` — grid dimensions (default 8×8)
- `KEEP_WHOLE_KEYWORDS` — collection name substrings whose objects are never sliced (e.g. `'Buildings'`, `'Extra'`)
- `SLICE_SIZE_RATIO` — objects larger than `chunk_size × ratio` are sliced across boundaries
- `EXPORT_CENTERED` — shifts each chunk so its FBX origin is at the chunk center (must match `PivotMode.ChunkCenter` in Unity)
- `OUTPUT_DIR` — export path, defaults to `//chunks_export/` (relative to `.blend` file)

Output: `NN_MM.fbx` files where `NN` = column (X) index and `MM` = row (Y/Z) index.

### Unity side (`unity/chunks/chunks.cs`)

Place in `Assets/Editor/`. Opens via **Tools → Chunks → Import FBX → Scenes**. All settings persist via `EditorPrefs`.

Key settings to match the Blender export:
- **FBX pivot** → `ChunkCenter` (matches `EXPORT_CENTERED=True`)
- **Axis layout** → `XZ_YUp` (matches `axis_forward='-Z'`, `axis_up='Y'`)
- **Index order** → `FirstIsCol_X_SecondIsRow_Z` (NN = col along X, MM = row along Z)

## Coordinate mapping

| Blender | Unity |
|---------|-------|
| +X      | +X (column index, first in filename) |
| +Y      | +Z (row index, second in filename) |
| +Z      | +Y (up; handled by FBX root rotation) |

## Critical invariant

The FBX importer (`use_space_transform=True`, `bake_space_transform=False`) bakes a compensating rotation onto the FBX root to convert Blender Z-up to Unity Y-up. The Unity script resets only `localPosition` of the instantiated FBX child — **never** `localRotation` or `localScale`. Overriding the rotation causes the chunk to flip sideways.

## Troubleshooting chunk alignment

- Grid mirrored along X → toggle **Invert U**
- Grid mirrored along Z → toggle **Invert V**
- Grid rotated 90° → swap **Index order**
- Use **Preview positions (log only)** to verify root positions in the Unity Console before writing any scene files
