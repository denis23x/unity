// Assets/Editor/ChunkManager/ChunkManager.Import.cs
//
// FBX → per-chunk .unity scene pipeline. ImportChunks scans sourceFolder for
// XX_YY.fbx files, derives the grid bounds, and writes one scene per chunk
// via ImportOne.
//
// Per-chunk scene layout:
//   <scenePath>
//     _Geometry  (chunk world centre, identity rotation)
//       <FBX prefab instance>      ← PrefabUtility.InstantiatePrefab, NOT unpacked
//       (anything else the user adds here is preserved across re-imports)
//     _Logic     (chunk world centre, identity rotation)
//                (+ ChunkLifetimeScope + ChunkInstaller for the DI pipeline)
//       (user-owned subtree; Import never touches contents, only ensures the
//        two scope components exist on the root itself)
//
// Ensure-instance semantics:
//   * No scene file on disk → create scene, spawn _Geometry + _Logic, instantiate
//     the FBX under _Geometry, add MeshColliders (if enabled), attach the DI
//     scope components to _Logic, save.
//   * Scene already on disk → open it (additive), make sure _Geometry / _Logic
//     exist, ensure the DI scope components are on _Logic, and add the FBX
//     prefab instance only if no connected instance of that FBX is already
//     under _Geometry. Never delete anything, never reset transforms on existing
//     nodes. Existing FBX instances pick up Blender edits automatically through
//     the prefab → asset link on Unity's FBX reimport.
//
// ModelImporter side: bakeAxisConversion is forced ON so Unity bakes the
// Blender→Unity axis conversion into the mesh data and the FBX root lands
// with identity TRS in the hierarchy. This is the only model-import setting
// we touch; it's an asset-pipeline flag (no scene-side mesh mutation) and it
// does NOT break the PrefabUtility connection that keeps existing chunk
// scenes refreshing when an FBX re-exports.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ProjectName.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Path = System.IO.Path;

namespace ProjectName.EditorTools
{
    public partial class ChunkManager
    {
        struct Entry { public int a, b; public string assetPath; }

        void ImportChunks()
        {
            if (!AssetDatabase.IsValidFolder(sourceFolder))
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"Source folder not found or not a Unity asset folder:\n{sourceFolder}", "OK");
                return;
            }

            if (!AssetDatabase.IsValidFolder(destFolder))
            {
                Directory.CreateDirectory(destFolder);
                AssetDatabase.Refresh();
            }

            var rx = new Regex(@"^(\d+)_(\d+)\.fbx$", RegexOptions.IgnoreCase);
            var entries = new List<Entry>();
            foreach (var f in Directory.GetFiles(sourceFolder, "*.fbx", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                var m = rx.Match(name);
                if (!m.Success) continue;
                entries.Add(new Entry {
                    a = int.Parse(m.Groups[1].Value),
                    b = int.Parse(m.Groups[2].Value),
                    assetPath = f.Replace('\\', '/'),
                });
            }

            if (entries.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager", "Found 0 XX_YY.fbx files in source folder.", "OK");
                return;
            }

            // Deterministic order (a, b ascending) — makes logs and progress readable.
            entries = entries.OrderBy(e => e.a).ThenBy(e => e.b).ToList();

            int minA = entries.Min(e => e.a), maxA = entries.Max(e => e.a);
            int minB = entries.Min(e => e.b), maxB = entries.Max(e => e.b);
            int countA = maxA - minA + 1;
            int countB = maxB - minB + 1;

            Debug.Log($"[ChunkManager] {entries.Count} FBX files; " +
                      $"grid a:{minA}..{maxA} ({countA}), b:{minB}..{maxB} ({countB}); cell={chunkSize}m");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.LogWarning("[ChunkManager] Aborted by user (unsaved scenes).");
                return;
            }

            var prevActive = EditorSceneManager.GetActiveScene();
            int written = 0, addedInstances = 0;

            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    EditorUtility.DisplayProgressBar("Importing chunks",
                        $"{e.a:00}_{e.b:00}  ({i + 1}/{entries.Count})",
                        (float)i / entries.Count);

