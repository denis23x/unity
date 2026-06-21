// Assets/Editor/ChunkImport.cs
//
// Batch-imports XX_YY.fbx chunks (exported from the Blender split script with
// EXPORT_CENTERED=True, axis_forward='-Z', axis_up='Y') into individual Unity
// scenes, positioning each scene's root so the grid centers on world origin.
//
// Mapping between Blender export and Unity:
//   Blender +X → Unity +X         (column index, first in filename, cx)
//   Blender +Y → Unity +Z         (row index, second in filename, cy)
//   Blender +Z → Unity +Y         (up)
//
// World position math (zero-centered grid, matches ChunkStream.ChunkWorldCenter):
//   u = (col + 0.5 - countA/2) * chunkSize
//   v = (row + 0.5 - countB/2) * chunkSize
// Grid dims (countA × countB) are derived from the FBX file set on disk;
// chunkSize is set in this importer's UI. Its default literal must be kept in
// sync with ChunkStream.DefaultChunkSize by hand — the two scripts intentionally
// don't reference each other so the importer compiles standalone.
//
// Mesh-bake pipeline:
//   Blender's FBX export gives every instantiated chunk a non-identity local
//   transform on the FBX root (typically scale=100 and a -90°X rotation). The
//   transform is what makes the mesh data render in Unity coordinates; wiping
//   it directly lays the chunks on their side. To get clean identity TRS in
//   the Inspector we bake the transform into a fresh copy of each MeshFilter's
//   mesh (vertices, normals, tangents, and winding if the matrix is a
//   reflection), and only THEN reset every Transform in the inst subtree to
//   identity. The baked meshes are NOT written out as separate .asset files —
//   they stay in memory and Unity serialises them inline inside the .unity
//   scene when SaveScene runs. Each chunk has unique geometry so inlining
//   avoids both Project-window clutter and any duplication concern. The
//   original FBX is left untouched and can still be re-imported normally.
//
// Optional Addressables integration:
//   Define the scripting symbol ADDRESSABLES_PRESENT (Project Settings → Player →
//   Other Settings → Scripting Define Symbols) when the com.unity.addressables
//   package is installed. With the define a "Create Addressable" toggle appears;
//   it registers every imported scene into a group named "Scenes" and rewrites
//   each entry's address to the filename without extension (i.e. "Chunk_XX_YY"),
//   matching ChunkCoord.ToAddress() in ChunkStream. Without the define the
//   importer compiles and behaves identically minus that toggle.
//
// Menu: Tools → Chunks → Import FBX → Scenes

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.SceneManagement;
using Pathfinding; // A* Pathfinding Pro — NavmeshPrefab for per-chunk recast bake
// Disambiguates System.IO.Path from Pathfinding.Path (both pulled in by the
// two `using`s above); every bare `Path.` in this file means System.IO.Path.
using Path = System.IO.Path;

namespace ProjectName.EditorTools
{
    public class ChunkImport : EditorWindow
    {
        const string PK = "ChunkImport.";

        string sourceFolder;
        string destFolder;
        float  chunkSize;
        bool overwrite;
        bool addMeshCollider;
        bool createAddressable;
        bool createNavmesh;
        string sceneNamePrefix;

        Vector2 scroll;

        [MenuItem("Tools/Chunks/Import FBX → Scenes")]
        static void Open() => GetWindow<ChunkImport>("Chunk Import");

