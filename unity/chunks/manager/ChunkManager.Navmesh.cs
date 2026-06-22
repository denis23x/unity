// Assets/Editor/ChunkManager/ChunkManager.Navmesh.cs
//
// A* Pathfinding Pro integration. Two independent steps, both decoupled from
// Import so each can be re-run without re-importing the FBX:
//   * CreateNavmeshPrefabs / DeleteTiles — adds NavmeshPrefab to every chunk
//     inst, bakes per-chunk recast tiles into Assets/Tiles, and provides a
//     button to wipe that folder.
//   * ApplyNavmeshModifiers — attaches RecastNavmeshModifier components to
//     objects whose name matches a per-prefix preset.
// NavmeshModifierConfig + its JSON wrapper live here too because the
// Apply step owns their schema; the UI in ChunkManager.cs just consumes them.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Pathfinding;

namespace ProjectName.EditorTools
{
    public partial class ChunkManager
    {
        // Per-prefix RecastNavmeshModifier preset. Serialized via JsonUtility into
        // EditorPrefs so the window's config survives domain reloads and Unity
        // restarts. Field set mirrors the public surface of
        // RecastNavmeshModifier (A* Pathfinding Pro v5).
        [System.Serializable]
        public class NavmeshModifierConfig
        {
            public string keyPrefix = "";
            public RecastNavmeshModifier.Mode mode = RecastNavmeshModifier.Mode.WalkableSurface;
            public int surfaceID = 1;
            public RecastNavmeshModifier.GeometrySource geometrySource = RecastNavmeshModifier.GeometrySource.Auto;
            public RecastNavmeshModifier.ScanInclusion includeInScan = RecastNavmeshModifier.ScanInclusion.Auto;
            public bool dynamic = true;
            public bool solid = false;

            // UI-only fold state — kept out of JSON so it doesn't bloat EditorPrefs
            // and so toggling it in the inspector isn't treated as a config change.
            [System.NonSerialized] public bool foldout = true;
        }

        // JsonUtility refuses to serialise a bare List<T>, so the list is wrapped
        // in a named-field container that the serializer can introspect.
        [System.Serializable]
        class NavmeshModifierConfigList
        {
            public List<NavmeshModifierConfig> items = new List<NavmeshModifierConfig>();
        }

        // ── Navmesh prefab pipeline ──────────────────────────────────────
        // CreateNavmeshPrefabs operates on chunk scenes the user has already
        // opened additively. For each chunk scene matching sceneNamePrefix it
        // walks every immediate child of every root GameObject (i.e. each
        // FBX `inst` produced by Import) and adds a NavmeshPrefab component.
        // XZ size is the chunkSize (so neighbouring prefabs tile cleanly);
        // Y size is taken from the parent scene's RecastGraph
        // (forcedBoundsSize.y) so the navmesh covers the full vertical range
        // the graph was configured for. The parent scene is whichever open
        // scene carries the 'A*' GameObject; AstarPath.FindAstarPath()
        // resolves it without us needing to know its exact path.
        //
        // ScanAndSaveToFile is the same call the inspector's "Scan and save"
        // button uses. It writes a *.bytes TextAsset into Assets/Tiles (the
        // path is hard-coded inside NavmeshPrefab.SaveToFile — tilesDestFolder
        // is only used by DeleteTiles below) and assigns it back to
        // serializedNavmesh on the component, so saving the chunk scene
        // persists the reference and the runtime can re-apply tiles on load.

        public static void CreateNavmeshPrefabs(string sceneNamePrefix, float chunkSize)
        {
            // FindAstarPath wakes up AstarPath.active in edit mode — the same
            // pattern NavmeshPrefab.Reset() uses to find the recast graph.
            AstarPath.FindAstarPath();
            if (AstarPath.active == null || AstarPath.active.data.recastGraph == null)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    "No AstarPath with a RecastGraph found in any open scene. " +
                    "Open the parent scene that contains the 'A*' GameObject before running this.",
                    "OK");
                return;
            }