                    var result = ImportOne(e, minA, minB, countA, countB);
                    if (result.saved)         written++;
                    if (result.spawnedNewFbx) addedInstances++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (prevActive.IsValid() && prevActive.isLoaded)
                    EditorSceneManager.SetActiveScene(prevActive);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ChunkManager] DONE. {written}/{entries.Count} scenes saved, " +
                      $"{addedInstances} new FBX prefab instance(s) added. Destination: {destFolder}.");

            EditorUtility.DisplayDialog("Chunk Manager",
                $"Done.\n{written}/{entries.Count} scenes saved\n" +
                $"{addedInstances} new FBX prefab instance(s) added\n\n{destFolder}",
                "OK");
        }

        struct ImportResult { public bool saved; public bool spawnedNewFbx; }

        ImportResult ImportOne(Entry e, int minA, int minB, int countA, int countB)
        {
            // XX = col along Unity X, YY = row along Unity Z (matches the Blender split script).
            int col = e.a - minA;
            int row = e.b - minB;

            // Chunk center in world space, with the whole grid centered on (0,0,0).
            float u = (col + 0.5f - countA * 0.5f) * chunkSize;
            float v = (row + 0.5f - countB * 0.5f) * chunkSize;
            Vector3 rootPos = new Vector3(u, 0f, v);

            string baseName  = $"{sceneNamePrefix}{e.a:00}_{e.b:00}";
            string scenePath = $"{destFolder}/{baseName}.unity";

            // Resolve the working scene: reuse if already loaded in the hierarchy,
            // open additively if the file exists on disk but isn't loaded, or
            // create a fresh empty scene if there's no file yet. Tracking the
            // pre-existing load state lets us avoid closing scenes the user had
            // open for editing.
            bool fileExists = File.Exists(scenePath);
            Scene scene = default;
            bool wasAlreadyLoaded = false;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.path == scenePath)
                {
                    scene = s;
                    wasAlreadyLoaded = s.isLoaded;
                    if (!s.isLoaded)
                        scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    break;
                }
            }

            if (!scene.IsValid())
            {
                scene = fileExists
                    ? EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive)
                    : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            }

            EditorSceneManager.SetActiveScene(scene);

            // _Geometry and _Logic are created once at chunk centre. On
            // subsequent imports we only look them up — never reposition or
            // recreate — so any manual transform tweaks the user made survive.
            var geometryGO = FindOrCreateRoot(scene, GeometryRootName, rootPos);
            var logicGO    = FindOrCreateRoot(scene, LogicRootName, rootPos);

            // Ensure the DI scope lives on _Logic. AddComponent only fires when
            // the component is missing, so re-imports don't duplicate and
            // inspector-set fields on existing components survive.
            EnsureLogicScopeComponents(logicGO);

            // Force bakeAxisConversion=true so the FBX root lands identity
            // in Unity (Blender's use_space_transform export otherwise leaves
            // a compensating rotation on the root). Cheap: SaveAndReimport is
            // a no-op when the flag is already on, so subsequent imports skip it.
            var modelImporter = AssetImporter.GetAtPath(e.assetPath) as ModelImporter;
            if (modelImporter != null && !modelImporter.bakeAxisConversion)
            {
                modelImporter.bakeAxisConversion = true;
                modelImporter.SaveAndReimport();
            }

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(e.assetPath);
            if (fbx == null)
            {
                Debug.LogError($"[ChunkManager] Could not load FBX asset: {e.assetPath}");
                if (!fileExists && !wasAlreadyLoaded)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
                return new ImportResult { saved = false, spawnedNewFbx = false };
            }

            bool spawnedNewFbx = false;

            // Ensure-instance check: only spawn the FBX prefab when no
            // connected instance of this exact asset is parented under
            // _Geometry. This is what prevents Import from duplicating chunks
            // or stomping on user-applied prefab overrides. Existing instances
            // pick up Blender re-exports automatically through Unity's prefab
            // → FBX reimport — Import doesn't need to do anything for them.
            if (!HasConnectedFbxInstance(geometryGO.transform, e.assetPath))
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx, scene);
                inst.transform.SetParent(geometryGO.transform, worldPositionStays: false);
                inst.transform.localPosition = Vector3.zero;
                spawnedNewFbx = true;

                if (addMeshCollider)
                {
                    // Non-convex MeshCollider tracks the prefab's mesh data via
                    // sharedMesh; if the FBX reimports with new geometry, the
                    // collider's bounds refresh automatically. Added as a
                    // prefab-instance override on each sub-GameObject so the
                    // FBX asset stays untouched.
                    foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(includeInactive: true))
                    {
                        if (mf.sharedMesh == null) continue;
                        if (mf.gameObject.GetComponent<MeshCollider>() != null) continue;
                        var mc = mf.gameObject.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                        mc.convex = false;
                    }
                }
            }

            // The transform / hierarchy edits above always dirty the scene when
            // we spawned a new prefab instance or created either root; an idle
            // re-import of an unchanged scene still calls MarkSceneDirty to
            // make SaveScene a no-op on disk if nothing actually moved.
            EditorSceneManager.MarkSceneDirty(scene);

            bool ok = fileExists
                ? EditorSceneManager.SaveScene(scene)
                : EditorSceneManager.SaveScene(scene, scenePath);

            // Only close scenes we opened ourselves. Scenes that were already
            // loaded before Import ran (e.g. the user had them open for
            // editing) stay loaded.
            if (!wasAlreadyLoaded)
                EditorSceneManager.CloseScene(scene, removeScene: true);

            if (!ok)
            {
                Debug.LogError($"[ChunkManager] Failed to save: {scenePath}");
            }
            else
            {
                string action = !fileExists ? "created" : spawnedNewFbx ? "added FBX instance" : "left as-is";
                Debug.Log($"[ChunkManager] {baseName}  →  {action}  @ ({rootPos.x:F1}, {rootPos.y:F1}, {rootPos.z:F1})  [col={col}, row={row}]");
            }

            return new ImportResult { saved = ok, spawnedNewFbx = spawnedNewFbx };
        }

        // Walks the immediate children of `parent` and returns true if any of
        // them is the root of a connected prefab instance whose source asset
        // is the FBX at `fbxAssetPath`. Checking only the prefab-instance root
        // avoids matching nested children of the FBX, which would also report
        // the same source asset.
        static bool HasConnectedFbxInstance(Transform parent, string fbxAssetPath)
        {
            foreach (Transform child in parent)
            {
                var go = child.gameObject;
                if (!PrefabUtility.IsPartOfPrefabInstance(go)) continue;
                if (PrefabUtility.GetNearestPrefabInstanceRoot(go) != go) continue;
                var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (src == null) continue;
                if (AssetDatabase.GetAssetPath(src) == fbxAssetPath) return true;
            }
            return false;
        }

        // Ensures the DI runtime components (ChunkLifetimeScope + ChunkInstaller)
        // sit on the _Logic root. Idempotent — never removes, never resets fields.
        // Consumers who don't use the DI pipeline can strip these two checks;
        // it's a soft feature.
        static void EnsureLogicScopeComponents(GameObject logicGO)
        {
            if (logicGO.GetComponent<ChunkLifetimeScope>() == null)
                logicGO.AddComponent<ChunkLifetimeScope>();
            if (logicGO.GetComponent<ChunkInstaller>() == null)
                logicGO.AddComponent<ChunkInstaller>();
        }

        // Looks up a top-level GameObject named `name` in `scene`; creates it
        // at `position` (identity rotation) if missing. Position is only used
        // on creation — existing roots keep whatever transform the user (or a
        // previous Import) gave them.
        static GameObject FindOrCreateRoot(Scene scene, string name, Vector3 position)
        {
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == name) return go;

            var created = new GameObject(name);
            SceneManager.MoveGameObjectToScene(created, scene);
            created.transform.SetPositionAndRotation(position, Quaternion.identity);
            return created;
        }
    }
}
