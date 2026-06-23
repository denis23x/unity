// Assets/Editor/ChunkManager/ChunkManager.cs
//
// Editor window that wraps the Blender-side chunk export pipeline:
//   * Import — batch-imports XX_YY.fbx chunks (exported with
//     EXPORT_CENTERED=True, axis_forward='-Z', axis_up='Y') into individual
//     Unity scenes, positioning each scene's root so the grid centers on
//     world origin.
//   * Delete — removes the chunk scenes from disk by name prefix.
//   * Open / Unload / Remove — manipulates the same chunk scenes in the
//     current hierarchy at edit time.
//   * Create Navmesh Prefab / Delete Tiles — adds A* NavmeshPrefab components
//     to inst objects in every currently open chunk scene and bakes per-chunk
//     recast tiles into Assets/{Tiles dest}/*.bytes; Delete Tiles nukes that
//     folder. Navmesh creation is decoupled from Import so it can be re-run
//     after the parent scene's RecastGraph settings change without
//     re-importing FBX.
//   * Apply Navmesh Modifiers — recursively walks every open chunk scene and
//     attaches RecastNavmeshModifier components to GameObjects whose name
//     matches one of the user-defined key-prefix presets. Decoupled from
//     Import for the same reason as the navmesh prefab step: settings can be
//     iterated on without re-importing FBX.
//   * Apply Tags / Apply Layers — same key-prefix matching as Apply Navmesh
//     Modifiers, but assigns a project-defined Tag or Layer to each matched
//     GameObject. Tag/Layer values are picked from the project's existing
//     entries via TagField / LayerField, so this step never creates new
//     tags or layers.
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
// chunkSize is set in this window's UI. Its default literal must be kept in
// sync with ChunkStream.DefaultChunkSize by hand — the two scripts intentionally
// don't reference each other so the importer compiles standalone.
//
// UI: UI Toolkit. Layout lives in ChunkManager.uxml, styles in ChunkManager.uss
// (both loaded at runtime via MonoScript-relative paths, so the folder can be
// dropped anywhere under Assets/ and the window finds its own resources).
//
// Class is `partial`; pipeline implementations live next to this file:
//   * ChunkManager.Scenes.cs       — open/unload/remove/delete chunk scenes
//   * ChunkManager.Navmesh.cs      — NavmeshPrefab + RecastNavmeshModifier
//   * ChunkManager.TagsLayers.cs   — Tag / Layer bulk-assign by key prefix
//   * ChunkManager.Import.cs       — FBX scan + per-chunk scene write + mesh bake
//   * ChunkManager.Addressables.cs — Addressables group create/delete
//
// Menu: Tools → Chunks → Chunk Manager

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Pathfinding; // RecastNavmeshModifier enum casts in RebuildConfigs
using Path = System.IO.Path;

namespace ProjectName.EditorTools
{
    public partial class ChunkManager : EditorWindow
    {
        const string PK = "ChunkManager.";

        // Persisted settings
        string sourceFolder;
        string destFolder;
        float  chunkSize;
        bool   addMeshCollider;
        string sceneNamePrefix;
        string addressableGroupName;
        bool   simplifyAddressableNames;
        string tilesDestFolder;
        List<NavmeshModifierConfig> navmeshModifierConfigs;
        List<TagConfig> tagConfigs;
        List<LayerConfig> layerConfigs;

        // UI Toolkit element refs (queried once in CreateGUI)
        VisualElement configsContainer;
        VisualElement tagConfigsContainer, layerConfigsContainer;
        Button btnImport, btnDeleteChunks, btnOpen, btnLoad, btnUnload, btnRemove;
        Button btnCreatePrefab, btnDeleteTiles, btnAddConfig, btnApplyModifiers;
        Button btnAddTagConfig, btnApplyTags, btnAddLayerConfig, btnApplyLayers;
        Button btnCreateAddr, btnDeleteAddr, btnOpenAddrGroups;

        [MenuItem("Tools/Chunks/Chunk Manager")]
        static void Open() => GetWindow<ChunkManager>("Chunk Manager");