        void OnEnable()
        {
            sourceFolder    = EditorPrefs.GetString(PK + nameof(sourceFolder),    "Assets/Chunks");
            destFolder      = EditorPrefs.GetString(PK + nameof(destFolder),      "Assets/Scenes/Chunks");
            // Default must match ChunkStream.DefaultChunkSize — kept in sync by hand.
            chunkSize       = EditorPrefs.GetFloat (PK + nameof(chunkSize),       96f);
            overwrite       = EditorPrefs.GetBool  (PK + nameof(overwrite),       true);
            addMeshCollider   = EditorPrefs.GetBool  (PK + nameof(addMeshCollider),   true);
            createAddressable = EditorPrefs.GetBool  (PK + nameof(createAddressable), true);
            createNavmesh     = EditorPrefs.GetBool  (PK + nameof(createNavmesh),     true);
            sceneNamePrefix   = EditorPrefs.GetString(PK + nameof(sceneNamePrefix),   "Chunk_");
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(PK + nameof(sourceFolder),    sourceFolder);
            EditorPrefs.SetString(PK + nameof(destFolder),      destFolder);
            EditorPrefs.SetFloat (PK + nameof(chunkSize),       chunkSize);
            EditorPrefs.SetBool  (PK + nameof(overwrite),       overwrite);
            EditorPrefs.SetBool  (PK + nameof(addMeshCollider),   addMeshCollider);
            EditorPrefs.SetBool  (PK + nameof(createAddressable), createAddressable);
            EditorPrefs.SetBool  (PK + nameof(createNavmesh),     createNavmesh);
            EditorPrefs.SetString(PK + nameof(sceneNamePrefix),   sceneNamePrefix);
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("FBX chunks → additive scenes", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();

            sourceFolder = EditorGUILayout.TextField(
                new GUIContent("Source folder",
                    "Assets-relative folder holding the .fbx chunks exported from Blender. " +
                    "Filenames must follow XX_YY.fbx exactly (XX = column index, YY = row index, " +
                    "both zero-padded). Subfolders are not scanned."),
                sourceFolder);

            destFolder = EditorGUILayout.TextField(
                new GUIContent("Dest folder",
                    "Where to write the chunk .unity scenes. The folder is created if missing. " +
                    "These scenes are then registered as Addressables and loaded by ChunkStream " +
                    "using their filename (without the .unity extension) as the address."),
                destFolder);

            chunkSize = EditorGUILayout.FloatField(
                new GUIContent("Chunk size, m",
                    "Size of one grid cell in meters. MUST match what Blender used at export time " +
                    "(chunk_w / chunk_h, derived from bbox / GRID_X in chunks_export.py) AND the " +
                    "chunkSize field on ChunkStream in the runtime scene. A mismatch puts the Unity " +
                    "grid at the wrong physical positions."),
                chunkSize);

            sceneNamePrefix = EditorGUILayout.TextField(
                new GUIContent("Scene name prefix",
                    "Prefix for each chunk .unity filename. Final form: <Prefix><XX>_<YY>.unity, " +
                    "e.g. 'Chunk_04_07.unity'. Must match the format produced by " +
                    "ChunkCoord.ToAddress() in ChunkStream — otherwise the streamer cannot find " +
                    "the scenes in Addressables."),
                sceneNamePrefix);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            addMeshCollider = EditorGUILayout.Toggle(
                new GUIContent("Add MeshCollider",
                    "Attach a non-convex MeshCollider to every MeshFilter in the chunk after the " +
                    "bake step. sharedMesh references the baked mesh, so collision matches the " +
                    "visible geometry 1:1 and pivots around the same point as the renderer. " +
                    "Convex is not required because chunks are static environment (no Rigidbody)."),
                addMeshCollider);

            createAddressable = EditorGUILayout.Toggle(
                new GUIContent("Create Addressable",
                    "After all chunks are written, register every .unity scene in the destination " +
                    "folder into an Addressables group named 'Scenes' (created if missing) and " +
                    "rewrite each entry's address to the filename without extension — i.e. " +
                    "'Chunk_XX_YY'. That address format is exactly what ChunkCoord.ToAddress() " +
                    "in ChunkStream looks up at runtime, so the streamer finds the scenes without " +
                    "any extra setup. Turn off if you manage Addressables manually."),
                createAddressable);

            createNavmesh = EditorGUILayout.Toggle(
                new GUIContent("Create Navmesh",
                    "Attach a Pathfinding.NavmeshPrefab (A* Pathfinding Pro) to the chunk root, " +
                    "set its bounds to a cube of side = Chunk size centred on the root, and call " +
                    "Scan() so the recast navmesh is baked at import time and serialised inside " +
                    "the .unity scene. Result: every streamed chunk has navigation ready on load " +
                    "without a runtime scan. Requires the A* Pathfinding Pro package — turn off " +
                    "if you bake navigation differently or don't need it per chunk."),
                createNavmesh);

            overwrite = EditorGUILayout.Toggle(
                new GUIContent("Overwrite existing",
                    "Overwrite existing .unity scenes in Dest folder. When off, existing scenes " +
                    "are skipped with a '[ChunkImport] Skip existing' log — useful for re-running " +
                    "the importer after manually editing a few chunks."),
                overwrite);

            if (EditorGUI.EndChangeCheck()) SavePrefs();

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
                EditorUtility.DisplayDialog("Chunk Import",
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
                EditorUtility.DisplayDialog("Chunk Import", "Found 0 XX_YY.fbx files in source folder.", "OK");
                return;
            }

            // Deterministic order (a, b ascending) — makes logs and progress readable.
            entries = entries.OrderBy(e => e.a).ThenBy(e => e.b).ToList();

            int minA = entries.Min(e => e.a), maxA = entries.Max(e => e.a);
            int minB = entries.Min(e => e.b), maxB = entries.Max(e => e.b);
            int countA = maxA - minA + 1;
            int countB = maxB - minB + 1;

            Debug.Log($"[ChunkImport] {entries.Count} FBX files; " +
                      $"grid a:{minA}..{maxA} ({countA}), b:{minB}..{maxB} ({countB}); cell={chunkSize}m");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.LogWarning("[ChunkImport] Aborted by user (unsaved scenes).");
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

            Debug.Log($"[ChunkImport] DONE. {written}/{entries.Count} scenes written to {destFolder}.");

            if (createAddressable)
            {
                // Pick up every chunk scene in dest folder (newly written + previously
                // skipped because of !overwrite) so re-runs always converge to a fully
                // registered group, not just whatever was touched this pass.
                var scenePaths = Directory.GetFiles(destFolder, $"{sceneNamePrefix}*.unity", SearchOption.TopDirectoryOnly)
                    .Select(p => p.Replace('\\', '/'))
                    .ToList();
                if (scenePaths.Count > 0)
                    RegisterScenesAsAddressables(scenePaths);
            }

            EditorUtility.DisplayDialog("Chunk Import",
                $"Done.\n{written}/{entries.Count} scenes written to:\n{destFolder}", "OK");
        }

        bool ImportOne(Entry e, int minA, int minB, int countA, int countB)
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

            if (File.Exists(scenePath) && !overwrite)
            {
                Debug.Log($"[ChunkImport] Skip existing: {scenePath}");
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
            // Also force isReadable=true so the bake step below can read mesh
            // vertex data at edit time.
            var modelImporter = AssetImporter.GetAtPath(e.assetPath) as ModelImporter;
            if (modelImporter != null && (!modelImporter.bakeAxisConversion || !modelImporter.isReadable))
            {
                modelImporter.bakeAxisConversion = true;
                modelImporter.isReadable = true;
                modelImporter.SaveAndReimport();
            }

            // Instantiate FBX as a model-prefab instance (reimports propagate later).
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(e.assetPath);
            if (fbx == null)
            {
                Debug.LogError($"[ChunkImport] Could not load FBX asset: {e.assetPath}");
                EditorSceneManager.CloseScene(scene, removeScene: true);
                return false;
            }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx, scene);
            inst.transform.SetParent(root.transform, worldPositionStays: false);
            inst.transform.localPosition = Vector3.zero;

            // The bake step replaces every MeshFilter's mesh with a fresh copy
            // and then wipes all local TRS to identity. Unpacking is mandatory
            // here because we're swapping out prefab-owned references; leaving
            // the prefab connection would either reject the mesh override or
            // revert it on the next FBX reimport.
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            BakeChunkToIdentity(root, inst, baseName);

            if (addMeshCollider)
            {
                // Non-convex MeshCollider matches the visible geometry 1:1 and
                // works for static environment (no Rigidbody on the chunk).
                // sharedMesh points at the just-baked mesh, so the collision
                // shape lives in the same local space as the renderer.
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(includeInactive: true))
                {
                    if (mf.sharedMesh == null) continue;
                    var mc = mf.gameObject.GetComponent<MeshCollider>();
                    if (mc == null) mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mc.convex = false;
                }
            }

