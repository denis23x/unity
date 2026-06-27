// Assets/Editor/ChunkManagerNoNavmesh/ChunkManager.TagsLayers.cs
//
// Bulk-assigns a project-defined Tag or Layer to GameObjects inside every
// currently open chunk scene whose name starts with a per-prefix preset.
// Configs are evaluated in list order, the first matching prefix wins,
// scenes are marked dirty but NOT saved (user owns persistence via
// File → Save). The Tag/Layer values are picked from project-defined
// entries via TagField / LayerField in the UI, so this step never creates
// new tags or layers — only assigns existing ones.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectName.EditorTools
{
    public partial class ChunkManagerNoNavmesh
    {
        [System.Serializable]
        public class TagConfig
        {
            public string keyPrefix = "";
            public string tag = "Untagged";

            // UI-only fold state — kept out of JSON so it doesn't bloat
            // EditorPrefs and so toggling it isn't treated as a config change.
            [System.NonSerialized] public bool foldout = true;
        }

        [System.Serializable]
        class TagConfigList
        {
            public List<TagConfig> items = new List<TagConfig>();
        }

        [System.Serializable]
        public class LayerConfig
        {
            public string keyPrefix = "";
            public int layer = 0;

            [System.NonSerialized] public bool foldout = true;
        }

        [System.Serializable]
        class LayerConfigList
        {
            public List<LayerConfig> items = new List<LayerConfig>();
        }

        public static void ApplyTags(string sceneNamePrefix, List<TagConfig> configs)
        {
            var validConfigs = configs
                .Where(c => !string.IsNullOrWhiteSpace(c.keyPrefix) && !string.IsNullOrWhiteSpace(c.tag))
                .ToList();
            if (validConfigs.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager (No Navmesh)",
                    "No tag configs with a Key prefix and Tag set.", "OK");
                return;
            }

            var scenes = CollectScenesByPrefix(sceneNamePrefix, requireLoaded: true);
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager (No Navmesh)",
                    $"No loaded chunk scenes with prefix '{sceneNamePrefix}'. " +
                    "Use 'Open Chunks Additive' first.", "OK");
                return;
            }

            int updated = 0, matched = 0;
            try
            {
                for (int i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    EditorUtility.DisplayProgressBar("Applying Tags",
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
                                if (go.tag != cfg.tag)
                                {
                                    go.tag = cfg.tag;
                                    updated++;
                                    sceneDirty = true;
                                }
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

            Debug.Log($"[ChunkManagerNoNavmesh] Tags: {updated} updated " +
                      $"({matched} matched) across {scenes.Count} chunk scene(s); " +
                      $"{validConfigs.Count} config(s) used. Scenes marked dirty — save them to persist.");
        }

        public static void ApplyLayers(string sceneNamePrefix, List<LayerConfig> configs)
        {
            var validConfigs = configs
                .Where(c => !string.IsNullOrWhiteSpace(c.keyPrefix))
                .ToList();
            if (validConfigs.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager (No Navmesh)",
                    "No layer configs with a Key prefix set.", "OK");
                return;
            }

            var scenes = CollectScenesByPrefix(sceneNamePrefix, requireLoaded: true);
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager (No Navmesh)",
                    $"No loaded chunk scenes with prefix '{sceneNamePrefix}'. " +
                    "Use 'Open Chunks Additive' first.", "OK");
                return;
            }

            int updated = 0, matched = 0;
            try
            {
                for (int i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    EditorUtility.DisplayProgressBar("Applying Layers",
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
                                if (go.layer != cfg.layer)
                                {
                                    go.layer = cfg.layer;
                                    updated++;
                                    sceneDirty = true;
                                }
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

            Debug.Log($"[ChunkManagerNoNavmesh] Layers: {updated} updated " +
                      $"({matched} matched) across {scenes.Count} chunk scene(s); " +
                      $"{validConfigs.Count} config(s) used. Scenes marked dirty — save them to persist.");
        }
    }
}