        void OnEnable()
        {
            sourceFolder    = EditorPrefs.GetString(PK + nameof(sourceFolder),    "Assets/Chunks");
            destFolder      = EditorPrefs.GetString(PK + nameof(destFolder),      "Assets/Scenes/Chunks");
            // Default must match ChunkStream.DefaultChunkSize — kept in sync by hand.
            chunkSize       = EditorPrefs.GetFloat (PK + nameof(chunkSize),       96f);
            addMeshCollider          = EditorPrefs.GetBool  (PK + nameof(addMeshCollider),          true);
            sceneNamePrefix          = EditorPrefs.GetString(PK + nameof(sceneNamePrefix),          "Chunk_");
            addressableGroupName     = EditorPrefs.GetString(PK + nameof(addressableGroupName),     "Scenes");
            simplifyAddressableNames = EditorPrefs.GetBool  (PK + nameof(simplifyAddressableNames), true);
            // Default "Tiles" matches NavmeshPrefab.SaveToFile's hard-coded
            // Assets/Tiles output path in A* Pathfinding Pro.
            tilesDestFolder          = EditorPrefs.GetString(PK + nameof(tilesDestFolder),          "Tiles");

            // EditorPrefs cannot store List<T> directly; round-trip via JsonUtility
            // through a wrapper that gives the list a named field for the serializer.
            var navmeshConfigsJson = EditorPrefs.GetString(PK + nameof(navmeshModifierConfigs), "");
            if (!string.IsNullOrEmpty(navmeshConfigsJson))
            {
                var wrapper = JsonUtility.FromJson<NavmeshModifierConfigList>(navmeshConfigsJson);
                navmeshModifierConfigs = wrapper?.items ?? new List<NavmeshModifierConfig>();
            }
            else
            {
                navmeshModifierConfigs = new List<NavmeshModifierConfig>();
            }

            var tagConfigsJson = EditorPrefs.GetString(PK + nameof(tagConfigs), "");
            if (!string.IsNullOrEmpty(tagConfigsJson))
            {
                var wrapper = JsonUtility.FromJson<TagConfigList>(tagConfigsJson);
                tagConfigs = wrapper?.items ?? new List<TagConfig>();
            }
            else
            {
                tagConfigs = new List<TagConfig>();
            }

            var layerConfigsJson = EditorPrefs.GetString(PK + nameof(layerConfigs), "");
            if (!string.IsNullOrEmpty(layerConfigsJson))
            {
                var wrapper = JsonUtility.FromJson<LayerConfigList>(layerConfigsJson);
                layerConfigs = wrapper?.items ?? new List<LayerConfig>();
            }
            else
            {
                layerConfigs = new List<LayerConfig>();
            }
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(PK + nameof(sourceFolder),    sourceFolder);
            EditorPrefs.SetString(PK + nameof(destFolder),      destFolder);
            EditorPrefs.SetFloat (PK + nameof(chunkSize),       chunkSize);
            EditorPrefs.SetBool  (PK + nameof(addMeshCollider),          addMeshCollider);
            EditorPrefs.SetString(PK + nameof(sceneNamePrefix),          sceneNamePrefix);
            EditorPrefs.SetString(PK + nameof(addressableGroupName),     addressableGroupName);
            EditorPrefs.SetBool  (PK + nameof(simplifyAddressableNames), simplifyAddressableNames);
            EditorPrefs.SetString(PK + nameof(tilesDestFolder),          tilesDestFolder);
            EditorPrefs.SetString(PK + nameof(navmeshModifierConfigs),
                JsonUtility.ToJson(new NavmeshModifierConfigList { items = navmeshModifierConfigs }));
            EditorPrefs.SetString(PK + nameof(tagConfigs),
                JsonUtility.ToJson(new TagConfigList { items = tagConfigs }));
            EditorPrefs.SetString(PK + nameof(layerConfigs),
                JsonUtility.ToJson(new LayerConfigList { items = layerConfigs }));
        }

        // ── UI Toolkit setup ────────────────────────────────────────────────
        // CreateGUI is the UI Toolkit counterpart to the old IMGUI OnGUI: it
        // runs once when the window opens (and after domain reload), builds
        // the visual tree from the sibling .uxml/.uss, and wires every input
        // to a SavePrefs callback. AssetDatabase.LoadAssetAtPath is fed a path
        // derived from MonoScript.FromScriptableObject so the asset folder
        // can be relocated under Assets/ without code edits.

