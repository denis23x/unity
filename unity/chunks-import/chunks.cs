// Assets/Editor/ChunkImporter.cs
//
// Batch-imports NN_MM.fbx chunks (exported from the Blender split script with
// EXPORT_CENTERED=True, axis_forward='-Z', axis_up='Y') into individual Unity
// scenes, positioning each scene's root so the grid centers on world origin.
//
// Mapping between Blender export and Unity:
//   Blender +X → Unity +X         (column index, first in filename, cx)
//   Blender +Y → Unity +Z         (row index, second in filename, cy)
//   Blender +Z → Unity +Y         (up)
//
// Example for 8×8 grid, 100 m cells (chunk geometry centered in FBX):
//   Chunk_00_00 → root @ (-350, 0, -350)
//   Chunk_07_07 → root @ ( 350, 0,  350)
//
// IMPORTANT: each FBX is reimported with ModelImporter.bakeAxisConversion=true.
// This bakes the Blender Z-up → Y-up rotation into the mesh data, so the
// instantiated FBX has an identity local transform and no compensating rotation
// on the root. Without baking, use_space_transform=True leaves a root rotation
// whose direction depends on Blender's runtime export state and produces a
// 180° horizontal flip of the chunk content in Unity.
//
// Menu: Tools → Chunks → Import FBX → Scenes

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectName.EditorTools
{
    public class ChunkImporter : EditorWindow
    {
        public enum PivotMode
        {
            // FBX geometry centered around (0,0,0). EXPORT_CENTERED=True in Blender script.
            ChunkCenter,
            // FBX origin at the chunk's (-X,-Z) corner.
            ChunkCornerMin,
            // FBX preserves Blender source-world coords; whole grid gets one global shift.
            OriginalSourcePosition,
        }

        public enum AxisLayout { XZ_YUp, XY }

        public enum IndexOrder
        {
            FirstIsCol_X_SecondIsRow_Z, // NN_MM: NN=col along X, MM=row along Z   ← matches the Blender split script
            FirstIsRow_Z_SecondIsCol_X,
        }

        const string PK = "ChunkImporter.";

        string sourceFolder;
        string destFolder;
        float  chunkSize;
        PivotMode pivotMode;
        AxisLayout axis;
        IndexOrder indexOrder;
        bool markStatic;
        bool overwrite;
        bool unpackPrefab;
        string sceneNamePrefix;

        Vector2 scroll;

        [MenuItem("Tools/Chunks/Import FBX → Scenes")]
        static void Open() => GetWindow<ChunkImporter>("Chunk Importer");

        void OnEnable()
        {
            sourceFolder    = EditorPrefs.GetString(PK + nameof(sourceFolder),    "Assets/_Project/Art/Environment/Chunks_FBX");
            destFolder      = EditorPrefs.GetString(PK + nameof(destFolder),      "Assets/_Project/Scenes/Chunks");
            chunkSize       = EditorPrefs.GetFloat (PK + nameof(chunkSize),       100f);
            pivotMode       = (PivotMode) EditorPrefs.GetInt(PK + nameof(pivotMode),  (int)PivotMode.ChunkCenter);
            axis            = (AxisLayout)EditorPrefs.GetInt(PK + nameof(axis),       (int)AxisLayout.XZ_YUp);
            indexOrder      = (IndexOrder)EditorPrefs.GetInt(PK + nameof(indexOrder), (int)IndexOrder.FirstIsCol_X_SecondIsRow_Z);
            markStatic      = EditorPrefs.GetBool(PK + nameof(markStatic),    true);
            overwrite       = EditorPrefs.GetBool(PK + nameof(overwrite),     true);
            unpackPrefab    = EditorPrefs.GetBool(PK + nameof(unpackPrefab),  false);
            sceneNamePrefix = EditorPrefs.GetString(PK + nameof(sceneNamePrefix), "Chunk_");
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(PK + nameof(sourceFolder),    sourceFolder);
            EditorPrefs.SetString(PK + nameof(destFolder),      destFolder);
            EditorPrefs.SetFloat (PK + nameof(chunkSize),       chunkSize);
            EditorPrefs.SetInt   (PK + nameof(pivotMode),       (int)pivotMode);
            EditorPrefs.SetInt   (PK + nameof(axis),            (int)axis);
            EditorPrefs.SetInt   (PK + nameof(indexOrder),      (int)indexOrder);
            EditorPrefs.SetBool  (PK + nameof(markStatic),      markStatic);
            EditorPrefs.SetBool  (PK + nameof(overwrite),       overwrite);
            EditorPrefs.SetBool  (PK + nameof(unpackPrefab),    unpackPrefab);
            EditorPrefs.SetString(PK + nameof(sceneNamePrefix), sceneNamePrefix);
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("FBX chunks → additive scenes", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();

            sourceFolder    = EditorGUILayout.TextField("Source folder",     sourceFolder);
            destFolder      = EditorGUILayout.TextField("Dest folder",       destFolder);
            chunkSize       = EditorGUILayout.FloatField("Chunk size, m",    chunkSize);
            sceneNamePrefix = EditorGUILayout.TextField("Scene name prefix", sceneNamePrefix);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Layout", EditorStyles.boldLabel);
            pivotMode  = (PivotMode) EditorGUILayout.EnumPopup("FBX pivot",          pivotMode);
            axis       = (AxisLayout)EditorGUILayout.EnumPopup("Axis layout",        axis);
            indexOrder = (IndexOrder)EditorGUILayout.EnumPopup("Index order in name", indexOrder);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            markStatic   = EditorGUILayout.Toggle("Mark static",         markStatic);
            unpackPrefab = EditorGUILayout.Toggle("Unpack model prefab", unpackPrefab);
            overwrite    = EditorGUILayout.Toggle("Overwrite existing",  overwrite);

            if (EditorGUI.EndChangeCheck()) SavePrefs();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Filename pattern: NN_MM.fbx (e.g. 00_00.fbx, 07_03.fbx).\n\n" +
                "Matched against the included Blender split script:\n" +
                "  • EXPORT_CENTERED=True  → pivot = ChunkCenter\n" +
                "  • axis_forward='-Z', axis_up='Y' → leave Axis layout = XZ_YUp\n" +
                "  • cx (1st in name) ↦ Unity X column, cy (2nd) ↦ Unity Z row → IndexOrder = FirstIsCol_X_SecondIsRow_Z\n\n" +
                "If after import the city is rotated 90°, swap IndexOrder.",
                MessageType.Info);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(sourceFolder) || string.IsNullOrWhiteSpace(destFolder)))
            {
                if (GUILayout.Button("Scan & import", GUILayout.Height(28)))
                    EditorApplication.delayCall += Run;
            }

            EditorGUILayout.EndScrollView();
        }

        struct Entry { public int a, b; public string assetPath; }

        void Run()
        {
            if (!AssetDatabase.IsValidFolder(sourceFolder))
            {
                EditorUtility.DisplayDialog("Chunk Importer",
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
                EditorUtility.DisplayDialog("Chunk Importer", "Found 0 NN_MM.fbx files in source folder.", "OK");
                return;
            }

            // Deterministic order (a, b ascending) — makes logs and progress readable.
            entries = entries.OrderBy(e => e.a).ThenBy(e => e.b).ToList();

            int minA = entries.Min(e => e.a), maxA = entries.Max(e => e.a);
            int minB = entries.Min(e => e.b), maxB = entries.Max(e => e.b);
            int countA = maxA - minA + 1;
            int countB = maxB - minB + 1;

            Debug.Log($"[ChunkImporter] {entries.Count} FBX files; " +
                      $"grid a:{minA}..{maxA} ({countA}), b:{minB}..{maxB} ({countB}); " +
                      $"cell={chunkSize}m; pivot={pivotMode}; axis={axis}; idxOrder={indexOrder}");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.LogWarning("[ChunkImporter] Aborted by user (unsaved scenes).");
                return;
            }

            var prevActive = EditorSceneManager.GetActiveScene();
            int written = 0;

            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    EditorUtility.DisplayProgressBar("Importing chunks",
                        $"{e.a:00}_{e.b:00}  ({i + 1}/{entries.Count})",
                        (float)i / entries.Count);

                    if (ImportOne(e, minA, minB, countA, countB))
                        written++;
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

            Debug.Log($"[ChunkImporter] DONE. {written}/{entries.Count} scenes written to {destFolder}.");

            EditorUtility.DisplayDialog("Chunk Importer",
                $"Done.\n{written}/{entries.Count} scenes written to:\n{destFolder}", "OK");
        }

        bool ImportOne(Entry e, int minA, int minB, int countA, int countB)
        {
            // Map filename indices (a, b) to (col along U, row along V) on the horizontal plane.
            int col, row;
            if (indexOrder == IndexOrder.FirstIsCol_X_SecondIsRow_Z)
            {
                col = e.a - minA;
                row = e.b - minB;
            }
            else
            {
                row = e.a - minA;
                col = e.b - minB;
            }

            // Chunk center in world space, with the whole grid centered on (0,0,0).
            float u = (col + 0.5f - countA * 0.5f) * chunkSize;
            float v = (row + 0.5f - countB * 0.5f) * chunkSize;

            Vector3 chunkCenterWorld = axis == AxisLayout.XZ_YUp
                ? new Vector3(u, 0f, v)
                : new Vector3(u, v, 0f);

            // Root position depends on FBX pivot.
            Vector3 rootPos;
            switch (pivotMode)
            {
                case PivotMode.ChunkCenter:
                    rootPos = chunkCenterWorld;
                    break;

                case PivotMode.ChunkCornerMin:
                {
                    Vector3 cornerOffset = axis == AxisLayout.XZ_YUp
                        ? new Vector3(-chunkSize * 0.5f, 0f, -chunkSize * 0.5f)
                        : new Vector3(-chunkSize * 0.5f, -chunkSize * 0.5f, 0f);
                    rootPos = chunkCenterWorld + cornerOffset;
                    break;
                }

                case PivotMode.OriginalSourcePosition:
                {
                    Vector3 shift = axis == AxisLayout.XZ_YUp
                        ? new Vector3(-countA * chunkSize * 0.5f, 0f, -countB * chunkSize * 0.5f)
                        : new Vector3(-countA * chunkSize * 0.5f, -countB * chunkSize * 0.5f, 0f);
                    rootPos = shift;
                    break;
                }

                default:
                    rootPos = chunkCenterWorld;
                    break;
            }

            string baseName  = $"{sceneNamePrefix}{e.a:00}_{e.b:00}";
            string scenePath = $"{destFolder}/{baseName}.unity";

            if (File.Exists(scenePath) && !overwrite)
            {
                Debug.Log($"[ChunkImporter] Skip existing: {scenePath}");
                return false;
            }

            // Create scene additively — does not unload currently open scenes.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SetActiveScene(scene);

            // Root GameObject — placed at chunk's world center.
            var root = new GameObject(baseName);
            root.transform.SetPositionAndRotation(rootPos, Quaternion.identity);
            SceneManager.MoveGameObjectToScene(root, scene);

            // Force bakeAxisConversion=true so Unity bakes the Blender Z-up →
            // Y-up rotation into the mesh data instead of leaving a compensating
            // rotation on the FBX root. Without this, the use_space_transform=True
            // export produces a root rotation that lands the chunk content
            // mirrored 180° on the horizontal plane.
            var modelImporter = AssetImporter.GetAtPath(e.assetPath) as ModelImporter;
            if (modelImporter != null && !modelImporter.bakeAxisConversion)
            {
                modelImporter.bakeAxisConversion = true;
                modelImporter.SaveAndReimport();
            }

            // Instantiate FBX as a model-prefab instance (reimports propagate later).
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(e.assetPath);
            if (fbx == null)
            {
                Debug.LogError($"[ChunkImporter] Could not load FBX asset: {e.assetPath}");
                EditorSceneManager.CloseScene(scene, removeScene: true);
                return false;
            }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx, scene);
            inst.transform.SetParent(root.transform, worldPositionStays: false);

            // Only reset localPosition. Do NOT touch localRotation or localScale:
            // when bakeAxisConversion fails to actually bake (which happens with
            // Blender FBX exports that use use_space_transform=True together with
            // bake_space_transform=False), the axis conversion lives on the FBX
            // root rotation rather than in mesh data. Wiping that rotation lays
            // the chunk on its side — for single-mesh FBX files, the root IS the
            // mesh, so the plane ends up edge-on to the camera and invisible.
            inst.transform.localPosition = Vector3.zero;

            if (unpackPrefab)
                PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            if (markStatic)
            {
                // NavigationStatic intentionally omitted — A* Pathfinding Project Pro
                // uses its own Recast baking, not Unity NavMesh.
                var flags = StaticEditorFlags.ContributeGI
                          | StaticEditorFlags.BatchingStatic
                          | StaticEditorFlags.OccluderStatic
                          | StaticEditorFlags.OccludeeStatic
                          | StaticEditorFlags.ReflectionProbeStatic;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
            }

            bool ok = EditorSceneManager.SaveScene(scene, scenePath);
            EditorSceneManager.CloseScene(scene, removeScene: true);

            if (!ok) Debug.LogError($"[ChunkImporter] Failed to save: {scenePath}");
            else     Debug.Log($"[ChunkImporter] {baseName}  →  root @ ({rootPos.x:F1}, {rootPos.y:F1}, {rootPos.z:F1})  [col={col}, row={row}]");

            return ok;
        }
    }
}