            if (createNavmesh)
            {
                // Attach A* Pathfinding NavmeshPrefab one level below the chunk
                // root — on `inst`, the FBX-instantiated GameObject that owns
                // the geometry. Bounds are a cube of side chunkSize centred on
                // inst's local origin: XZ matches the chunk footprint exactly
                // (so neighbouring chunks tile cleanly), Y is also chunkSize as
                // a generous default for building heights.
                //
                // ScanAndSaveToFile() is what the inspector's "Scan and save"
                // button calls — it bakes the recast navmesh AND writes the
                // result into the serializedNavmesh field on the component.
                // Plain Scan() only returns the data without persisting it, so
                // SaveScene would have nothing to serialise and the loaded
                // scene would carry an empty NavmeshPrefab at runtime.
                var navmesh = inst.AddComponent<NavmeshPrefab>();
                navmesh.bounds = new Bounds(Vector3.zero, new Vector3(chunkSize, chunkSize, chunkSize));
                navmesh.ScanAndSaveToFile();
            }

            bool ok = EditorSceneManager.SaveScene(scene, scenePath);
            EditorSceneManager.CloseScene(scene, removeScene: true);

            if (!ok) Debug.LogError($"[ChunkImport] Failed to save: {scenePath}");
            else     Debug.Log($"[ChunkImport] {baseName}  →  root @ ({rootPos.x:F1}, {rootPos.y:F1}, {rootPos.z:F1})  [col={col}, row={row}]");