            var graph = AstarPath.active.data.recastGraph;
            float ySize = graph.forcedBoundsSize.y;
            if (ySize <= 0f)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"RecastGraph forcedBoundsSize.y is {ySize}, cannot bake.", "OK");
                return;
            }

            var scenes = CollectScenesByPrefix(sceneNamePrefix, requireLoaded: true);
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"No loaded chunk scenes with prefix '{sceneNamePrefix}'. " +
                    "Use 'Open Chunks Additive' first.", "OK");
                return;
            }

            int created = 0, reused = 0;
            try
            {
                for (int i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    EditorUtility.DisplayProgressBar("Creating Navmesh Prefabs",
                        $"{scene.name}  ({i + 1}/{scenes.Count})",
                        (float)i / scenes.Count);

                    bool sceneDirty = false;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        for (int c = 0; c < root.transform.childCount; c++)
                        {
                            var inst = root.transform.GetChild(c).gameObject;
                            var navmesh = inst.GetComponent<NavmeshPrefab>();
                            if (navmesh == null)
                            {
                                navmesh = inst.AddComponent<NavmeshPrefab>();
                                created++;
                            }
                            else
                            {
                                reused++;
                            }
                            navmesh.bounds = new Bounds(Vector3.zero, new Vector3(chunkSize, ySize, chunkSize));
                            navmesh.ScanAndSaveToFile();
                            sceneDirty = true;
                        }
                    }

                    if (sceneDirty) EditorSceneManager.MarkSceneDirty(scene);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[ChunkManager] NavmeshPrefab: {created} added, {reused} reused " +
                      $"across {scenes.Count} chunk scene(s); Y size {ySize:F1}m from RecastGraph. " +
                      "Scenes marked dirty — save them to persist the navmesh references.");
        }

        public static void DeleteTiles(string tilesDestFolder)
        {
            string folderPath = $"Assets/{tilesDestFolder}";
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"Tiles folder not found:\n{folderPath}", "OK");
                return;
            }

            int fileCount = Directory.GetFiles(folderPath, "*.bytes", SearchOption.AllDirectories).Length;
            if (!EditorUtility.DisplayDialog("Delete Tiles",
                    $"Delete folder '{folderPath}' and its {fileCount} .bytes file(s)?\n\n" +
                    "Chunk scenes that reference these tiles will have a missing serializedNavmesh " +
                    "until re-baked.",
                    "Delete", "Cancel"))
                return;

            if (AssetDatabase.DeleteAsset(folderPath))
            {
                AssetDatabase.Refresh();
                Debug.Log($"[ChunkManager] Deleted tiles folder: {folderPath} ({fileCount} .bytes files).");
            }
            else
            {
                Debug.LogError($"[ChunkManager] Failed to delete tiles folder: {folderPath}");
            }
        }

        // ── Navmesh modifier pipeline ────────────────────────────────────
        // Walks every currently open chunk scene that matches sceneNamePrefix
        // and recursively scans every GameObject under each root. For each
        // object whose name starts with a config's keyPrefix, attaches (or
        // reuses) a RecastNavmeshModifier and copies the config's fields onto
        // it. Configs are evaluated in list order and the first matching
        // prefix wins — list more specific prefixes before generic ones.
        //
        // Scenes are marked dirty but NOT saved automatically; the user must
        // save them (e.g. via File → Save) so the components persist. This
        // mirrors how CreateNavmeshPrefabs above leaves persistence to the
        // user, keeping the two navmesh steps consistent.

        public static void ApplyNavmeshModifiers(string sceneNamePrefix, List<NavmeshModifierConfig> configs)
        {
            var validConfigs = configs
                .Where(c => !string.IsNullOrWhiteSpace(c.keyPrefix))
                .ToList();
            if (validConfigs.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    "No navmesh modifier configs with a Key prefix set.", "OK");
                return;
            }

            var scenes = CollectScenesByPrefix(sceneNamePrefix, requireLoaded: true);
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"No loaded chunk scenes with prefix '{sceneNamePrefix}'. " +
                    "Use 'Open Chunks Additive' first.", "OK");
                return;
            }

            int added = 0, updated = 0, matched = 0;
            try
            {
                for (int i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    EditorUtility.DisplayProgressBar("Applying Navmesh Modifiers",
                        $"{scene.name}  ({i + 1}/{scenes.Count})",
                        (float)i / scenes.Count);

                    bool sceneDirty = false;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
                        {
                            var go = t.gameObject;
                            var name = go.name;
                            foreach (var cfg in validConfigs)
                            {
                                if (!name.StartsWith(cfg.keyPrefix)) continue;
                                matched++;
                                var mod = go.GetComponent<RecastNavmeshModifier>();
                                if (mod == null)
                                {
                                    mod = go.AddComponent<RecastNavmeshModifier>();
                                    added++;
                                }
                                else
                                {
                                    updated++;
                                }
                                mod.mode           = cfg.mode;
                                mod.surfaceID      = cfg.surfaceID;
                                mod.geometrySource = cfg.geometrySource;
                                mod.includeInScan  = cfg.includeInScan;
                                mod.dynamic        = cfg.dynamic;
                                mod.solid          = cfg.solid;
                                sceneDirty = true;
                                break;
                            }
                        }
                    }

                    if (sceneDirty) EditorSceneManager.MarkSceneDirty(scene);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[ChunkManager] RecastNavmeshModifier: {added} added, {updated} updated " +
                      $"({matched} matched) across {scenes.Count} chunk scene(s); " +
                      $"{validConfigs.Count} config(s) used. Scenes marked dirty — save them to persist.");
        }
    }
}