        void CreateGUI()
        {
            var root = rootVisualElement;

            var script = MonoScript.FromScriptableObject(this);
            var scriptPath = AssetDatabase.GetAssetPath(script);
            var dir = Path.GetDirectoryName(scriptPath).Replace('\\', '/');

            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/ChunkManager.uxml");
            if (visualTree == null)
            {
                root.Add(new Label($"ChunkManager.uxml not found next to ChunkManager.cs in '{dir}'."));
                return;
            }
            visualTree.CloneTree(root);

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>($"{dir}/ChunkManager.uss");
            if (styleSheet != null) root.styleSheets.Add(styleSheet);

            BindFields(root);
            BindButtons(root);
            BindFoldoutPersistence(root);

            RebuildConfigs();
            RebuildTagConfigs();
            RebuildLayerConfigs();
            UpdateButtonStates();
        }

        void BindFields(VisualElement root)
        {
            // Chunk Files
            BindText(root, "source-folder", sourceFolder,
                "Assets-relative folder holding the .fbx chunks exported from Blender. " +
                "Filenames must follow XX_YY.fbx exactly (XX = column index, YY = row index, " +
                "both zero-padded). Subfolders are not scanned.",
                v => { sourceFolder = v; UpdateButtonStates(); });

            BindText(root, "dest-folder", destFolder,
                "Where to write the chunk .unity scenes. The folder is created if missing. " +
                "These scenes are then registered as Addressables and loaded by ChunkStream " +
                "using their filename (without the .unity extension) as the address.",
                v => { destFolder = v; UpdateButtonStates(); });

            BindFloat(root, "chunk-size", chunkSize,
                "Size of one grid cell in meters. MUST match what Blender used at export time " +
                "(chunk_w / chunk_h, derived from bbox / GRID_X in chunks_export.py) AND the " +
                "chunkSize field on ChunkStream in the runtime scene. A mismatch puts the Unity " +
                "grid at the wrong physical positions.",
                v => { chunkSize = v; });

            BindText(root, "scene-prefix", sceneNamePrefix,
                "Prefix for each chunk .unity filename. Final form: <Prefix><XX>_<YY>.unity, " +
                "e.g. 'Chunk_04_07.unity'. Must match the format produced by " +
                "ChunkCoord.ToAddress() in ChunkStream — otherwise the streamer cannot find " +
                "the scenes in Addressables.",
                v => { sceneNamePrefix = v; UpdateButtonStates(); });

            BindToggle(root, "add-mesh-collider", addMeshCollider,
                "Attach a non-convex MeshCollider to every MeshFilter in the chunk after the " +
                "bake step. sharedMesh references the baked mesh, so collision matches the " +
                "visible geometry 1:1 and pivots around the same point as the renderer. " +
                "Convex is not required because chunks are static environment (no Rigidbody).",
                v => { addMeshCollider = v; });

            // Chunk Navmesh Prefabs
            BindText(root, "tiles-dest", tilesDestFolder,
                "Folder name (under Assets/) that stores the per-chunk navmesh tile .bytes " +
                "files. A* Pathfinding Pro's NavmeshPrefab.SaveToFile is hard-coded to write " +
                "into Assets/Tiles, so changing this only affects Delete Tiles below — keep " +
                "it pointing at 'Tiles' unless you've patched the A* source to honor a " +
                "different path.",
                v => { tilesDestFolder = v; UpdateButtonStates(); });

            // Chunk Navmesh Modifiers — the configs list is the source of truth;
            // an empty list means Apply Navmesh Modifiers does nothing, so no
            // extra "enable" toggle is needed.
            configsContainer = root.Q<VisualElement>("configs-container");

            // Chunk Tags and Layers — same pattern as Navmesh Modifiers; the
            // configs lists are the source of truth, empty lists short-circuit
            // the Apply button.
            tagConfigsContainer   = root.Q<VisualElement>("tag-configs-container");
            layerConfigsContainer = root.Q<VisualElement>("layer-configs-container");

            // Chunk Addressables
            BindText(root, "group-name", addressableGroupName,
                "Name of the Addressables group used by Create / Delete below. Created if " +
                "missing on Create; Delete looks the group up by this exact name and removes " +
                "it along with every entry it owns.",
                v => { addressableGroupName = v; UpdateButtonStates(); });

            BindToggle(root, "simplify-names", simplifyAddressableNames,
                "After registering, rewrite every entry's address to the filename without " +
                "extension (e.g. 'Chunk_04_07'). That format matches ChunkCoord.ToAddress() " +
                "in ChunkStream, so the streamer can find the scenes without extra setup. " +
                "Turn off to keep the default GUID-based addresses.",
                v => { simplifyAddressableNames = v; });
        }

