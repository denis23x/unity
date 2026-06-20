using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Postal.World
{
    /// <summary>
    /// Целочисленная координата чанка на сетке. Совпадает с NN/MM-индексом в имени
    /// сцены Chunk_NN_MM.unity, который задаёт ChunkImport при экспорте FBX-чанков.
    /// Перевод координаты в мировую позицию делается через ChunkStream.ChunkWorldCenter —
    /// формула зависит от gridSize и gridOrigin, поэтому здесь её намеренно нет.
    /// Y здесь — имя в координатах чанка, не путать с world Y (высотой).
    /// </summary>
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int X;
        public readonly int Y;

        public ChunkCoord(int x, int y) { X = x; Y = y; }

        /// <summary>Чебышёвское ("шахматное") расстояние — соответствует квадратным кольцам.</summary>
        public int ChebyshevDistance(ChunkCoord o)
            => Mathf.Max(Mathf.Abs(X - o.X), Mathf.Abs(Y - o.Y));

        /// <summary>Квадрат евклидова расстояния. Нужен для приоритета загрузки (ближе = раньше).</summary>
        public int SquaredEuclideanDistance(ChunkCoord o)
        {
            int dx = X - o.X, dy = Y - o.Y;
            return dx * dx + dy * dy;
        }

        public bool Equals(ChunkCoord o) => X == o.X && Y == o.Y;
        public override bool Equals(object o) => o is ChunkCoord c && Equals(c);
        // HashCode.Combine — стандартный mix, без риска overflow на больших координатах
        // (старая версия с X*73856093 переполнялась для X > ~29 тыс, хотя для int wrap-around
        // это не ломало корректность, новая версия аккуратнее).
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
        // Совпадает с именем .unity-сцены от ChunkImport (sceneNamePrefix="Chunk_", :00 padding).
        public string ToAddress() => $"Chunk_{X:00}_{Y:00}";
    }

    /// <summary>
    /// Стример чанков v2.1 — исправления:
    ///  • Origin — центр чанка (0,0), не угол. Игрок стартует в середине стартового чанка.
    ///  • Existence-кеш переживает выгрузку: вводится состояние Available для "знаем что
    ///    есть, но не в памяти". При повторном входе нет лишнего запроса в Addressables.
    ///  • Queued-чанки, которые стали ненужными, отменяются (возвращаются в Available)
    ///    до того, как уйдут в фактическую загрузку. Защита от паразитных IO-операций
    ///    при дёрганом предиктиве.
    ///  • Более информативные gizmo: координатная сетка, crosshair игрока (плавно следит),
    ///    стрелка скорости, цветная заливка по состояниям, таймер на pending-unload,
    ///    координатные подписи.
    /// </summary>
    public partial class ChunkStream : MonoBehaviour
    {
        // ============================================================
        // Канонические дефолты сетки. Менять ЗДЕСЬ — и стример, и ChunkImport
        // подхватят одно значение для свежей установки. Уже сериализованные
        // значения (на компоненте в сцене и в EditorPrefs импортёра) не
        // обновятся автоматически — править их Inspector'ом / в окне импортёра,
        // либо разово сбросить EditorPrefs ключи и Reset на компоненте.
        // ============================================================

        public const float DefaultChunkSize     = 100f;
        public const int   DefaultGridDimension = 8;

        // ============================================================
        // Configuration
        // ============================================================

        [Header("Цель слежения")]
        [SerializeField] Transform target;

        [Header("Якорь сетки")]
        [Tooltip("Мировая позиция, в которой центрируется ВСЯ сетка (как делает ChunkImport). " +
                 "По умолчанию (0,0,0) — сетка центрирована на мировом origin. " +
                 "НЕ меняй во время Play — сместит всю сетку и приведёт к каскаду перезагрузок.")]
        [SerializeField] Vector3 gridOrigin = Vector3.zero;

        [Header("Сетка")]
        [SerializeField, Min(1f)] float chunkSize = DefaultChunkSize;
        [Tooltip("Размер сетки в чанках (X = столбцы, Y = ряды). Должен совпадать с GRID_X / GRID_Y " +
                 "в Blender-скрипте экспорта. Используется и для перевода мировой позиции игрока " +
                 "в Chunk_NN_MM координату, и для отсечки запросов вне границ сетки.")]
        [SerializeField] Vector2Int gridSize = new Vector2Int(DefaultGridDimension, DefaultGridDimension);
        [SerializeField, Min(0)] int loadRadius = 1;
        [SerializeField, Min(1)] int unloadRadius = 2;

        [Header("Производительность")]
        [SerializeField, Min(1)] int loadBudget = 3;
        [SerializeField, Min(0f)] float unloadDelaySeconds = 10f;
        [SerializeField, Min(0)] int predictiveLoadAhead = 2;
        [SerializeField, Min(0.05f)] float velocitySmoothing = 0.5f;

        [Tooltip("Минимальная скорость (м/с), при которой предиктив включается. " +
                 "Защита от дрожания при околонулевой скорости.")]
        [SerializeField, Min(0f)] float predictiveMinSpeed = 1f;

        [Header("Бюджет памяти (пока только варнинг)")]
        [SerializeField, Min(64f)] float memoryBudgetMB = 4096f;
        [SerializeField, Min(1f)] float memoryCheckInterval = 5f;

        [Header("Диагностика")]
        [SerializeField] bool verboseLogging = false;

        // ============================================================
        // Public events — persistence hooks
        // ============================================================

        public event Action<ChunkCoord, Scene> OnAfterChunkLoad;
        public event Action<ChunkCoord, Scene> OnBeforeChunkUnload;

        // ============================================================
        // Internal state
        // ============================================================

        enum ChunkState
        {
            Unknown,            // ещё не запрашивали existence
            CheckingExistence,  // летит запрос Addressables.LoadResourceLocationsAsync
            DoesNotExist,       // подтверждено отсутствие — финальное состояние
            Available,          // известно что есть, но не в памяти (выгружено или ещё не грузился)
            Queued,             // в очереди на загрузку, ждёт бюджета
            Loading,            // летит LoadSceneAsync
            Loaded,             // в памяти
            PendingUnload,      // на таймере отложенной выгрузки
            Unloading,          // летит UnloadSceneAsync — ещё не завершилось
        }

        class ChunkEntry
        {
            public ChunkCoord Coord;
            public ChunkState State;
            public AsyncOperationHandle<SceneInstance> SceneHandle;
            public AsyncOperationHandle<SceneInstance> UnloadHandle; // ждём пока выгрузится
            public float UnloadAtTime;
            public List<TaskCompletionSource<bool>> Barriers;

            /// <summary>
            /// Включается через EnsureChunkLoaded. Гарантирует, что чанк будет доведён до
            /// Loaded даже если игрок далеко (контракт "ensure" — это форсированная загрузка
            /// для телепортов и cutscene). Сбрасывается после успешной загрузки.
            /// </summary>
            public bool ForceLoad;

            /// <summary>
            /// Игрок захочет перезагрузить этот чанк после того, как он закончит выгрузку.
            /// Нужен, потому что UnloadSceneAsync асинхронен — между Pending→Unloading
            /// и фактическим завершением может прийти RequestLoad.
            /// </summary>
            public bool ReloadAfterUnload;
        }

        readonly Dictionary<ChunkCoord, ChunkEntry> _chunks = new();
        readonly List<ChunkCoord> _scratch = new();
        readonly List<ChunkEntry> _queueScratch = new();
        readonly HashSet<ChunkCoord> _desiredScratch = new();

        Vector3 _origin;
        Vector3 _lastTargetPos;
        Vector3 _smoothedVelocity;
        ChunkCoord _lastCenter;
        ChunkCoord _lastPredicted;
        bool _initialized;
        int _activeLoads;
        float _nextMemoryCheckTime;

        // ============================================================
        // Lifecycle
        // ============================================================

        void OnValidate()
        {
            if (unloadRadius <= loadRadius) unloadRadius = loadRadius + 1;
        }

        void Start()
        {
            if (target == null)
            {
                Debug.LogError("[ChunkStream] Не назначен target — выключаюсь.");
                enabled = false;
                return;
            }

            // Origin — это ЯКОРЬ глобальной сетки, не позиция игрока. Сетка фиксированно
            // привязана к мировым координатам, игрок просто оказывается в каком-то её чанке.
            _origin = gridOrigin;
            _lastTargetPos = target.position;
            _initialized = true;

            var startChunk = CurrentChunk();
            if (verboseLogging)
                Log($"gridOrigin = ({_origin.x:F1}, {_origin.z:F1}). " +
                    $"Игрок стартовал в ({target.position.x:F1}, {target.position.z:F1}) — это чанк {startChunk}.");

            _lastCenter = startChunk;
            _lastPredicted = _lastCenter;
            RecomputeRings();
        }

        void OnDestroy()
        {
            foreach (var entry in _chunks.Values) ResolveBarriers(entry, success: false);
        }

        void Update()
        {
            if (!_initialized) return;
            // Защита от уничтоженного target (респаун игрока через Destroy+Instantiate).
            // Unity-овский == null корректно ловит "missing reference" Object'ы.
            if (target == null)
            {
                Debug.LogWarning("[ChunkStream] target == null, выключаюсь. " +
                                 "Если игрок пересоздаётся — назначь новый target и заново enable.");
                enabled = false;
                return;
            }

            UpdateVelocity();

            var center = CurrentChunk();
            var predicted = ApplyPredictiveOffset(center);
            bool centerChanged = !center.Equals(_lastCenter);
            bool predictedChanged = !predicted.Equals(_lastPredicted);

            if (centerChanged || predictedChanged)
            {
                if (centerChanged && verboseLogging)
                    Log($"Игрок перешёл в чанк {center} (было {_lastCenter}).");
                _lastCenter = center;
                _lastPredicted = predicted;
                RecomputeRings();
            }

            ProcessPendingUnloads();
            ServiceLoadQueue();

            if (Time.unscaledTime >= _nextMemoryCheckTime)
            {
                _nextMemoryCheckTime = Time.unscaledTime + memoryCheckInterval;
                CheckMemoryBudget();
            }
        }

        // ============================================================
        // Public API
        // ============================================================

        /// <summary>
        /// Origin для текущего рендера. В Play-mode — кешированный (зафиксирован в Start),
        /// в Edit-mode — текущее значение из инспектора (чтобы gizmo обновлялись на лету
        /// при правке gridOrigin без запуска игры).
        /// </summary>
        Vector3 ActiveOrigin => _initialized ? _origin : gridOrigin;

        /// <summary>
        /// Чанк, в котором сейчас игрок. Считается от gridOrigin: рассчитываем позицию
        /// игрока относительно якоря и делим на chunkSize.
        /// </summary>
        public ChunkCoord CurrentChunk()
        {
            Vector3 rel = target.position - ActiveOrigin;
            // Та же математика, что в ChunkImport.ImportOne:
            //   u_center = (col + 0.5 - gridSize.x/2) * chunkSize
            // Инверсия для "в каком чанке точка u":
            //   col = floor(u / chunkSize + gridSize.x/2)
            // Для 8×8/100м игрок в мировом (0,0,0) → Chunk_04_04, в (-350,0,-350) → Chunk_00_00.
            return new ChunkCoord(
                Mathf.FloorToInt(rel.x / chunkSize + gridSize.x * 0.5f),
                Mathf.FloorToInt(rel.z / chunkSize + gridSize.y * 0.5f));
        }

        /// <summary>Мировой центр чанка (Y=0). Зеркало CurrentChunk.</summary>
        public Vector3 ChunkWorldCenter(ChunkCoord c)
        {
            return ActiveOrigin + new Vector3(
                (c.X + 0.5f - gridSize.x * 0.5f) * chunkSize,
                0f,
                (c.Y + 0.5f - gridSize.y * 0.5f) * chunkSize);
        }

        /// <summary>
        /// Координата лежит в пределах конечной сетки [0, gridSize)? Off-grid не запрашиваются —
        /// иначе у краёв стример постоянно бил бы в Addressables по несуществующим адресам.
        /// </summary>
        bool InGrid(ChunkCoord c)
            => c.X >= 0 && c.X < gridSize.x && c.Y >= 0 && c.Y < gridSize.y;

        /// <summary>AABB чанка в мировых координатах.</summary>
        public Bounds ChunkWorldBounds(ChunkCoord c, float heightY = 100f)
        {
            return new Bounds(ChunkWorldCenter(c), new Vector3(chunkSize, heightY, chunkSize));
        }

        /// <summary>
        /// Stream barrier: гарантирует, что чанк загружен. Используется для телепортов.
        /// Возвращает true если загружен или уже в памяти, false если не существует или сбой.
        /// Главный чанк (0,0) всегда true.
        ///
        /// Контракт: ForceLoad — этот чанк ДОЛЖЕН быть доведён до Loaded даже если игрок
        /// далеко. Иначе телепорт мог бы зависнуть на барьере, который никогда не резолвится,
        /// потому что обычный код решил, что чанк "уже не нужен".
        /// </summary>
        public Task<bool> EnsureChunkLoaded(ChunkCoord coord)
        {
            if (!InGrid(coord)) return Task.FromResult(false);

            var entry = GetOrCreateEntry(coord);

            switch (entry.State)
            {
                case ChunkState.Loaded:
                    return Task.FromResult(true);
                case ChunkState.DoesNotExist:
                    return Task.FromResult(false);
                case ChunkState.PendingUnload:
                    entry.State = ChunkState.Loaded;
                    return Task.FromResult(true);
                case ChunkState.Unloading:
                    // Сцена ещё в процессе выгрузки. После её завершения сразу перезагрузим.
                    entry.ReloadAfterUnload = true;
                    break;
                case ChunkState.Unknown:
                    StartExistenceCheck(entry);
                    break;
                case ChunkState.Available:
                    entry.State = ChunkState.Queued;
                    break;
                // CheckingExistence / Queued / Loading — уже движутся; форсируем флагом ниже.
            }

            // Главное: помечаем намерение довести до Loaded даже если игрок отойдёт.
            entry.ForceLoad = true;

            if (entry.Barriers == null) entry.Barriers = new List<TaskCompletionSource<bool>>();
            // RunContinuationsAsynchronously — чтобы await продолжился НЕ синхронно прямо
            // из нашего TrySetResult (который может выполниться в чужом callback'е Addressables).
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Barriers.Add(tcs);
            return tcs.Task;
        }

        public System.Collections.IEnumerator EnsureChunkLoadedCoroutine(ChunkCoord coord)
        {
            var task = EnsureChunkLoaded(coord);
            while (!task.IsCompleted) yield return null;
        }

        // ============================================================
        // Velocity & predictive prefetch
        // ============================================================

        void UpdateVelocity()
        {
            Vector3 currentPos = target.position;
            Vector3 delta = currentPos - _lastTargetPos;
            delta.y = 0f;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);

            // Защита от телепортов: если игрок прыгнул больше чем на полчанка за кадр,
            // это не движение, а скачок (fast travel, респаун, cutscene-snap). НЕ учитываем
            // его в скорости — иначе _smoothedVelocity накачается до тысяч м/с и предиктив
            // запустит кольцо загрузки в сторону телепорта на несколько кадров.
            float teleportThresholdSqr = chunkSize * chunkSize * 0.25f;
            if (delta.sqrMagnitude > teleportThresholdSqr)
            {
                _lastTargetPos = currentPos;
                _smoothedVelocity = Vector3.zero;
                if (verboseLogging)
                    Log($"Обнаружен телепорт ({delta.magnitude:F0}м за кадр) — сброс скорости.");
                return;
            }

            Vector3 instantVel = delta / dt;
            float alpha = 1f - Mathf.Exp(-dt / velocitySmoothing);
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, instantVel, alpha);
            _lastTargetPos = currentPos;
        }

        ChunkCoord ApplyPredictiveOffset(ChunkCoord realCenter)
        {
            if (predictiveLoadAhead <= 0) return realCenter;
            float speed = _smoothedVelocity.magnitude;
            if (speed < predictiveMinSpeed) return realCenter;
            Vector3 dir = _smoothedVelocity.normalized;
            int dx = Mathf.RoundToInt(dir.x * predictiveLoadAhead);
            int dy = Mathf.RoundToInt(dir.z * predictiveLoadAhead);
            return new ChunkCoord(realCenter.X + dx, realCenter.Y + dy);
        }

        // ============================================================
        // Ring recomputation — desire set + cancellation
        // ============================================================

        void RecomputeRings()
        {
            var center = _lastCenter;
            var predicted = _lastPredicted;

            // 1) Желаемое множество = кольцо вокруг текущего + кольцо вокруг предсказанного.
            _desiredScratch.Clear();
            FillRingAround(center, loadRadius, _desiredScratch);
            if (predictiveLoadAhead > 0)
                FillRingAround(predicted, loadRadius, _desiredScratch);

            // 2) Заявка на загрузку всех желаемых, кого ещё нет в памяти.
            //    Off-grid координаты RequestLoad отсечёт через InGrid().
            foreach (var c in _desiredScratch) RequestLoad(c);

            // 3) Отмена очереди и pending-unload для уже-загружаемых/уже-загруженных,
            //    которые перестали быть желаемыми.
            _scratch.Clear();
            foreach (var kv in _chunks)
            {
                var entry = kv.Value;
                bool desired = _desiredScratch.Contains(entry.Coord);

                switch (entry.State)
                {
                    case ChunkState.Queued:
                        // Передумали грузить — откатываем в Available (existence кешируется).
                        // НО: если стоит ForceLoad (от EnsureChunkLoaded) — не трогаем,
                        // контракт барьера выше пользовательского движения.
                        if (!desired && !entry.ForceLoad)
                        {
                            entry.State = ChunkState.Available;
                            if (verboseLogging) Log($"Отменил Queued для {entry.Coord} — больше не нужен.");
                        }
                        break;

                    case ChunkState.Loaded:
                        // Уже в памяти, но больше не желателен — на таймер выгрузки.
                        int distActual = entry.Coord.ChebyshevDistance(center);
                        int distPredicted = entry.Coord.ChebyshevDistance(predicted);
                        int dist = Mathf.Min(distActual, distPredicted);
                        if (dist > unloadRadius) _scratch.Add(entry.Coord);
                        break;

                    case ChunkState.PendingUnload:
                        // Был на таймере, но игрок вернулся в зону — оживляем.
                        if (desired)
                        {
                            entry.State = ChunkState.Loaded;
                            if (verboseLogging) Log($"Отменён pending unload для {entry.Coord}.");
                        }
                        break;

                    case ChunkState.Unloading:
                        // Сейчас выгружается. Если снова стал нужен — попросим перезагрузить
                        // когда выгрузка завершится.
                        if (desired) entry.ReloadAfterUnload = true;
                        break;

                    // CheckingExistence / Loading — пусть закончат своё дело,
                    // в их completion-callback стоит проверка "ещё нужен?" + ForceLoad.
                    // Unknown / DoesNotExist / Available — нечего отменять.
                }
            }

            foreach (var c in _scratch) MarkPendingUnload(c);
        }

        void FillRingAround(ChunkCoord center, int radius, HashSet<ChunkCoord> sink)
        {
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                    sink.Add(new ChunkCoord(center.X + dx, center.Y + dy));
        }

        /// <summary>
        /// Чанк сейчас нужен (в активном кольце загрузки от current или predicted)?
        /// ВАЖНО: эта функция вычисляется из _lastCenter/_lastPredicted напрямую и НЕ
        /// зависит от _desiredScratch — поэтому её безопасно вызывать из async-колбэков
        /// Addressables, в момент, когда _desiredScratch может быть в промежуточном
        /// состоянии (пересоздаётся в RecomputeRings).
        /// </summary>
        bool IsDesired(ChunkCoord c)
        {
            if (c.ChebyshevDistance(_lastCenter) <= loadRadius) return true;
            if (predictiveLoadAhead > 0 && !_lastPredicted.Equals(_lastCenter))
                if (c.ChebyshevDistance(_lastPredicted) <= loadRadius) return true;
            return false;
        }

        /// <summary>
        /// Чанк вышел за гистерезисный буфер (от обоих центров)? Используется в
        /// completion-колбэках для решения "выгружать ли только что загруженный чанк".
        /// </summary>
        bool IsBeyondUnloadRange(ChunkCoord c)
        {
            int distA = c.ChebyshevDistance(_lastCenter);
            int distP = c.ChebyshevDistance(_lastPredicted);
            return Mathf.Min(distA, distP) > unloadRadius;
        }

        // ============================================================
        // Lifecycle переходов чанка
        // ============================================================

        ChunkEntry GetOrCreateEntry(ChunkCoord c)
        {
            if (!_chunks.TryGetValue(c, out var entry))
            {
                entry = new ChunkEntry { Coord = c, State = ChunkState.Unknown };
                _chunks[c] = entry;
            }
            return entry;
        }

        void RequestLoad(ChunkCoord c)
        {
            if (!InGrid(c)) return;
            var entry = GetOrCreateEntry(c);

            switch (entry.State)
            {
                case ChunkState.Unknown:
                    StartExistenceCheck(entry);
                    break;
                case ChunkState.Available:
                    entry.State = ChunkState.Queued;
                    if (verboseLogging) Log($"{entry.Coord}: Available → Queued.");
                    break;
                case ChunkState.PendingUnload:
                    entry.State = ChunkState.Loaded;
                    if (verboseLogging) Log($"{entry.Coord}: PendingUnload → Loaded (откат).");
                    break;
                case ChunkState.Unloading:
                    // Сцена ещё выгружается — попросим перезагрузить, когда закончит.
                    entry.ReloadAfterUnload = true;
                    if (verboseLogging) Log($"{entry.Coord}: Unloading, поставлю reload-after-unload.");
                    break;
                // Остальные состояния — уже движутся к нужному терминалу или не движутся вообще.
            }
        }

        void StartExistenceCheck(ChunkEntry entry)
        {
            entry.State = ChunkState.CheckingExistence;
            string addr = entry.Coord.ToAddress();

            var handle = Addressables.LoadResourceLocationsAsync(addr, typeof(SceneInstance));
            handle.Completed += op =>
            {
                bool exists = op.Status == AsyncOperationStatus.Succeeded
                              && op.Result != null && op.Result.Count > 0;
                Addressables.Release(op);

                if (!exists)
                {
                    entry.State = ChunkState.DoesNotExist;
                    if (verboseLogging)
                        Log($"{entry.Coord} ('{addr}') не существует — игнорю.");
                    ResolveBarriers(entry, success: false);
                    return;
                }

                // Существует. Дальше — нужен ли он СЕЙЧАС:
                // - ForceLoad (через EnsureChunkLoaded) ВСЕГДА доводит до Queued.
                // - Иначе проверяем актуальный desire (IsDesired считается из _lastCenter/
                //   _lastPredicted напрямую, не из _desiredScratch — это снимает гонку).
                if (entry.ForceLoad || IsDesired(entry.Coord))
                {
                    entry.State = ChunkState.Queued;
                    if (verboseLogging) Log($"{entry.Coord} ('{addr}') существует — в очередь.");
                }
                else
                {
                    entry.State = ChunkState.Available;
                    if (verboseLogging)
                        Log($"{entry.Coord} ('{addr}') существует, но уже не нужен — Available.");
                    // Барьеры (если есть) резолвим неудачей: чанк не загружен и не будет
                    // загружаться (никто не звал EnsureChunkLoaded → ForceLoad=false).
                    ResolveBarriers(entry, success: false);
                }
            };
        }

        void ServiceLoadQueue()
        {
            if (_activeLoads >= loadBudget) return;

            _queueScratch.Clear();
            foreach (var entry in _chunks.Values)
                if (entry.State == ChunkState.Queued) _queueScratch.Add(entry);

            if (_queueScratch.Count == 0) return;

            var center = _lastCenter;
            _queueScratch.Sort((a, b) =>
                a.Coord.SquaredEuclideanDistance(center)
                    .CompareTo(b.Coord.SquaredEuclideanDistance(center)));

            for (int i = 0; i < _queueScratch.Count && _activeLoads < loadBudget; i++)
                StartLoad(_queueScratch[i]);
        }

        void StartLoad(ChunkEntry entry)
        {
            string addr = entry.Coord.ToAddress();
            entry.State = ChunkState.Loading;
            _activeLoads++;

            if (verboseLogging)
                Log($"→ загружаю {entry.Coord} ('{addr}'). Активных: {_activeLoads}/{loadBudget}.");

            entry.SceneHandle = Addressables.LoadSceneAsync(addr, LoadSceneMode.Additive);
            entry.SceneHandle.Completed += op =>
            {
                _activeLoads--;

                if (op.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogWarning($"[ChunkStream] ✗ Не удалось загрузить {entry.Coord} ('{addr}').");
                    Addressables.Release(op);
                    entry.State = ChunkState.Available; // existence известно, попробуем потом
                    entry.SceneHandle = default;
                    entry.ForceLoad = false;
                    ResolveBarriers(entry, success: false);
                    return;
                }

                // Игрок мог уйти за время загрузки. Если уже не нужен — сразу выгружаем.
                // ВАЖНО: проверяем ОБА расстояния (current И predicted) — иначе ломается
                // предиктивная подгрузка (чанк, загруженный для prediction, сразу же ушёл бы
                // на выгрузку). ForceLoad обходит проверку — это контракт EnsureChunkLoaded.
                if (!entry.ForceLoad && IsBeyondUnloadRange(entry.Coord))
                {
                    if (verboseLogging)
                        Log($"{entry.Coord} загрузился, но игрок ушёл — сразу выгружаю.");
                    BeginUnload(entry);
                    ResolveBarriers(entry, success: false);
                    return;
                }

                entry.State = ChunkState.Loaded;
                entry.ForceLoad = false; // флаг свою задачу выполнил
                if (verboseLogging) Log($"✓ {entry.Coord} ('{addr}') загружен.");

                try { OnAfterChunkLoad?.Invoke(entry.Coord, op.Result.Scene); }
                catch (Exception e) { Debug.LogException(e); }

                ResolveBarriers(entry, success: true);
            };
        }

        // ============================================================
        // Unload (timed) — entries сохраняются с Available, не удаляются
        // ============================================================

        void MarkPendingUnload(ChunkCoord c)
        {
            if (!_chunks.TryGetValue(c, out var entry)) return;
            if (entry.State != ChunkState.Loaded) return;
            entry.State = ChunkState.PendingUnload;
            entry.UnloadAtTime = Time.unscaledTime + unloadDelaySeconds;
            if (verboseLogging) Log($"{c} на таймер выгрузки ({unloadDelaySeconds:F1}с).");
        }

        void ProcessPendingUnloads()
        {
            _scratch.Clear();
            foreach (var kv in _chunks)
                if (kv.Value.State == ChunkState.PendingUnload && Time.unscaledTime >= kv.Value.UnloadAtTime)
                    _scratch.Add(kv.Key);

            foreach (var c in _scratch)
                if (_chunks.TryGetValue(c, out var entry))
                    BeginUnload(entry);
        }

        /// <summary>
        /// Запускает выгрузку чанка. UnloadSceneAsync асинхронный — пока он не завершится,
        /// чанк висит в состоянии Unloading. Если в это время кто-то вызовет RequestLoad
        /// или EnsureChunkLoaded на этом чанке — ставим entry.ReloadAfterUnload, и после
        /// завершения выгрузки сразу пускаем в Queued. Так избегаем гонки "состояние сказало
        /// Available, а сцена ещё выгружается".
        /// </summary>
        void BeginUnload(ChunkEntry entry)
        {
            if (!entry.SceneHandle.IsValid())
            {
                // Нечего выгружать — handle уже невалидный.
                entry.State = ChunkState.Available;
                return;
            }

            // Persistence hook — последний шанс снять snapshot ДО уничтожения объектов.
            try { OnBeforeChunkUnload?.Invoke(entry.Coord, entry.SceneHandle.Result.Scene); }
            catch (Exception e) { Debug.LogException(e); }

            if (verboseLogging) Log($"← начинаю выгрузку {entry.Coord}.");

            var unloadHandle = Addressables.UnloadSceneAsync(entry.SceneHandle);
            entry.UnloadHandle = unloadHandle;
            entry.SceneHandle = default;
            entry.State = ChunkState.Unloading;

            unloadHandle.Completed += op =>
            {
                entry.UnloadHandle = default;

                if (entry.ReloadAfterUnload)
                {
                    entry.ReloadAfterUnload = false;
                    entry.State = ChunkState.Queued;
                    if (verboseLogging) Log($"{entry.Coord}: Unloading → Queued (был запрос перезагрузки).");
                }
                else
                {
                    entry.State = ChunkState.Available;
                    if (verboseLogging) Log($"{entry.Coord}: выгружен → Available.");
                }
            };
        }

        // ============================================================
        // Stream barriers
        // ============================================================

        void ResolveBarriers(ChunkEntry entry, bool success)
        {
            if (entry.Barriers == null || entry.Barriers.Count == 0) return;
            foreach (var tcs in entry.Barriers) tcs.TrySetResult(success);
            entry.Barriers.Clear();
        }

        // ============================================================
        // Memory budget — пока warning
        // ============================================================

        void CheckMemoryBudget()
        {
            // Profiler.GetTotalAllocatedMemoryLong отдаёт ОБЪЕДИНЁННЫЙ объём управляемой и
            // native памяти — текстуры, меши, аудио, gameobjects, сцены. То что нам и нужно
            // для стриминга. GC.GetTotalMemory был бы только managed heap (как замечал ревью)
            // и игнорировал бы основной вес — native ассеты загруженных сцен.
            long bytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            float mb = bytes / (1024f * 1024f);
            if (mb > memoryBudgetMB)
            {
                Debug.LogWarning(
                    $"[ChunkStream] Бюджет памяти превышен: {mb:F0}/{memoryBudgetMB:F0} МБ " +
                    "(Profiler.GetTotalAllocatedMemoryLong, native+managed). " +
                    $"Активных чанков: {CountByState(ChunkState.Loaded)}. " +
                    "TODO: реализовать агрессивную выгрузку дальних.");
            }
        }

        int CountByState(ChunkState s)
        {
            int n = 0;
            foreach (var e in _chunks.Values) if (e.State == s) n++;
            return n;
        }

        void Log(string msg) => Debug.Log($"[ChunkStream] {msg}");

        // ============================================================
        // Gizmo — информативно, плавно следит за игроком
    }
}