            return ok;
        }

        // ── Mesh bake pipeline ──────────────────────────────────────────────
        // The FBX import preserves the Blender→Unity coordinate compensation as
        // a non-identity local transform on the instantiated GameObject. The
        // methods below rewrite each MeshFilter's mesh so the vertex data lives
        // in the chunk root's local space, allowing every Transform under inst
        // to be reset to identity without changing what's rendered.

        void BakeChunkToIdentity(GameObject root, GameObject inst, string baseName)
        {
            var meshFilters = inst.GetComponentsInChildren<MeshFilter>(includeInactive: true);

            // Snapshot each MeshFilter's world position and its world
            // rotation+scale matrix BEFORE mutating any Transform. Splitting
            // the matrix is what restores the rotation pivot: only the
            // rotation+scale part is baked into vertices, the translation
            // goes back onto the GameObject Transform below. If we baked the
            // full matrix instead, every Transform would sit at the chunk
            // root and rotating a single building would swing it around the
            // chunk centre rather than its own pivot.
            var worldPositions = new Vector3[meshFilters.Length];
            var worldRotScales = new Matrix4x4[meshFilters.Length];
            for (int i = 0; i < meshFilters.Length; i++)
            {
                var m = meshFilters[i].transform.localToWorldMatrix;
                worldPositions[i] = new Vector3(m.m03, m.m13, m.m23);
                m.m03 = 0f; m.m13 = 0f; m.m23 = 0f;
                worldRotScales[i] = m;
            }

            // Collapse every Transform under inst to identity so the FBX
            // root's -90°X / scale 100 disappears from the hierarchy.
            foreach (var t in inst.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale    = Vector3.one;
            }

            // Restore each MeshFilter to its original world position and bake
            // the matching rotation+scale into the mesh vertices. The
            // resulting Transform has identity rotation+scale + the mesh's
            // own world position, so rotating it pivots around the mesh
            // itself. GetComponentsInChildren returns components in
            // depth-first order, so a MeshFilter that is also an ancestor
            // of another MeshFilter is placed before its descendants — the
            // child's world position then lands correctly.
            for (int i = 0; i < meshFilters.Length; i++)
            {
                var mf = meshFilters[i];
                var source = mf.sharedMesh;
                if (source == null) continue;

                if (!source.isReadable)
                {
                    Debug.LogWarning($"[ChunkImport] {baseName}: source mesh '{source.name}' is not readable; skipping bake for this filter.");
                    continue;
                }

                mf.transform.position = worldPositions[i];

                var baked = BakeMeshThroughMatrix(source, worldRotScales[i]);
                baked.name = $"{source.name}_baked";
                // No CreateAsset call — leaving the mesh as a scene-owned
                // object makes SaveScene serialise its data inline into the
                // .unity file, so no extra .asset clutter is produced.
                mf.sharedMesh = baked;
            }
        }

