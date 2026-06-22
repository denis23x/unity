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
// Addressables integration:
//   The "Chunk Addressables" panel exposes Create / Delete buttons that manage
//   a single group (name comes from the "Group Name" field, default "Scenes").
//   Create adds every scene matching <Scene prefix>*.unity in Dest folder into
//   that group; with "Simplify Names" enabled each entry's address becomes the
//   filename without extension (i.e. "Chunk_XX_YY"), matching
//   ChunkCoord.ToAddress() in ChunkStream. Delete removes the group along with
//   every entry it owns. Import does NOT touch Addressables — registration is
//   a separate, explicit step.
//
// UI: UI Toolkit. Layout lives in ChunkManager.uxml, styles in ChunkManager.uss
// (both loaded at runtime via MonoScript-relative paths, so the folder can be
// dropped anywhere under Assets/ and the window finds its own resources).
//
// Menu: Tools → Chunks → Chunk Manager

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Pathfinding; // A* Pathfinding Pro — NavmeshPrefab for per-chunk recast bake
// Disambiguates System.IO.Path from Pathfinding.Path (both pulled in by the
// two `using`s above); every bare `Path.` in this file means System.IO.Path.
using Path = System.IO.Path;

namespace ProjectName.EditorTools
{
    public class ChunkManager : EditorWindow
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
        bool   addNavmeshModifier;
        List<NavmeshModifierConfig> navmeshModifierConfigs;

        // UI Toolkit element refs (queried once in CreateGUI)
        VisualElement configsContainer;
        VisualElement modifiersBody;
        Button btnImport, btnDeleteChunks, btnOpen, btnUnload, btnRemove;
        Button btnCreatePrefab, btnDeleteTiles, btnAddConfig, btnApplyModifiers;
        Button btnCreateAddr, btnDeleteAddr;

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
            addNavmeshModifier       = EditorPrefs.GetBool  (PK + nameof(addNavmeshModifier),       false);

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
            EditorPrefs.SetBool  (PK + nameof(addNavmeshModifier),       addNavmeshModifier);
            EditorPrefs.SetString(PK + nameof(navmeshModifierConfigs),
                JsonUtility.ToJson(new NavmeshModifierConfigList { items = navmeshModifierConfigs }));
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
            UpdateModifiersBodyVisibility();
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

            // Chunk Navmesh Modifiers
            BindToggle(root, "add-modifier", addNavmeshModifier,
                "When on, exposes a list of RecastNavmeshModifier configs that 'Apply Navmesh Modifiers' " +
                "below pushes into every currently open chunk scene matching 'Scene prefix'. Each config " +
                "targets GameObjects whose name starts with its Key prefix (search is recursive through " +
                "every root in the scene). Configs are evaluated top-to-bottom; the first matching prefix " +
                "wins per object so a more specific prefix should come before a more generic one.",
                v => { addNavmeshModifier = v; UpdateModifiersBodyVisibility(); });

            modifiersBody    = root.Q<VisualElement>("modifiers-body");
            configsContainer = root.Q<VisualElement>("configs-container");

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
            btnUnload         = root.Q<Button>("btn-unload");
            btnRemove         = root.Q<Button>("btn-remove");
            btnCreatePrefab   = root.Q<Button>("btn-create-prefab");
            btnDeleteTiles    = root.Q<Button>("btn-delete-tiles");
            btnAddConfig      = root.Q<Button>("btn-add-config");
            btnApplyModifiers = root.Q<Button>("btn-apply-modifiers");
            btnCreateAddr     = root.Q<Button>("btn-create-addr");
            btnDeleteAddr     = root.Q<Button>("btn-delete-addr");

            // delayCall defers the operation to the next editor tick — mirrors
            // the IMGUI version. Important for ImportChunks which switches the
            // active scene mid-loop and for ops that pop modal dialogs.
            btnImport.clicked         += () => EditorApplication.delayCall += ImportChunks;
            btnDeleteChunks.clicked   += () => EditorApplication.delayCall += () => DeleteChunks(destFolder, sceneNamePrefix);
            btnOpen.clicked           += () => EditorApplication.delayCall += () => OpenChunksAdditive(destFolder, sceneNamePrefix);
            btnUnload.clicked         += () => EditorApplication.delayCall += () => UnloadChunkScenes(sceneNamePrefix);
            btnRemove.clicked         += () => EditorApplication.delayCall += () => RemoveChunkScenes(sceneNamePrefix);
            btnCreatePrefab.clicked   += () => EditorApplication.delayCall += () => CreateNavmeshPrefabs(sceneNamePrefix, chunkSize);
            btnDeleteTiles.clicked    += () => EditorApplication.delayCall += () => DeleteTiles(tilesDestFolder);
            btnApplyModifiers.clicked += () => EditorApplication.delayCall += () => ApplyNavmeshModifiers(sceneNamePrefix, navmeshModifierConfigs);
            btnCreateAddr.clicked     += () => EditorApplication.delayCall += () =>
                CreateAddressables(destFolder, sceneNamePrefix, addressableGroupName, simplifyAddressableNames);
            btnDeleteAddr.clicked     += () => EditorApplication.delayCall += () => DeleteAddressables(addressableGroupName);

