// Assets/Editor/ChunkManager/ChunkManager.Addressables.cs
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

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Path = System.IO.Path;

namespace ProjectName.EditorTools
{
    public partial class ChunkManager
    {
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

        // Opens Unity's built-in Addressables Groups window. ExecuteMenuItem is
        // used instead of AddressableAssetsWindow.Init() because the latter
        // is internal in some Addressables versions; the menu path has been
        // stable across all versions that ship with our supported Unity range.
        public static void OpenAddressableGroupsWindow()
        {
            if (!EditorApplication.ExecuteMenuItem("Window/Asset Management/Addressables/Groups"))
            {
                Debug.LogWarning("[ChunkManager] Could not open Addressables Groups window: " +
                    "menu item 'Window/Asset Management/Addressables/Groups' not found. " +
                    "Is the Addressables package installed?");
            }
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