        void BindText(VisualElement root, string name, string initial, string tooltip, System.Action<string> setter)
        {
            var f = root.Q<TextField>(name);
            f.value = initial;
            f.tooltip = tooltip;
            f.RegisterValueChangedCallback(evt => { setter(evt.newValue); SavePrefs(); });
        }

        void BindFloat(VisualElement root, string name, float initial, string tooltip, System.Action<float> setter)
        {
            var f = root.Q<FloatField>(name);
            f.value = initial;
            f.tooltip = tooltip;
            f.RegisterValueChangedCallback(evt => { setter(evt.newValue); SavePrefs(); });
        }

        void BindToggle(VisualElement root, string name, bool initial, string tooltip, System.Action<bool> setter)
        {
            var f = root.Q<Toggle>(name);
            f.value = initial;
            f.tooltip = tooltip;
            f.RegisterValueChangedCallback(evt => { setter(evt.newValue); SavePrefs(); });
        }

        void BindButtons(VisualElement root)
        {
            btnImport         = root.Q<Button>("btn-import");
            btnDeleteChunks   = root.Q<Button>("btn-delete-chunks");
            btnOpen           = root.Q<Button>("btn-open");
            btnLoad           = root.Q<Button>("btn-load");
            btnUnload         = root.Q<Button>("btn-unload");
            btnRemove         = root.Q<Button>("btn-remove");
            btnCreatePrefab   = root.Q<Button>("btn-create-prefab");
            btnDeleteTiles    = root.Q<Button>("btn-delete-tiles");
            btnAddConfig      = root.Q<Button>("btn-add-config");
            btnApplyModifiers = root.Q<Button>("btn-apply-modifiers");
            btnAddTagConfig   = root.Q<Button>("btn-add-tag-config");
            btnApplyTags      = root.Q<Button>("btn-apply-tags");
            btnAddLayerConfig = root.Q<Button>("btn-add-layer-config");
            btnApplyLayers    = root.Q<Button>("btn-apply-layers");
            btnCreateAddr     = root.Q<Button>("btn-create-addr");
            btnDeleteAddr     = root.Q<Button>("btn-delete-addr");
            btnOpenAddrGroups = root.Q<Button>("btn-open-addr-groups");

            // delayCall defers the operation to the next editor tick — mirrors
            // the IMGUI version. Important for ImportChunks which switches the
            // active scene mid-loop and for ops that pop modal dialogs.
            btnImport.clicked         += () => EditorApplication.delayCall += ImportChunks;
            btnDeleteChunks.clicked   += () => EditorApplication.delayCall += () => DeleteChunks(destFolder, sceneNamePrefix);
            btnOpen.clicked           += () => EditorApplication.delayCall += () => OpenChunksAdditive(destFolder, sceneNamePrefix);
            btnLoad.clicked           += () => EditorApplication.delayCall += () => LoadChunkScenes(sceneNamePrefix);
            btnUnload.clicked         += () => EditorApplication.delayCall += () => UnloadChunkScenes(sceneNamePrefix);
            btnRemove.clicked         += () => EditorApplication.delayCall += () => RemoveChunkScenes(sceneNamePrefix);
            btnCreatePrefab.clicked   += () => EditorApplication.delayCall += () => CreateNavmeshPrefabs(sceneNamePrefix, chunkSize);
            btnDeleteTiles.clicked    += () => EditorApplication.delayCall += () => DeleteTiles(tilesDestFolder);
            btnApplyModifiers.clicked += () => EditorApplication.delayCall += () => ApplyNavmeshModifiers(sceneNamePrefix, navmeshModifierConfigs);
            btnApplyTags.clicked      += () => EditorApplication.delayCall += () => ApplyTags(sceneNamePrefix, tagConfigs);
            btnApplyLayers.clicked    += () => EditorApplication.delayCall += () => ApplyLayers(sceneNamePrefix, layerConfigs);
            btnCreateAddr.clicked     += () => EditorApplication.delayCall += () =>
                CreateAddressables(destFolder, sceneNamePrefix, addressableGroupName, simplifyAddressableNames);
            btnDeleteAddr.clicked     += () => EditorApplication.delayCall += () => DeleteAddressables(addressableGroupName);
            btnOpenAddrGroups.clicked += OpenAddressableGroupsWindow;

            btnAddConfig.clicked += () =>
            {
                navmeshModifierConfigs.Add(new NavmeshModifierConfig());
                SavePrefs();
                RebuildConfigs();
                UpdateButtonStates();
            };

            btnAddTagConfig.clicked += () =>
            {
                tagConfigs.Add(new TagConfig());
                SavePrefs();
                RebuildTagConfigs();
                UpdateButtonStates();
            };

            btnAddLayerConfig.clicked += () =>
            {
                layerConfigs.Add(new LayerConfig());
                SavePrefs();
                RebuildLayerConfigs();
                UpdateButtonStates();
            };
        }

