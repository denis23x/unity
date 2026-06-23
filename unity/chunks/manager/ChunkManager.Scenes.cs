// Assets/Editor/ChunkManager/ChunkManager.Scenes.cs
//
// Reusable scene-file operations: delete chunk .unity files on disk, and
// open/unload/remove chunk scenes in the current edit-time hierarchy. The
// helpers are static so other editor tools can call them without
// instantiating the window. CollectScenesByPrefix is the shared lookup used
// by Unload/Remove here and by the navmesh pipeline in ChunkManager.Navmesh.cs.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectName.EditorTools
{
    public partial class ChunkManager
    {
        public static void DeleteChunks(string destFolder, string sceneNamePrefix)
        {
            if (!AssetDatabase.IsValidFolder(destFolder))
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"Dest folder not found or not a Unity asset folder:\n{destFolder}", "OK");
                return;
            }

            var files = Directory.GetFiles(destFolder, $"{sceneNamePrefix}*.unity", SearchOption.TopDirectoryOnly)
                .Select(p => p.Replace('\\', '/'))
                .ToList();

            if (files.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"No scenes matching '{sceneNamePrefix}*.unity' in:\n{destFolder}", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Delete Chunks",
                    $"Delete {files.Count} scene file(s) from\n{destFolder}?",
                    "Delete", "Cancel"))
                return;

            int deleted = 0;
            foreach (var path in files)
                if (AssetDatabase.DeleteAsset(path)) deleted++;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ChunkManager] Deleted {deleted}/{files.Count} chunk scenes from {destFolder}.");
        }

        public static void OpenChunksAdditive(string destFolder, string sceneNamePrefix)
        {
            if (!AssetDatabase.IsValidFolder(destFolder))
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"Dest folder not found or not a Unity asset folder:\n{destFolder}", "OK");
                return;
            }

            var files = Directory.GetFiles(destFolder, $"{sceneNamePrefix}*.unity", SearchOption.TopDirectoryOnly)
                .Select(p => p.Replace('\\', '/'))
                .OrderBy(p => p)
                .ToList();

            if (files.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"No scenes matching '{sceneNamePrefix}*.unity' in:\n{destFolder}", "OK");
                return;
            }

            // Skip paths already present in the hierarchy so re-clicking the
            // button doesn't double-load anything (Unity would silently no-op
            // but we also avoid resetting their loaded state).
            var openPaths = new HashSet<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                openPaths.Add(SceneManager.GetSceneAt(i).path);

            int opened = 0;
            foreach (var path in files)
            {
                if (openPaths.Contains(path)) continue;
                EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                opened++;
            }

            Debug.Log($"[ChunkManager] Opened {opened}/{files.Count} chunk scenes additively from {destFolder}.");
        }

        public static void LoadChunkScenes(string sceneNamePrefix)
        {
            var targets = CollectScenesByPrefix(sceneNamePrefix, requireLoaded: false)
                .Where(s => !s.isLoaded)
                .ToList();
            if (targets.Count == 0)
            {
                Debug.Log($"[ChunkManager] No unloaded chunk scenes with prefix '{sceneNamePrefix}'.");
                return;
            }

            int loaded = 0;
            foreach (var s in targets)
            {
                if (string.IsNullOrEmpty(s.path)) continue;
                var opened = EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
                if (opened.isLoaded) loaded++;
            }

            Debug.Log($"[ChunkManager] Loaded {loaded}/{targets.Count} chunk scenes (prefix '{sceneNamePrefix}').");
        }

        public static void UnloadChunkScenes(string sceneNamePrefix)
        {
            var targets = CollectScenesByPrefix(sceneNamePrefix, requireLoaded: true);
            if (targets.Count == 0)
            {
                Debug.Log($"[ChunkManager] No loaded chunk scenes with prefix '{sceneNamePrefix}'.");
                return;
            }

            int unloaded = 0;
            foreach (var s in targets)
                if (EditorSceneManager.CloseScene(s, removeScene: false)) unloaded++;

            Debug.Log($"[ChunkManager] Unloaded {unloaded}/{targets.Count} chunk scenes (prefix '{sceneNamePrefix}').");
        }

        public static void RemoveChunkScenes(string sceneNamePrefix)
        {
            var targets = CollectScenesByPrefix(sceneNamePrefix, requireLoaded: false);
            if (targets.Count == 0)
            {
                Debug.Log($"[ChunkManager] No chunk scenes with prefix '{sceneNamePrefix}'.");
                return;
            }

            int removed = 0;
            foreach (var s in targets)
                if (EditorSceneManager.CloseScene(s, removeScene: true)) removed++;

            Debug.Log($"[ChunkManager] Removed {removed}/{targets.Count} chunk scenes (prefix '{sceneNamePrefix}').");
        }

        static List<Scene> CollectScenesByPrefix(string sceneNamePrefix, bool requireLoaded)
        {
            var list = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (string.IsNullOrEmpty(s.name)) continue;
                if (!s.name.StartsWith(sceneNamePrefix)) continue;
                if (requireLoaded && !s.isLoaded) continue;
                list.Add(s);
            }
            return list;
        }
    }
}