        static Mesh BakeMeshThroughMatrix(Mesh source, Matrix4x4 matrix)
        {
            var mesh = new Mesh();
            if (source.vertexCount > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            var vertices = source.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            mesh.vertices = vertices;

            var normals = source.normals;
            if (normals != null && normals.Length > 0)
            {
                // Uniform-scale + rotation: MultiplyVector + normalize gives the
                // right direction. Chunks don't use shear so we don't need the
                // full inverse-transpose.
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = matrix.MultiplyVector(normals[i]).normalized;
                mesh.normals = normals;
            }

            var tangents = source.tangents;
            if (tangents != null && tangents.Length > 0)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    var dir = matrix.MultiplyVector((Vector3)tangents[i]).normalized;
                    tangents[i] = new Vector4(dir.x, dir.y, dir.z, tangents[i].w);
                }
                mesh.tangents = tangents;
            }

            var uv  = source.uv;     if (uv.Length  > 0) mesh.uv  = uv;
            var uv2 = source.uv2;    if (uv2.Length > 0) mesh.uv2 = uv2;
            var uv3 = source.uv3;    if (uv3.Length > 0) mesh.uv3 = uv3;
            var uv4 = source.uv4;    if (uv4.Length > 0) mesh.uv4 = uv4;
            var colors = source.colors; if (colors.Length > 0) mesh.colors = colors;

            // Reflections (negative-determinant matrices, e.g. an axis-swap)
            // invert winding; reverse triangle order so front-face culling
            // still renders the right side. Uniform positive scale + rotation
            // keeps winding intact.
            bool flipWinding = matrix.determinant < 0f;
            mesh.subMeshCount = source.subMeshCount;
            for (int sm = 0; sm < source.subMeshCount; sm++)
            {
                var tris = source.GetTriangles(sm);
                if (flipWinding)
                {
                    for (int i = 0; i < tris.Length; i += 3)
                        (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
                }
                mesh.SetTriangles(tris, sm);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        // ── Addressables registration ──────────────────────────────────────
        // Registers all scene paths into a single Addressables group named
        // "Scenes" and rewrites every entry's address to the filename without
        // extension. That last step is the "Simplify Addressable Names"
        // operation, performed unconditionally on the whole group so old
        // entries (registered before this code existed) are normalised too.
        // The simplified address matches ChunkCoord.ToAddress() — that's the
        // contract ChunkStream relies on for its Addressables lookups.

        const string AddressableGroupName = "Scenes";

        static void RegisterScenesAsAddressables(List<string> scenePaths)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[ChunkImport] Skipped Addressables registration: " +
                    "AddressableAssetSettings is null. Open Window → Asset Management → " +
                    "Addressables → Groups once so Unity creates the default settings asset, " +
                    "then re-run the importer.");
                return;
            }

            var group = settings.FindGroup(AddressableGroupName);
            if (group == null)
            {
                // Create the group with fresh instances of the same schema types as
                // the default group (typically BundledAssetGroupSchema +
                // ContentUpdateGroupSchema) — that keeps the new group buildable
                // out of the box without sharing schema references with the default.
                var schemaTypes = settings.DefaultGroup != null
                    ? settings.DefaultGroup.Schemas.Select(s => s.GetType()).ToArray()
                    : System.Array.Empty<System.Type>();
                group = settings.CreateGroup(AddressableGroupName, false, false, true, null, schemaTypes);
            }

            int registered = 0;
            foreach (var path in scenePaths)
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;
                var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                if (entry != null) registered++;
            }

            // "Simplify Addressable Names" for every entry in the group: address
            // becomes filename-without-extension, which is what ChunkStream uses.
            foreach (var entry in group.entries)
                entry.address = Path.GetFileNameWithoutExtension(entry.AssetPath);

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);

            Debug.Log($"[ChunkImport] Addressables: registered {registered} scenes in group " +
                      $"'{AddressableGroupName}'; addresses simplified to filename.");
        }
    }
}