        // Each top-level Foldout in the UXML carries name="<key>-foldout"; its
        // open/closed state round-trips through EditorPrefs so re-opening the
        // window restores the user's layout instead of forcing every section
        // back to expanded.
        void BindFoldoutPersistence(VisualElement root)
        {
            foreach (var fold in root.Query<Foldout>().Build())
            {
                if (string.IsNullOrEmpty(fold.name)) continue;
                if (!fold.name.EndsWith("-foldout")) continue;

                string prefKey = PK + "fold." + fold.name;
                fold.value = EditorPrefs.GetBool(prefKey, true);
                fold.RegisterValueChangedCallback(evt => EditorPrefs.SetBool(prefKey, evt.newValue));
            }
        }

        // ── Navmesh modifier config list ───────────────────────────────────
        // Built imperatively (not from UXML) because the row count is dynamic
        // and each row carries Add/Remove handlers that capture the config's
        // list index. RebuildConfigs is called whenever the list size changes;
        // existing rows are replaced wholesale because the index closures are
        // cheaper to recreate than to retarget.

        void RebuildConfigs()
        {
            configsContainer.Clear();

            for (int i = 0; i < navmeshModifierConfigs.Count; i++)
            {
                int idx = i;
                var cfg = navmeshModifierConfigs[i];

                var card = new VisualElement();
                card.AddToClassList("config-card");

                var row = new VisualElement();
                row.AddToClassList("config-card-row");

                var fold = new Foldout
                {
                    text  = string.IsNullOrEmpty(cfg.keyPrefix) ? $"Config {i + 1}" : cfg.keyPrefix,
                    value = cfg.foldout,
                };
                fold.RegisterValueChangedCallback(evt => cfg.foldout = evt.newValue);

                var removeBtn = new Button(() =>
                {
                    navmeshModifierConfigs.RemoveAt(idx);
                    SavePrefs();
                    RebuildConfigs();
                    UpdateButtonStates();
                }) { text = "Remove" };
                removeBtn.AddToClassList("config-remove-button");

                row.Add(fold);
                row.Add(removeBtn);
                card.Add(row);

                var keyField = new TextField("Key prefix")
                {
                    value   = cfg.keyPrefix,
                    tooltip = "Name prefix used to match GameObjects inside open chunk scenes. " +
                              "Any object whose name starts with this string gets a RecastNavmeshModifier."
                };
                keyField.RegisterValueChangedCallback(evt =>
                {
                    cfg.keyPrefix = evt.newValue;
                    fold.text = string.IsNullOrEmpty(evt.newValue) ? $"Config {idx + 1}" : evt.newValue;
                    SavePrefs();
                });
                fold.Add(keyField);

                var modeField = new EnumField("Mode", cfg.mode)
                {
                    tooltip = "Surface rasterization mode. WalkableSurface = standard ground; " +
                              "UnwalkableSurface = blocks the scan; WalkableSurfaceWithSeam / WithTag " +
                              "use Surface ID to split or tag the resulting area."
                };
                modeField.RegisterValueChangedCallback(evt =>
                {
                    cfg.mode = (RecastNavmeshModifier.Mode)evt.newValue;
                    SavePrefs();
                });
                fold.Add(modeField);

                var surfaceField = new IntegerField("Surface ID")
                {
                    value   = cfg.surfaceID,
                    tooltip = "Voxel area for the mesh. Only meaningful with WalkableSurfaceWithSeam and " +
                              "WalkableSurfaceWithTag — ignored by the other modes."
                };
                surfaceField.RegisterValueChangedCallback(evt =>
                {
                    cfg.surfaceID = evt.newValue;
                    SavePrefs();
                });
                fold.Add(surfaceField);

                var geomField = new EnumField("Geometry source", cfg.geometrySource)
                {
                    tooltip = "Where the recast scan reads geometry from. Auto picks MeshFilter or " +
                              "Collider depending on what the GameObject has."
                };
                geomField.RegisterValueChangedCallback(evt =>
                {
                    cfg.geometrySource = (RecastNavmeshModifier.GeometrySource)evt.newValue;
                    SavePrefs();
                });
                fold.Add(geomField);

                var inclField = new EnumField("Include in scan", cfg.includeInScan)
                {
                    tooltip = "Whether the object is included in the recast scan. Auto follows the graph's " +
                              "default include rules; AlwaysInclude / AlwaysExclude override them."
                };
                inclField.RegisterValueChangedCallback(evt =>
                {
                    cfg.includeInScan = (RecastNavmeshModifier.ScanInclusion)evt.newValue;
                    SavePrefs();
                });
                fold.Add(inclField);

                var dynamicField = new Toggle("Dynamic")
                {
                    value   = cfg.dynamic,
                    tooltip = "Enable if the object will move at runtime. Chunk geometry is static, so leave " +
                              "off unless this specific object is animated or moved."
                };
                dynamicField.RegisterValueChangedCallback(evt =>
                {
                    cfg.dynamic = evt.newValue;
                    SavePrefs();
                });
                fold.Add(dynamicField);

                var solidField = new Toggle("Solid")
                {
                    value   = cfg.solid,
                    tooltip = "If on, the mesh is treated as solid and its interior becomes unwalkable. " +
                              "Useful for one-sided shells where the inside should also block agents."
                };
                solidField.RegisterValueChangedCallback(evt =>
                {
                    cfg.solid = evt.newValue;
                    SavePrefs();
                });
                fold.Add(solidField);

                configsContainer.Add(card);
            }
        }

