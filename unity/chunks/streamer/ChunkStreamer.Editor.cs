// unity/chunks/streamer/ChunkStreamer.Editor.cs
//
// Custom inspector for ChunkStreamer, written in UI Toolkit to match the canonical
// Unity 2022+ pattern used by ChunkManager: layout in ChunkStreamer.uxml, styles
// in ChunkStreamer.uss, fields bound via PropertyField + the parent SerializedObject.
// Foldout open/closed state round-trips through EditorPrefs so section preferences
// survive domain reload and window reopen.
//
// PropertyField auto-binds to the inspector's SerializedObject and respects
// [Tooltip] attributes on the underlying fields — no manual wiring needed.
//
// File lives outside an "Editor" folder; the #if UNITY_EDITOR guard keeps it
// out of the runtime assembly. The sibling .uxml / .uss are loaded relative
// to this script via MonoScript.FromScriptableObject, mirroring ChunkManager.

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Path = System.IO.Path;

namespace ProjectName.World
{
    [CustomEditor(typeof(ChunkStreamer))]
    public class ChunkStreamerEditor : Editor
    {
        const string PK = "ChunkStreamer.";

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            // Locate sibling .uxml / .uss the same way ChunkManager does — via the
            // script's own asset path. Lets the folder be moved under Assets/
            // without touching code.
            var script = MonoScript.FromScriptableObject(this);
            var scriptPath = AssetDatabase.GetAssetPath(script);
            var dir = Path.GetDirectoryName(scriptPath).Replace('\\', '/');

            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/ChunkStreamer.uxml");
            if (visualTree == null)
            {
                root.Add(new Label($"ChunkStreamer.uxml not found next to ChunkStreamer.Editor.cs in '{dir}'."));
                return root;
            }
            visualTree.CloneTree(root);

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>($"{dir}/ChunkStreamer.uss");
            if (styleSheet != null) root.styleSheets.Add(styleSheet);

            // Explicit bind so every PropertyField inside the cloned tree resolves
            // against the current SerializedObject. CreateInspectorGUI binding is
            // implicit in newer Unity versions but calling Bind ourselves keeps it
            // robust across editor versions.
            root.Bind(serializedObject);

            BindFoldoutPersistence(root);

            return root;
        }

        // Each Foldout in the UXML carries name="<key>-foldout"; open/closed state
        // is persisted globally (not per-instance) — section preferences are UI
        // ergonomics, not data attached to a specific component.
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
    }
}
#endif