            btnAddConfig.clicked += () =>
            {
                navmeshModifierConfigs.Add(new NavmeshModifierConfig());
                SavePrefs();
                RebuildConfigs();
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

        void UpdateModifiersBodyVisibility()
        {
            if (modifiersBody == null) return;
            modifiersBody.style.display = addNavmeshModifier ? DisplayStyle.Flex : DisplayStyle.None;
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
            btnUnload.SetEnabled(hasPrefix);
            btnRemove.SetEnabled(hasPrefix);
            btnCreatePrefab.SetEnabled(hasPrefix);
            btnDeleteTiles.SetEnabled(hasTiles);
            btnApplyModifiers.SetEnabled(hasPrefix && navmeshModifierConfigs.Count > 0);
            btnCreateAddr.SetEnabled(hasDst && hasPrefix && hasGroup);
            btnDeleteAddr.SetEnabled(hasGroup);
        }

        struct Entry { public int a, b; public string assetPath; }

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

        // ── Reusable scene-file operations ───────────────────────────────
        // Public-ish static helpers so other editor tools can call the same
        // operations the window exposes without instantiating the window.

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

        // ── Import pipeline ──────────────────────────────────────────────
        // Wraps the FBX-scan + per-chunk write into a single entry point. The
        // body below is the original Run() logic verbatim — only the method
        // name has changed.

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

        // ── Addressables ──────────────────────────────────────────────────
        // Create — registers every <prefix>*.unity scene in destFolder into a
        // group with the given name (created if missing). When simplifyNames is
        // true each entry's address is rewritten to the filename without
        // extension — the "Simplify Addressable Names" operation, performed on
        // the whole group so old entries are normalised too. The simplified
        // address matches ChunkCoord.ToAddress() in ChunkStream.
        // Delete — removes the group and all entries it owns in a single
        // RemoveGroup call (Addressables cascades entry removal automatically).

        public static void CreateAddressables(string destFolder, string sceneNamePrefix, string groupName, bool simplifyNames)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[ChunkManager] Skipped Addressables registration: " +
                    "AddressableAssetSettings is null. Open Window → Asset Management → " +
                    "Addressables → Groups once so Unity creates the default settings asset, " +
                    "then try again.");
                return;
            }

            if (!AssetDatabase.IsValidFolder(destFolder))
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"Dest folder not found or not a Unity asset folder:\n{destFolder}", "OK");
                return;
            }

            var scenePaths = Directory.GetFiles(destFolder, $"{sceneNamePrefix}*.unity", SearchOption.TopDirectoryOnly)
                .Select(p => p.Replace('\\', '/'))
                .ToList();

            if (scenePaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"No scenes matching '{sceneNamePrefix}*.unity' in:\n{destFolder}", "OK");
                return;
            }

            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                // Create the group with fresh instances of the same schema types as
                // the default group (typically BundledAssetGroupSchema +
                // ContentUpdateGroupSchema) — that keeps the new group buildable
                // out of the box without sharing schema references with the default.
                var schemaTypes = settings.DefaultGroup != null
                    ? settings.DefaultGroup.Schemas.Select(s => s.GetType()).ToArray()
                    : System.Array.Empty<System.Type>();
                group = settings.CreateGroup(groupName, false, false, true, null, schemaTypes);
            }

            int registered = 0;
            foreach (var path in scenePaths)
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;
                var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                if (entry != null) registered++;
            }

            if (simplifyNames)
            {
                foreach (var entry in group.entries)
                    entry.address = Path.GetFileNameWithoutExtension(entry.AssetPath);
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);

            Debug.Log($"[ChunkManager] Addressables: registered {registered} scenes in group '{groupName}'" +
                      (simplifyNames ? "; addresses simplified to filename." : "."));
        }

        public static void DeleteAddressables(string groupName)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[ChunkManager] Skipped Addressables delete: AddressableAssetSettings is null.");
                return;
            }

            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                EditorUtility.DisplayDialog("Chunk Manager",
                    $"Addressables group '{groupName}' not found.", "OK");
                return;
            }

            int entryCount = group.entries.Count;
            if (!EditorUtility.DisplayDialog("Delete Addressable",
                    $"Delete Addressables group '{groupName}' and its {entryCount} entry(ies)?",
                    "Delete", "Cancel"))
                return;

            // RemoveGroup disposes the group's entries as part of the same call;
            // no need to iterate entries and RemoveAssetEntry one by one.
            settings.RemoveGroup(group);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);

            Debug.Log($"[ChunkManager] Addressables: removed group '{groupName}' ({entryCount} entries).");
        }
    }
}
