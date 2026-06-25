#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace ProjectName.World
{
    /// <summary>
    /// Gizmo visualization for ChunkStreamer: load/unload rings, player marker,
    /// per-chunk states with labels. Fully wrapped in #if UNITY_EDITOR —
    /// never compiled into standalone builds.
    ///
    /// Partial file of the main ChunkStreamer class. Has access to all private
    /// fields: _chunks, _lastCenter, loadRadius, etc.
    /// </summary>
    public partial class ChunkStreamer
    {
        // ============================================================
        // Gizmo configuration (all colors / toggles)
        // ============================================================

        [Header("Gizmo")]
        [Tooltip("All visualization parameters. Expand to disable individual elements " +
                 "or recolor them.")]
        [SerializeField] GizmoSettings gizmos = new GizmoSettings();

        /// <summary>
        /// All gizmo drawing settings. Each element has a toggle and a color — disable
        /// what gets in the way, or recolor to fit the scene.
        /// </summary>
        [System.Serializable]
        public class GizmoSettings
        {
            [Header("Load ring (loadRadius)")]
            public bool showLoadRing = true;
            public Color loadRingColor = new Color(0f, 1f, 0f, 0.20f);

            [Header("Unload ring (unloadRadius)")]
            public bool showUnloadRing = true;
            public Color unloadRingColor = new Color(1f, 0f, 0f, 0.15f);

            [Header("Predictive ring")]
            public bool showPredictiveRing = true;
            public Color predictiveRingColor = new Color(0.3f, 0.7f, 1f, 0.20f);

            [Header("Player marker")]
            public bool showPlayerMarker = true;

            [Header("Chunk labels")]
            public bool showChunkLabels = true;
        }

        // GUIStyle cache — previously these were allocated inside every DrawChunkState/
        // DrawPlayerMarker call, producing GC garbage on the order of N chunks * frame
        // in the Scene View. Colors come from `gizmos` and can change in the inspector,
        // so textColor is rewritten each time (no longer an allocation).
        // Lazy init — to avoid depending on initialization order.
        static GUIStyle _chunkLabelStyle;
        static GUIStyle _playerLabelStyle;

        void OnDrawGizmos()
        {
            if (!_initialized)
            {
                // Edit mode: show the chunk the player would start in,
                // so alignment can be checked before entering Play.
                if (target != null && gizmos.showPlayerMarker)
                {
                    DrawPlayerMarker(target.position, CurrentChunk());
                }
                return;
            }

            var center = CurrentChunk();

            // Y-stagger: outermost (unload) on the bottom, predictive in the middle,
            // load on top — avoids z-fighting between coplanar semi-transparent discs.
            if (gizmos.showUnloadRing)
                DrawRing(center, unloadRadius, gizmos.unloadRingColor, 0.20f);

            var predicted = _lastPredicted;
            bool predictiveActive = !predicted.Equals(center);
            if (predictiveActive && gizmos.showPredictiveRing)
            {
                DrawRing(predicted, loadRadius, gizmos.predictiveRingColor, 0.25f);
                DrawVelocityArrow(target.position, ChunkWorldCenter(predicted), gizmos.predictiveRingColor);
            }

            if (gizmos.showLoadRing)
                DrawRing(center, loadRadius, gizmos.loadRingColor, 0.30f);

            if (gizmos.showChunkLabels)
            {
                foreach (var kv in _chunks)
                    DrawChunkState(kv.Value);
            }

            if (gizmos.showPlayerMarker)
                DrawPlayerMarker(target.position, center);
        }

        // ---- gizmo helpers ----

        void DrawRing(ChunkCoord center, int radius, Color color, float yOffset)
        {
            Vector3 c = ChunkWorldCenter(center);
            c.y = yOffset;
            // The actual load area is a Chebyshev square (2r+1)×(2r+1) chunks. We inscribe
            // a disc in that square — it sits entirely inside the load area, so anything
            // visually under the disc is definitely streamed. The corners outside the disc
            // are also streamed (load is Chebyshev) — that's a visual simplification.
            float discRadius = (radius * 2 + 1) * chunkSize * 0.5f;
            UnityEditor.Handles.color = color;
            UnityEditor.Handles.DrawSolidDisc(c, Vector3.up, discRadius);
        }

        void DrawChunkState(ChunkEntry entry)
        {
            Vector3 center = ChunkWorldCenter(entry.Coord);

            _chunkLabelStyle ??= new GUIStyle
            {
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _chunkLabelStyle.normal.textColor = Color.black;
            _chunkLabelStyle.normal.background = EditorGUIUtility.whiteTexture;

            string label = $"{entry.Coord}\n{StateShortName(entry.State)}";
            if (entry.State == ChunkState.PendingUnload)
            {
                float left = entry.UnloadAtTime - Time.unscaledTime;
                if (left > 0) label += $"\n{left:F1}s";
            }
            UnityEditor.Handles.Label(center + Vector3.up * 0.3f, label, _chunkLabelStyle);
        }

        void DrawPlayerMarker(Vector3 pos, ChunkCoord coord)
        {
            pos.y = 0.5f;

            _playerLabelStyle ??= new GUIStyle
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _playerLabelStyle.normal.textColor = Color.black;
            // EditorGUIUtility.whiteTexture is a built-in 1×1 white texture, no allocation.
            _playerLabelStyle.normal.background = EditorGUIUtility.whiteTexture;

            UnityEditor.Handles.Label(pos + Vector3.up * 1.5f, coord.ToString(), _playerLabelStyle);
        }

        void DrawVelocityArrow(Vector3 from, Vector3 to, Color color)
        {
            from.y = 0.4f;
            to.y = from.y;

            Vector3 dir = (to - from).normalized;
            if (dir.sqrMagnitude < 0.01f) return;

            // The ring's color is intentionally semi-transparent (it's an area hint).
            // The arrow is a directional indicator that must read clearly against the
            // scene geometry — force full alpha while keeping the ring's hue.
            color.a = 1f;
            UnityEditor.Handles.color = color;

            Vector3 right = Vector3.Cross(Vector3.up, dir);
            float headLen = chunkSize * 0.15f;
            float headHalfWidth = chunkSize * 0.08f;
            Vector3 headBase = to - dir * headLen;
            Vector3 head1 = headBase + right * headHalfWidth;
            Vector3 head2 = headBase - right * headHalfWidth;

            // Anti-aliased thick shaft up to the arrowhead base, plus a filled triangle
            // arrowhead — much more readable than thin Gizmos.DrawLine.
            UnityEditor.Handles.DrawAAPolyLine(6f, from, headBase);
            UnityEditor.Handles.DrawAAConvexPolygon(to, head1, head2);
        }

        static string StateShortName(ChunkState s) => s switch
        {
            ChunkState.Loaded            => "LOADED",
            ChunkState.Loading           => "LOADING…",
            ChunkState.Queued            => "QUEUED",
            ChunkState.PendingUnload     => "PENDING\nUNLOAD",
            ChunkState.Unloading         => "UNLOADING…",
            ChunkState.Available         => "AVAILABLE",
            ChunkState.CheckingExistence => "CHECKING…",
            ChunkState.DoesNotExist      => "NO SCENE",
            _ => "?",
        };

    }
}
#endif
