// Assets/Editor/ChunkManager/ChunkManager.Import.cs
//
// FBX → per-chunk .unity scene pipeline. ImportChunks scans sourceFolder for
// XX_YY.fbx files, derives the grid bounds, and writes one scene per chunk
// via ImportOne. The mesh-bake half (BakeChunkToIdentity + the matrix-baking
// helper) lives at the bottom — it's how each chunk lands with identity TRS
// in the Inspector despite the Blender→Unity axis compensation that the
// FBX import leaves on the root.
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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

            Debug.Log($"[ChunkManager] DONE. {written}/{entries.Count} scenes written to {destFolder}.");

            EditorUtility.DisplayDialog("Chunk Manager",
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
                Debug.LogError($"[ChunkManager] Could not load FBX asset: {e.assetPath}");
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

            bool ok = EditorSceneManager.SaveScene(scene, scenePath);
            EditorSceneManager.CloseScene(scene, removeScene: true);

            if (!ok) Debug.LogError($"[ChunkManager] Failed to save: {scenePath}");
            else     Debug.Log($"[ChunkManager] {baseName}  →  root @ ({rootPos.x:F1}, {rootPos.y:F1}, {rootPos.z:F1})  [col={col}, row={row}]");

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
                    Debug.LogWarning($"[ChunkManager] {baseName}: source mesh '{source.name}' is not readable; skipping bake for this filter.");
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
    }
}
