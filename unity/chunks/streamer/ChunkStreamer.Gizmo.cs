#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace ProjectName.World
{
    /// <summary>
    /// Gizmo visualization for ChunkStreamer: background grid, load/unload rings,
    /// player marker, per-chunk states with labels, HUD summary. Fully wrapped
    /// in #if UNITY_EDITOR — never compiled into standalone builds.
    ///
    /// Partial file of the main ChunkStreamer class. Has access to all private
    /// fields: _chunks, _lastCenter, _origin, loadRadius, etc.
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
            [Header("General")]
            [Tooltip("Master switch. If off — the streamer draws no gizmos at all.")]
            public bool enabled = true;

            [Tooltip("Y level at which flat gizmos are drawn (typically ground level).")]
            public float yLevel = 0.05f;

            [Header("Background coordinate grid")]
            public bool showGrid = true;
            public Color gridColor = new Color(1f, 1f, 1f, 0.15f);
            [Tooltip("Radius in chunks around the player where the grid is drawn.")]
            [Min(1)] public int gridRadius = 5;

            [Header("Current chunk ring (where the player stands)")]
            public bool showCurrentChunkOutline = true;
            public Color currentChunkColor = new Color(1f, 1f, 1f, 0.9f);

            [Header("Load ring (loadRadius)")]
            public bool showLoadRing = true;
            [Tooltip("Cyan by default — contrasts well with the green navmesh.")]
            public Color loadRingColor = new Color(0f, 1f, 1f, 1f);

            [Header("Unload ring (unloadRadius)")]
            public bool showUnloadRing = true;
            public Color unloadRingColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            [Header("Predictive ring")]
            public bool showPredictiveRing = true;
            public Color predictiveRingColor = new Color(0.3f, 0.7f, 1f, 0.8f);

            [Header("Velocity arrow")]
            public bool showVelocityArrow = true;
            public Color velocityArrowColor = new Color(0.3f, 0.7f, 1f, 0.9f);

            [Header("Player marker")]
            public bool showPlayerMarker = true;
            public Color playerMarkerColor = Color.yellow;
            public Color playerCenterDotColor = Color.red;
            public bool showPlayerLabel = true;
            public Color playerLabelColor = Color.yellow;

            [Header("Chunk fills by state")]
            public bool showChunkFills = true;
            [Tooltip("LOADED and actively wanted (inside the current loadRadius ring).")]
            public Color loadedInRingColor = new Color(0.2f, 1f, 0.2f, 0.35f);
            [Tooltip("LOADED by inertia (inside the hysteresis buffer, awaiting timer or the player's return).")]
            public Color loadedInBufferColor = new Color(0.6f, 0.7f, 0.3f, 0.20f);
            public Color loadingColor = new Color(0.3f, 0.7f, 1f, 0.40f);
            public Color queuedColor = new Color(1f, 0.9f, 0.2f, 0.35f);
            public Color pendingUnloadColor = new Color(1f, 0.5f, 0.1f, 0.40f);
            public Color availableColor = new Color(0.5f, 0.7f, 0.5f, 0.08f);
            public Color checkingExistenceColor = new Color(0.6f, 0.6f, 0.6f, 0.25f);
            public Color doesNotExistColor = new Color(0.4f, 0.4f, 0.4f, 0.15f);

            [Header("Chunk labels")]
            public bool showChunkLabels = true;
            public Color chunkLabelColor = Color.white;

            [Header("HUD summary above the player (shown when ChunkStreamer is selected)")]
            public bool showHUD = true;
            public Color hudColor = Color.white;
        }

        // GUIStyle cache — previously these were allocated inside every DrawChunkState/
        // DrawPlayerCrosshair/OnDrawGizmosSelected call, producing GC garbage on the order
        // of N chunks * frame in the Scene View. Colors come from `gizmos` and can change
        // in the inspector, so textColor is rewritten each time (no longer an allocation).
        // Lazy init — to avoid depending on initialization order.
        static GUIStyle _chunkLabelStyle;
        static GUIStyle _playerLabelStyle;
        static GUIStyle _hudStyle;

        void OnDrawGizmos()
        {
            if (!gizmos.enabled) return;

            if (!_initialized)
            {
                // Edit mode: show the grid and the chunk the player would start in,
                // so alignment can be checked before entering Play. CurrentChunk uses
                // ActiveOrigin and is therefore correct in edit mode too.
                if (target != null)
                {
                    var editCenter = CurrentChunk();
                    if (gizmos.showGrid) DrawGrid(editCenter);
                    if (gizmos.showCurrentChunkOutline)
                        DrawChunkOutline(editCenter, gizmos.currentChunkColor);
                    if (gizmos.showPlayerMarker) DrawPlayerCrosshair(target.position);
                }
                return;
            }

            var center = CurrentChunk();

            if (gizmos.showGrid)
                DrawGrid(center);

            if (gizmos.showCurrentChunkOutline)
                DrawChunkOutline(center, gizmos.currentChunkColor);

            if (gizmos.showLoadRing)
                DrawRingOutline(center, loadRadius, gizmos.loadRingColor);

            if (gizmos.showUnloadRing)
                DrawRingOutline(center, unloadRadius, gizmos.unloadRingColor);

            var predicted = _lastPredicted;
            bool predictiveActive = !predicted.Equals(center);
            if (predictiveActive)
            {
                if (gizmos.showPredictiveRing)
                    DrawRingOutline(predicted, loadRadius, gizmos.predictiveRingColor);
                if (gizmos.showVelocityArrow)
                    DrawVelocityArrow(target.position, ChunkWorldCenter(predicted));
            }

            if (gizmos.showChunkFills || gizmos.showChunkLabels)
            {
                foreach (var kv in _chunks)
                    DrawChunkState(kv.Value);
            }

            if (gizmos.showPlayerMarker)
                DrawPlayerCrosshair(target.position);
        }

        void OnDrawGizmosSelected()
        {
            if (!gizmos.enabled || !gizmos.showHUD) return;
            if (!_initialized || target == null) return;

            int loaded = CountByState(ChunkState.Loaded);
            int loading = CountByState(ChunkState.Loading);
            int queued = CountByState(ChunkState.Queued);
            int pending = CountByState(ChunkState.PendingUnload);
            int unloading = CountByState(ChunkState.Unloading);
            int dne = CountByState(ChunkState.DoesNotExist);
            int available = CountByState(ChunkState.Available);

            _hudStyle ??= new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            _hudStyle.normal.textColor = gizmos.hudColor;
            string hud =
                $"chunk {_lastCenter}  |  loaded {loaded}  |  loading {loading}/{loadBudget}  |  " +
                $"queued {queued}  |  pending {pending}  |  unloading {unloading}  |  " +
                $"available {available}  |  noScene {dne}  |  total {_chunks.Count}";
            UnityEditor.Handles.Label(target.position + Vector3.up * 4f, hud, _hudStyle);
        }

        // ---- gizmo helpers ----

        void DrawGrid(ChunkCoord center)
        {
            Gizmos.color = gizmos.gridColor;
            float r = gizmos.gridRadius * chunkSize;
            // Use the player's Y so the grid sits right on the ground and doesn't float
            // into the sky / sink below the floor.
            float y = (target != null ? target.position.y : 0f) + gizmos.yLevel;
            // Base — world center of the player's chunk. ChunkWorldCenter accounts for
            // gridSize, so the grid stays aligned with the importer at any grid size.
            Vector3 base_ = ChunkWorldCenter(center);
            base_.y = y;

            for (int i = -gizmos.gridRadius; i <= gizmos.gridRadius + 1; i++)
            {
                float off = (i - 0.5f) * chunkSize;
                // Vertical lines (along Z).
                Gizmos.DrawLine(base_ + new Vector3(off, 0, -r - chunkSize / 2),
                                base_ + new Vector3(off, 0,  r + chunkSize / 2));
                // Horizontal lines (along X).
                Gizmos.DrawLine(base_ + new Vector3(-r - chunkSize / 2, 0, off),
                                base_ + new Vector3( r + chunkSize / 2, 0, off));
            }
        }

        void DrawChunkOutline(ChunkCoord c, Color color)
        {
            Gizmos.color = color;
            Vector3 center = ChunkWorldCenter(c);
            center.y = gizmos.yLevel + 0.1f;
            Gizmos.DrawWireCube(center, new Vector3(chunkSize, 0.1f, chunkSize));
        }

        void DrawRingOutline(ChunkCoord center, int radius, Color color)
        {
            Gizmos.color = color;
            float w = (radius * 2 + 1) * chunkSize;
            Vector3 c = ChunkWorldCenter(center);
            c.y = gizmos.yLevel + 0.2f;
            Gizmos.DrawWireCube(c, new Vector3(w, 0.2f, w));
        }

        void DrawChunkState(ChunkEntry entry)
        {
            bool inRing = IsDesired(entry.Coord);

            Vector3 center = ChunkWorldCenter(entry.Coord);
            center.y = gizmos.yLevel;

            // 1) Fill (if enabled).
            if (gizmos.showChunkFills)
            {
                Color fill = ColorForState(entry.State, inRing, gizmos);
                if (fill.a > 0.01f)
                {
                    Vector3 size = new Vector3(chunkSize * 0.95f, 0.05f, chunkSize * 0.95f);
                    Vector3 hs = size * 0.5f;
                    Vector3[] quad =
                    {
                        center + new Vector3(-hs.x, 0, -hs.z),
                        center + new Vector3( hs.x, 0, -hs.z),
                        center + new Vector3( hs.x, 0,  hs.z),
                        center + new Vector3(-hs.x, 0,  hs.z),
                    };
                    UnityEditor.Handles.DrawSolidRectangleWithOutline(quad, fill, new Color(0, 0, 0, 0.4f));
                }
            }

            // 2) Label (if enabled).
            if (gizmos.showChunkLabels)
            {
                int dist = entry.Coord.ChebyshevDistance(_lastCenter);
                string distMark = dist == 0 ? "" : $" Δ{dist}";
                string label = entry.Coord.ToString() + distMark;
                label += $"\n{StateShortName(entry.State)}";

                if (entry.State == ChunkState.Loaded)
                    label += inRing ? " ★" : " (buffer)";

                if (entry.State == ChunkState.PendingUnload)
                {
                    float left = entry.UnloadAtTime - Time.unscaledTime;
                    if (left > 0) label += $" {left:F1}s";
                }

                _chunkLabelStyle ??= new GUIStyle { fontSize = 10, alignment = TextAnchor.MiddleCenter };
                _chunkLabelStyle.normal.textColor = gizmos.chunkLabelColor;
                UnityEditor.Handles.Label(center + Vector3.up * 0.3f, label, _chunkLabelStyle);
            }
        }

        void DrawPlayerCrosshair(Vector3 pos)
        {
            pos.y = gizmos.yLevel + 0.5f;
            float r = chunkSize * 0.18f;

            Gizmos.color = gizmos.playerMarkerColor;
            // Diamond
            Gizmos.DrawLine(pos + Vector3.forward * r, pos + Vector3.right * r);
            Gizmos.DrawLine(pos + Vector3.right   * r, pos + Vector3.back  * r);
            Gizmos.DrawLine(pos + Vector3.back    * r, pos + Vector3.left  * r);
            Gizmos.DrawLine(pos + Vector3.left    * r, pos + Vector3.forward * r);
            // Cross through the center
            float cr = r * 1.3f;
            Gizmos.DrawLine(pos + Vector3.left * cr,    pos + Vector3.right * cr);
            Gizmos.DrawLine(pos + Vector3.forward * cr, pos + Vector3.back * cr);

            // Center dot
            Gizmos.color = gizmos.playerCenterDotColor;
            Gizmos.DrawSphere(pos, r * 0.12f);

            if (gizmos.showPlayerLabel)
            {
                _playerLabelStyle ??= new GUIStyle
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
                _playerLabelStyle.normal.textColor = gizmos.playerLabelColor;
                string label = _initialized ? $"PLAYER {_lastCenter}" : "PLAYER";
                UnityEditor.Handles.Label(pos + Vector3.up * 1.5f, label, _playerLabelStyle);
            }
        }

        void DrawVelocityArrow(Vector3 from, Vector3 to)
        {
            from.y = gizmos.yLevel + 0.4f;
            to.y = from.y;
            Gizmos.color = gizmos.velocityArrowColor;
            Gizmos.DrawLine(from, to);
            Vector3 dir = (to - from).normalized;
            if (dir.sqrMagnitude > 0.01f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, dir);
                Vector3 head1 = to - dir * (chunkSize * 0.15f) + right * (chunkSize * 0.08f);
                Vector3 head2 = to - dir * (chunkSize * 0.15f) - right * (chunkSize * 0.08f);
                Gizmos.DrawLine(to, head1);
                Gizmos.DrawLine(to, head2);
            }
        }

        static Color ColorForState(ChunkState s, bool inCurrentLoadRing, GizmoSettings g)
        {
            return s switch
            {
                ChunkState.Loaded            => inCurrentLoadRing ? g.loadedInRingColor : g.loadedInBufferColor,
                ChunkState.Loading           => g.loadingColor,
                ChunkState.Queued            => g.queuedColor,
                ChunkState.PendingUnload     => g.pendingUnloadColor,
                ChunkState.Unloading         => g.pendingUnloadColor, // visually close to pending
                ChunkState.Available         => g.availableColor,
                ChunkState.CheckingExistence => g.checkingExistenceColor,
                ChunkState.DoesNotExist      => g.doesNotExistColor,
                _ => new Color(0, 0, 0, 0),
            };
        }

        static string StateShortName(ChunkState s) => s switch
        {
            ChunkState.Loaded            => "LOADED",
            ChunkState.Loading           => "LOADING…",
            ChunkState.Queued            => "QUEUED",
            ChunkState.PendingUnload     => "PENDING UNLOAD",
            ChunkState.Unloading         => "UNLOADING…",
            ChunkState.Available         => "available",
            ChunkState.CheckingExistence => "checking…",
            ChunkState.DoesNotExist      => "❌ no scene",
            _ => "?",
        };

    }
}
#endif
