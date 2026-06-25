using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace ProjectName.World
{
    /// <summary>
    /// Integer chunk coordinate on the grid. Matches the XX/YY index in the
    /// Chunk_XX_YY.unity scene name produced by ChunkImport when exporting FBX chunks.
    /// Converting a coordinate to a world position is done via ChunkStreamer.ChunkWorldCenter —
    /// the formula depends on gridSize, so it is intentionally not here.
    /// Y here is the name in chunk coordinates, not to be confused with world Y (height).
    /// </summary>
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int X;
        public readonly int Y;

        public ChunkCoord(int x, int y) { X = x; Y = y; }

        /// <summary>Chebyshev ("chessboard") distance — matches the square rings.</summary>
        public int ChebyshevDistance(ChunkCoord o)
            => Mathf.Max(Mathf.Abs(X - o.X), Mathf.Abs(Y - o.Y));

        /// <summary>Squared Euclidean distance. Used for load priority (closer = sooner).</summary>
        public int SquaredEuclideanDistance(ChunkCoord o)
        {
            int dx = X - o.X, dy = Y - o.Y;
            return dx * dx + dy * dy;
        }

        public bool Equals(ChunkCoord o) => X == o.X && Y == o.Y;
        public override bool Equals(object o) => o is ChunkCoord c && Equals(c);
        // HashCode.Combine — standard mix, no overflow risk on large coordinates
        // (the old version with X*73856093 overflowed for X > ~29k; for int wrap-around
        // this didn't break correctness, but the new version is tidier).
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
        // Matches the .unity scene name produced by ChunkImport (sceneNamePrefix="Chunk_", :00 padding).
        public string ToAddress() => $"Chunk_{X:00}_{Y:00}";
    }

    /// <summary>
    /// Chunk streamer v2.1 — fixes:
    ///  • Origin is the center of chunk (0,0), not the corner. The player starts in the middle of the start chunk.
    ///  • Existence cache survives unload: an Available state is introduced for "known to
    ///    exist but not in memory". On re-entry there is no extra Addressables request.
    ///  • Queued chunks that became unneeded are cancelled (rolled back to Available)
    ///    before they actually start loading. Protects against spurious IO operations
    ///    under jittery predictive movement.
    ///  • More informative gizmos: coordinate grid, player crosshair (smoothly follows),
    ///    velocity arrow, color fills per state, pending-unload countdown,
    ///    coordinate labels.
    /// </summary>
    public partial class ChunkStreamer : MonoBehaviour
    {
        // ============================================================
        // Canonical grid defaults. Change them HERE — both the streamer and ChunkImport
        // will pick up the same value for a fresh setup. Values that are already
        // serialized (on the component in a scene, and in the importer's EditorPrefs)
        // will NOT update automatically — edit them in the Inspector / importer window,
        // or one-off reset the EditorPrefs keys and Reset the component.
        // ============================================================

        public const float DefaultChunkSize     = 96f;
        public const int   DefaultGridDimension = 8;

        // ============================================================
        // Configuration
        // ============================================================

        [Header("Tracking target")]
        [SerializeField] Transform target;

        [Header("Grid")]
        [SerializeField, Min(1f)] float chunkSize = DefaultChunkSize;
        [Tooltip("Grid size in chunks (X = columns, Y = rows). Must match GRID_X / GRID_Y " +
                 "in the Blender export script. Used both to translate the player's world position " +
                 "into a Chunk_XX_YY coordinate, and to cull requests outside the grid bounds.")]
        [SerializeField] Vector2Int gridSize = new Vector2Int(DefaultGridDimension, DefaultGridDimension);
        [SerializeField, Min(0)] int loadRadius = 1;
        [SerializeField, Min(1)] int unloadRadius = 2;

        [Header("Performance")]
        [SerializeField, Min(1)] int loadBudget = 3;
        [SerializeField, Min(0f)] float unloadDelaySeconds = 5f;
        [SerializeField, Min(0)] int predictiveLoadAhead = 2;
        [SerializeField, Min(0.05f)] float velocitySmoothing = 0.5f;

        [Tooltip("Minimum speed (m/s) at which predictive prefetch kicks in. " +
                 "Guard against jitter at near-zero speed.")]
        [SerializeField, Min(0f)] float predictiveMinSpeed = 1f;

        [Header("Memory budget (warning only for now)")]
        [SerializeField, Min(64f)] float memoryBudgetMB = 2048f;
        [SerializeField, Min(1f)] float memoryCheckInterval = 5f;

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
            Unknown,            // existence not yet queried
            CheckingExistence,  // Addressables.LoadResourceLocationsAsync in flight
            DoesNotExist,       // confirmed missing — terminal state
            Available,          // known to exist but not in memory (unloaded or never loaded)
            Queued,             // queued for load, waiting on budget
            Loading,            // LoadSceneAsync in flight
            Loaded,             // in memory
            PendingUnload,      // on the deferred-unload timer
            Unloading,          // UnloadSceneAsync in flight — not yet finished
        }

        class ChunkEntry
        {
            public ChunkCoord Coord;
            public ChunkState State;
            public AsyncOperationHandle<SceneInstance> SceneHandle;
            public AsyncOperationHandle<SceneInstance> UnloadHandle; // wait until unload finishes
            public float UnloadAtTime;
            public List<TaskCompletionSource<bool>> Barriers;

            /// <summary>
            /// Set by EnsureChunkLoaded. Guarantees the chunk will be driven to Loaded
            /// even if the player is far away (the "ensure" contract — a forced load
            /// for teleports and cutscenes). Cleared after a successful load.
            /// </summary>
            public bool ForceLoad;

            /// <summary>
            /// The player will want this chunk reloaded after it finishes unloading.
            /// Needed because UnloadSceneAsync is async — between Pending→Unloading
            /// and actual completion, a RequestLoad may arrive.
            /// </summary>
            public bool ReloadAfterUnload;
        }

        readonly Dictionary<ChunkCoord, ChunkEntry> _chunks = new();
        readonly List<ChunkCoord> _scratch = new();
        readonly List<ChunkEntry> _queueScratch = new();
        readonly HashSet<ChunkCoord> _desiredScratch = new();

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
                Debug.LogError("[ChunkStreamer] target is not assigned — disabling.");
                enabled = false;
                return;
            }

            _lastTargetPos = target.position;
            _initialized = true;

            _lastCenter = CurrentChunk();
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
            // Guard against a destroyed target (player respawn via Destroy+Instantiate).
            // Unity's == null correctly catches "missing reference" Objects.
            if (target == null)
            {
                Debug.LogWarning("[ChunkStreamer] target == null, disabling. " +
                                 "If the player is being recreated — assign a new target and re-enable.");
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
        /// The chunk the player is currently in. The grid is always centered at world origin,
        /// so we just divide the player's world position by chunkSize.
        /// </summary>
        public ChunkCoord CurrentChunk()
        {
            Vector3 pos = target.position;
            // Same math as in ChunkImport.ImportOne:
            //   u_center = (col + 0.5 - gridSize.x/2) * chunkSize
            // Inverted to "which chunk a point u belongs to":
            //   col = floor(u / chunkSize + gridSize.x/2)
            // For 8×8/100m a player at world (0,0,0) → Chunk_04_04, at (-350,0,-350) → Chunk_00_00.
            return new ChunkCoord(
                Mathf.FloorToInt(pos.x / chunkSize + gridSize.x * 0.5f),
                Mathf.FloorToInt(pos.z / chunkSize + gridSize.y * 0.5f));
        }

        /// <summary>World center of a chunk (Y=0). Mirrors CurrentChunk.</summary>
        public Vector3 ChunkWorldCenter(ChunkCoord c)
        {
            return new Vector3(
                (c.X + 0.5f - gridSize.x * 0.5f) * chunkSize,
                0f,
                (c.Y + 0.5f - gridSize.y * 0.5f) * chunkSize);
        }

        /// <summary>
        /// Is the coordinate within the finite grid [0, gridSize)? Off-grid coords are
        /// not queried — otherwise edges would constantly hit Addressables with
        /// non-existent addresses.
        /// </summary>
        bool InGrid(ChunkCoord c)
            => c.X >= 0 && c.X < gridSize.x && c.Y >= 0 && c.Y < gridSize.y;

        /// <summary>AABB of a chunk in world coordinates.</summary>
        public Bounds ChunkWorldBounds(ChunkCoord c, float heightY = 100f)
        {
            return new Bounds(ChunkWorldCenter(c), new Vector3(chunkSize, heightY, chunkSize));
        }

        /// <summary>
        /// Stream barrier: guarantees the chunk is loaded. Used for teleports.
        /// Returns true if loaded or already in memory, false if it doesn't exist or load failed.
        /// The main chunk (0,0) is always true.
        ///
        /// Contract: ForceLoad — this chunk MUST be driven to Loaded even if the player
        /// is far away. Otherwise a teleport could hang on a barrier that never resolves
        /// because regular code decided the chunk is "no longer needed".
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
                    // Scene is still in the middle of unloading. Reload as soon as it finishes.
                    entry.ReloadAfterUnload = true;
                    break;
                case ChunkState.Unknown:
                    StartExistenceCheck(entry);
                    break;
                case ChunkState.Available:
                    entry.State = ChunkState.Queued;
                    break;
                // CheckingExistence / Queued / Loading — already in motion; the flag below forces them.
            }

            // The point: mark the intent to drive to Loaded even if the player walks away.
            entry.ForceLoad = true;

            if (entry.Barriers == null) entry.Barriers = new List<TaskCompletionSource<bool>>();
            // RunContinuationsAsynchronously — so the await does NOT continue synchronously from
            // our TrySetResult (which may execute inside someone else's Addressables callback).
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

            // Teleport guard: if the player jumped more than half a chunk in a single frame,
            // it's not movement but a snap (fast travel, respawn, cutscene snap). Do NOT feed
            // this into velocity — otherwise _smoothedVelocity would spike into thousands of m/s
            // and predictive would launch a load ring towards the teleport for several frames.
            float teleportThresholdSqr = chunkSize * chunkSize * 0.25f;
            if (delta.sqrMagnitude > teleportThresholdSqr)
            {
                _lastTargetPos = currentPos;
                _smoothedVelocity = Vector3.zero;
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
            // Scale so the dominant axis equals predictiveLoadAhead BEFORE rounding.
            // Naive `dir * predictiveLoadAhead` under-shoots on diagonals:
            //   east (1, 0)        * 2 = (2.00, 0.00) → (+2,  0), Chebyshev 2 ahead
            //   NE   (.707, .707)  * 2 = (1.41, 1.41) → (+1, +1), Chebyshev only 1 ahead
            // → diagonal predictive ring almost overlaps current ring, real lookahead lost,
            //   causes stalls on diagonal traversal. Max-axis normalization keeps the
            //   predicted center at Chebyshev ≈ predictiveLoadAhead in every direction.
            float scale = predictiveLoadAhead / Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.z));
            int dx = Mathf.RoundToInt(dir.x * scale);
            int dy = Mathf.RoundToInt(dir.z * scale);
            return new ChunkCoord(realCenter.X + dx, realCenter.Y + dy);
        }

        // ============================================================
        // Ring recomputation — desire set + cancellation
        // ============================================================

        void RecomputeRings()
        {
            var center = _lastCenter;
            var predicted = _lastPredicted;

            // 1) Desired set = ring around current + ring around predicted.
            _desiredScratch.Clear();
            FillRingAround(center, loadRadius, _desiredScratch);
            if (predictiveLoadAhead > 0)
                FillRingAround(predicted, loadRadius, _desiredScratch);

            // 2) Request a load for every desired chunk not yet in memory.
            //    Off-grid coords are filtered out by RequestLoad via InGrid().
            foreach (var c in _desiredScratch) RequestLoad(c);

            // 3) Cancel queue/pending-unload for already-loading/already-loaded chunks
            //    that stopped being desired.
            _scratch.Clear();
            foreach (var kv in _chunks)
            {
                var entry = kv.Value;
                bool desired = _desiredScratch.Contains(entry.Coord);

                switch (entry.State)
                {
                    case ChunkState.Queued:
                        // Changed our mind — roll back to Available (existence stays cached).
                        // BUT: if ForceLoad is set (via EnsureChunkLoaded) — leave it alone;
                        // the barrier contract trumps user movement.
                        if (!desired && !entry.ForceLoad)
                        {
                            entry.State = ChunkState.Available;
                        }
                        break;

                    case ChunkState.Loaded:
                        // In memory but no longer desired — put it on the unload timer.
                        int distActual = entry.Coord.ChebyshevDistance(center);
                        int distPredicted = entry.Coord.ChebyshevDistance(predicted);
                        int dist = Mathf.Min(distActual, distPredicted);
                        if (dist > unloadRadius) _scratch.Add(entry.Coord);
                        break;

                    case ChunkState.PendingUnload:
                        // Was on the timer, but the player walked back into the zone — revive it.
                        if (desired)
                        {
                            entry.State = ChunkState.Loaded;
                        }
                        break;

                    case ChunkState.Unloading:
                        // Currently unloading. If it became wanted again — schedule a reload
                        // for when the unload completes.
                        if (desired) entry.ReloadAfterUnload = true;
                        break;

                    // CheckingExistence / Loading — let them finish their work;
                    // their completion callback re-checks "still wanted?" and ForceLoad.
                    // Unknown / DoesNotExist / Available — nothing to cancel.
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
        /// Is the chunk currently wanted (in the active load ring of current or predicted)?
        /// IMPORTANT: this is computed from _lastCenter/_lastPredicted directly and does NOT
        /// depend on _desiredScratch — so it's safe to call from Addressables async callbacks
        /// at a moment when _desiredScratch may be in an intermediate state (being rebuilt
        /// inside RecomputeRings).
        /// </summary>
        bool IsDesired(ChunkCoord c)
        {
            if (c.ChebyshevDistance(_lastCenter) <= loadRadius) return true;
            if (predictiveLoadAhead > 0 && !_lastPredicted.Equals(_lastCenter))
                if (c.ChebyshevDistance(_lastPredicted) <= loadRadius) return true;
            return false;
        }

        /// <summary>
        /// Is the chunk past the hysteresis buffer (from both centers)? Used in completion
        /// callbacks to decide "should we unload the chunk we just finished loading?"
        /// </summary>
        bool IsBeyondUnloadRange(ChunkCoord c)
        {
            int distA = c.ChebyshevDistance(_lastCenter);
            int distP = c.ChebyshevDistance(_lastPredicted);
            return Mathf.Min(distA, distP) > unloadRadius;
        }

        // ============================================================
        // Chunk lifecycle transitions
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
                    break;
                case ChunkState.PendingUnload:
                    entry.State = ChunkState.Loaded;
                    break;
                case ChunkState.Unloading:
                    // Scene is still unloading — request a reload for when it finishes.
                    entry.ReloadAfterUnload = true;
                    break;
                // Other states are either already heading to the right terminal or going nowhere.
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
                    ResolveBarriers(entry, success: false);
                    return;
                }

                // It exists. Next — is it wanted RIGHT NOW:
                // - ForceLoad (via EnsureChunkLoaded) ALWAYS drives it to Queued.
                // - Otherwise check the live desire (IsDesired reads from _lastCenter/
                //   _lastPredicted directly, not from _desiredScratch — that removes the race).
                if (entry.ForceLoad || IsDesired(entry.Coord))
                {
                    entry.State = ChunkState.Queued;
                }
                else
                {
                    entry.State = ChunkState.Available;
                    // Resolve any barriers with failure: the chunk is not loaded and won't
                    // be loaded (nobody called EnsureChunkLoaded → ForceLoad=false).
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

            entry.SceneHandle = Addressables.LoadSceneAsync(addr, LoadSceneMode.Additive);
            entry.SceneHandle.Completed += op =>
            {
                _activeLoads--;

                if (op.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogWarning($"[ChunkStreamer] ✗ Failed to load {entry.Coord} ('{addr}').");
                    Addressables.Release(op);
                    entry.State = ChunkState.Available; // existence is known, try again later
                    entry.SceneHandle = default;
                    entry.ForceLoad = false;
                    ResolveBarriers(entry, success: false);
                    return;
                }

                // The player may have moved away during the load. If no longer wanted, unload now.
                // IMPORTANT: check BOTH distances (current AND predicted) — otherwise predictive
                // prefetch breaks (a chunk loaded for prediction would immediately go to unload).
                // ForceLoad bypasses the check — that's the EnsureChunkLoaded contract.
                if (!entry.ForceLoad && IsBeyondUnloadRange(entry.Coord))
                {
                    BeginUnload(entry);
                    ResolveBarriers(entry, success: false);
                    return;
                }

                entry.State = ChunkState.Loaded;
                entry.ForceLoad = false; // flag has done its job

                try { OnAfterChunkLoad?.Invoke(entry.Coord, op.Result.Scene); }
                catch (Exception e) { Debug.LogException(e); }

                ResolveBarriers(entry, success: true);
            };
        }

        // ============================================================
        // Unload (timed) — entries are kept as Available, not deleted
        // ============================================================

        void MarkPendingUnload(ChunkCoord c)
        {
            if (!_chunks.TryGetValue(c, out var entry)) return;
            if (entry.State != ChunkState.Loaded) return;
            entry.State = ChunkState.PendingUnload;
            entry.UnloadAtTime = Time.unscaledTime + unloadDelaySeconds;
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
        /// Kicks off a chunk unload. UnloadSceneAsync is async — until it completes,
        /// the chunk stays in Unloading state. If RequestLoad or EnsureChunkLoaded fires
        /// on this chunk in the meantime, we set entry.ReloadAfterUnload, and once unload
        /// finishes we go straight to Queued. This avoids the race "state says Available
        /// but the scene is still unloading".
        /// </summary>
        void BeginUnload(ChunkEntry entry)
        {
            if (!entry.SceneHandle.IsValid())
            {
                // Nothing to unload — the handle is already invalid.
                entry.State = ChunkState.Available;
                return;
            }

            // Persistence hook — last chance to snapshot BEFORE the objects are destroyed.
            try { OnBeforeChunkUnload?.Invoke(entry.Coord, entry.SceneHandle.Result.Scene); }
            catch (Exception e) { Debug.LogException(e); }

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
                }
                else
                {
                    entry.State = ChunkState.Available;
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
        // Memory budget — warning for now
        // ============================================================

        void CheckMemoryBudget()
        {
            // Profiler.GetTotalAllocatedMemoryLong returns the COMBINED managed + native
            // footprint — textures, meshes, audio, gameobjects, scenes. Exactly what we need
            // for streaming. GC.GetTotalMemory would be managed heap only (as the review noted)
            // and would miss the bulk — native assets of loaded scenes.
            long bytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            float mb = bytes / (1024f * 1024f);
            if (mb > memoryBudgetMB)
            {
                Debug.LogWarning(
                    $"[ChunkStreamer] Memory budget exceeded: {mb:F0}/{memoryBudgetMB:F0} MB " +
                    "(Profiler.GetTotalAllocatedMemoryLong, native+managed). " +
                    $"Active chunks: {CountByState(ChunkState.Loaded)}. " +
                    "TODO: implement aggressive unload of far chunks.");
            }
        }

        int CountByState(ChunkState s)
        {
            int n = 0;
            foreach (var e in _chunks.Values) if (e.State == s) n++;
            return n;
        }

        // ============================================================
        // Gizmo — informative, smoothly follows the player
    }
}