        // Tag / Layer config rows follow the same pattern as RebuildConfigs:
        // a card with a foldout header (showing the key prefix), a Remove
        // button, a key-prefix TextField, and the value picker (TagField or
        // LayerField — both populate from the project's defined tags/layers,
        // so the user can never type a non-existent value).

        void RebuildTagConfigs()
        {
            tagConfigsContainer.Clear();

            for (int i = 0; i < tagConfigs.Count; i++)
            {
                int idx = i;
                var cfg = tagConfigs[i];

                var card = new VisualElement();
                card.AddToClassList("config-card");

                var row = new VisualElement();
                row.AddToClassList("config-card-row");

                var fold = new Foldout
                {
                    text  = string.IsNullOrEmpty(cfg.keyPrefix) ? $"Config {i + 1}" : cfg.keyPrefix,
                    value = cfg.foldout,
                };
                fold.RegisterValueChangedCallback(evt => cfg.foldout = evt.newValue);

                var removeBtn = new Button(() =>
                {
                    tagConfigs.RemoveAt(idx);
                    SavePrefs();
                    RebuildTagConfigs();
                    UpdateButtonStates();
                }) { text = "Remove" };
                removeBtn.AddToClassList("config-remove-button");

                row.Add(fold);
                row.Add(removeBtn);
                card.Add(row);

                var keyField = new TextField("Key prefix")
                {
                    value   = cfg.keyPrefix,
                    tooltip = "Name prefix used to match GameObjects inside open chunk scenes. " +
                              "Any object whose name starts with this string gets its tag set."
                };
                keyField.RegisterValueChangedCallback(evt =>
                {
                    cfg.keyPrefix = evt.newValue;
                    fold.text = string.IsNullOrEmpty(evt.newValue) ? $"Config {idx + 1}" : evt.newValue;
                    SavePrefs();
                });
                fold.Add(keyField);

                var tagField = new TagField("Tag", cfg.tag)
                {
                    tooltip = "Tag to assign. Lists only tags already defined in the project " +
                              "(Project Settings → Tags and Layers) — this step never creates new tags."
                };
                tagField.RegisterValueChangedCallback(evt =>
                {
                    cfg.tag = evt.newValue;
                    SavePrefs();
                });
                fold.Add(tagField);

                tagConfigsContainer.Add(card);
            }
        }

