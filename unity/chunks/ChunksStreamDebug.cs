#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace Postal.World
{
    /// <summary>
    /// Gizmo-визуализация ChunkStreamer'а: фоновая сетка, кольца загрузки/выгрузки,
    /// маркер игрока, состояния чанков с подписями, HUD-сводка. Полностью
    /// заворачивается в #if UNITY_EDITOR — в standalone-сборку не входит совсем.
    ///
    /// Это partial-файл основного класса ChunkStreamer. Имеет доступ ко всем приватным
    /// полям: _chunks, _lastCenter, _origin, loadRadius и т.д.
    /// </summary>
    public partial class ChunkStreamer
    {
        // ============================================================
        // Конфигурация gizmo (все цвета/тоглы)
        // ============================================================

        [Header("Gizmo")]
        [Tooltip("Все параметры визуализации. Раскрой, чтобы выключить отдельные элементы " +
                 "или подкрасить цвета.")]
        [SerializeField] GizmoSettings gizmos = new GizmoSettings();

        /// <summary>
        /// Все настройки рисования gizmo. Каждый элемент имеет тогл и цвет — можно выключить
        /// то, что мешает, или подкрасить под свою сцену.
        /// </summary>
        [System.Serializable]
        public class GizmoSettings
        {
            [Header("Общее")]
            [Tooltip("Мастер-выключатель. Если выключен — стример не рисует ни одного gizmo.")]
            public bool enabled = true;

            [Tooltip("Y-уровень, на котором рисуются плоские gizmo (обычно уровень земли).")]
            public float yLevel = 0.05f;

            [Header("Фоновая координатная сетка")]
            public bool showGrid = true;
            public Color gridColor = new Color(1f, 1f, 1f, 0.15f);
            [Tooltip("Радиус в чанках, в котором рисуется решётка вокруг игрока.")]
            [Min(1)] public int gridRadius = 5;

            [Header("Кольцо текущего чанка (где стоит игрок)")]
            public bool showCurrentChunkOutline = true;
            public Color currentChunkColor = new Color(1f, 1f, 1f, 0.9f);

            [Header("Кольцо загрузки (loadRadius)")]
            public bool showLoadRing = true;
            [Tooltip("Cyan по умолчанию — контраст с зелёным навмешем.")]
            public Color loadRingColor = new Color(0f, 1f, 1f, 1f);

            [Header("Кольцо выгрузки (unloadRadius)")]
            public bool showUnloadRing = true;
            public Color unloadRingColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            [Header("Предсказательное кольцо")]
            public bool showPredictiveRing = true;
            public Color predictiveRingColor = new Color(0.3f, 0.7f, 1f, 0.8f);

            [Header("Стрелка скорости")]
            public bool showVelocityArrow = true;
            public Color velocityArrowColor = new Color(0.3f, 0.7f, 1f, 0.9f);

            [Header("Маркер игрока")]
            public bool showPlayerMarker = true;
            public Color playerMarkerColor = Color.yellow;
            public Color playerCenterDotColor = Color.red;
            public bool showPlayerLabel = true;
            public Color playerLabelColor = Color.yellow;

            [Header("Заливки чанков по состояниям")]
            public bool showChunkFills = true;
            [Tooltip("LOADED и активно нужен (в текущем кольце loadRadius).")]
            public Color loadedInRingColor = new Color(0.2f, 1f, 0.2f, 0.35f);
            [Tooltip("LOADED по инерции (в гистерезисном буфере, ждёт таймера или возврата игрока).")]
            public Color loadedInBufferColor = new Color(0.6f, 0.7f, 0.3f, 0.20f);
            public Color loadingColor = new Color(0.3f, 0.7f, 1f, 0.40f);
            public Color queuedColor = new Color(1f, 0.9f, 0.2f, 0.35f);
            public Color pendingUnloadColor = new Color(1f, 0.5f, 0.1f, 0.40f);
            public Color availableColor = new Color(0.5f, 0.7f, 0.5f, 0.08f);
            public Color checkingExistenceColor = new Color(0.6f, 0.6f, 0.6f, 0.25f);
            public Color doesNotExistColor = new Color(0.4f, 0.4f, 0.4f, 0.15f);

            [Header("Подписи на чанках")]
            public bool showChunkLabels = true;
            public Color chunkLabelColor = Color.white;

            [Header("HUD-сводка над игроком (показывается при выделении ChunkStreamer)")]
            public bool showHUD = true;
            public Color hudColor = Color.white;
        }

        void OnDrawGizmos()
        {
            if (!gizmos.enabled) return;

            if (!_initialized)
            {
                // Edit-mode: показываем сетку относительно gridOrigin и где будет игрок,
                // чтобы можно было проверить выравнивание до запуска игры.
                if (target != null)
                {
                    Vector3 rel = target.position - gridOrigin;
                    float half = chunkSize * 0.5f;
                    var editCenter = new ChunkCoord(
                        Mathf.FloorToInt((rel.x + half) / chunkSize),
                        Mathf.FloorToInt((rel.z + half) / chunkSize));

                    if (gizmos.showGrid) DrawGrid(gridOrigin, editCenter);
                    if (gizmos.showCurrentChunkOutline)
                        DrawChunkOutline(editCenter, gizmos.currentChunkColor);
                    if (gizmos.showPlayerMarker) DrawPlayerCrosshair(target.position);
                }
                return;
            }

            var center = CurrentChunk();

            if (gizmos.showGrid)
                DrawGrid(ActiveOrigin, center);

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

            var style = new GUIStyle
            {
                fontSize = 11,
                normal = { textColor = gizmos.hudColor },
                alignment = TextAnchor.MiddleCenter
            };
            string hud =
                $"chunk {_lastCenter}  |  loaded {loaded}  |  loading {loading}/{loadBudget}  |  " +
                $"queued {queued}  |  pending {pending}  |  unloading {unloading}  |  " +
                $"available {available}  |  noScene {dne}  |  total {_chunks.Count}";
            UnityEditor.Handles.Label(target.position + Vector3.up * 4f, hud, style);
        }

        // ---- gizmo helpers ----

        void DrawGrid(Vector3 origin, ChunkCoord center)
        {
            Gizmos.color = gizmos.gridColor;
            float r = gizmos.gridRadius * chunkSize;
            // Y игрока, чтобы сетка лежала ровно на земле, не уходила в небо/под пол.
            float y = (target != null ? target.position.y : 0f) - chunkSize * 0f + gizmos.yLevel;
            // Сдвиг по чанкам — сетка ползёт вместе с игроком.
            Vector3 base_ = origin + new Vector3(center.X * chunkSize, 0f, center.Y * chunkSize);
            base_.y = y;

            for (int i = -gizmos.gridRadius; i <= gizmos.gridRadius + 1; i++)
            {
                float off = (i - 0.5f) * chunkSize;
                // Вертикальные линии (вдоль Z).
                Gizmos.DrawLine(base_ + new Vector3(off, 0, -r - chunkSize / 2),
                                base_ + new Vector3(off, 0,  r + chunkSize / 2));
                // Горизонтальные (вдоль X).
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

            // 1) Заливка (если включена).
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

            // 2) Подпись (если включена).
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

                var style = new GUIStyle
                {
                    fontSize = 10,
                    normal = { textColor = gizmos.chunkLabelColor },
                    alignment = TextAnchor.MiddleCenter
                };
                UnityEditor.Handles.Label(center + Vector3.up * 0.3f, label, style);
            }
        }

        void DrawPlayerCrosshair(Vector3 pos)
        {
            pos.y = gizmos.yLevel + 0.5f;
            float r = chunkSize * 0.18f;

            Gizmos.color = gizmos.playerMarkerColor;
            // Ромб
            Gizmos.DrawLine(pos + Vector3.forward * r, pos + Vector3.right * r);
            Gizmos.DrawLine(pos + Vector3.right   * r, pos + Vector3.back  * r);
            Gizmos.DrawLine(pos + Vector3.back    * r, pos + Vector3.left  * r);
            Gizmos.DrawLine(pos + Vector3.left    * r, pos + Vector3.forward * r);
            // Крест через центр
            float cr = r * 1.3f;
            Gizmos.DrawLine(pos + Vector3.left * cr,    pos + Vector3.right * cr);
            Gizmos.DrawLine(pos + Vector3.forward * cr, pos + Vector3.back * cr);

            // Точка-маркер в центре
            Gizmos.color = gizmos.playerCenterDotColor;
            Gizmos.DrawSphere(pos, r * 0.12f);

            if (gizmos.showPlayerLabel)
            {
                var style = new GUIStyle
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = gizmos.playerLabelColor },
                    alignment = TextAnchor.MiddleCenter
                };
                string label = _initialized ? $"PLAYER {_lastCenter}" : "PLAYER";
                UnityEditor.Handles.Label(pos + Vector3.up * 1.5f, label, style);
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
                ChunkState.Unloading         => g.pendingUnloadColor, // визуально близок к pending
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