        void RebuildLayerConfigs()
        {
            layerConfigsContainer.Clear();

            for (int i = 0; i < layerConfigs.Count; i++)
            {
                int idx = i;
                var cfg = layerConfigs[i];

                var card = new VisualElement();
                card.AddToClassList("config-card");

                var row = new VisualElement();
                row.AddToClassList("config-card-row");

                var fold = new Foldout
                {
                    text  = string.IsNullOrEmpty(cfg.keyPrefix) ? $"Config {i + 1}" : cfg.keyPrefix,
                    value = cfg.foldout,
                };
                fold.RegisterValueChangedCallback(evt => cfg.foldout = evt.newValue);

                var removeBtn = new Button(() =>
                {
                    layerConfigs.RemoveAt(idx);
                    SavePrefs();
                    RebuildLayerConfigs();
                    UpdateButtonStates();
                }) { text = "Remove" };
                removeBtn.AddToClassList("config-remove-button");

                row.Add(fold);
                row.Add(removeBtn);
                card.Add(row);

                var keyField = new TextField("Key prefix")
                {
                    value   = cfg.keyPrefix,
                    tooltip = "Name prefix used to match GameObjects inside open chunk scenes. " +
                              "Any object whose name starts with this string gets its layer set."
                };
                keyField.RegisterValueChangedCallback(evt =>
                {
                    cfg.keyPrefix = evt.newValue;
                    fold.text = string.IsNullOrEmpty(evt.newValue) ? $"Config {idx + 1}" : evt.newValue;
                    SavePrefs();
                });
                fold.Add(keyField);

                var layerField = new LayerField("Layer", cfg.layer)
                {
                    tooltip = "Layer to assign. Lists only layers already defined in the project " +
                              "(Project Settings → Tags and Layers) — this step never creates new layers."
                };
                layerField.RegisterValueChangedCallback(evt =>
                {
                    cfg.layer = evt.newValue;
                    SavePrefs();
                });
                fold.Add(layerField);

                layerConfigsContainer.Add(card);
            }
        }

        // Replaces the IMGUI EditorGUI.DisabledScope guards. Called once at
        // window open and after every input that gates a button — keeps the
        // disabled state in sync with current field values without polling.
        void UpdateButtonStates()
        {
            bool hasSrc    = !string.IsNullOrWhiteSpace(sourceFolder);
            bool hasDst    = !string.IsNullOrWhiteSpace(destFolder);
            bool hasPrefix = !string.IsNullOrWhiteSpace(sceneNamePrefix);
            bool hasTiles  = !string.IsNullOrWhiteSpace(tilesDestFolder);
            bool hasGroup  = !string.IsNullOrWhiteSpace(addressableGroupName);

            btnImport.SetEnabled(hasSrc && hasDst);
            btnDeleteChunks.SetEnabled(hasDst && hasPrefix);
            btnOpen.SetEnabled(hasDst && hasPrefix);
            btnLoad.SetEnabled(hasPrefix);
            btnUnload.SetEnabled(hasPrefix);
            btnRemove.SetEnabled(hasPrefix);
            btnCreatePrefab.SetEnabled(hasPrefix);
            btnDeleteTiles.SetEnabled(hasTiles);
            btnApplyModifiers.SetEnabled(hasPrefix && navmeshModifierConfigs.Count > 0);
            btnApplyTags.SetEnabled(hasPrefix && tagConfigs.Count > 0);
            btnApplyLayers.SetEnabled(hasPrefix && layerConfigs.Count > 0);
            btnCreateAddr.SetEnabled(hasDst && hasPrefix && hasGroup);
            btnDeleteAddr.SetEnabled(hasGroup);
        }
    }
}
