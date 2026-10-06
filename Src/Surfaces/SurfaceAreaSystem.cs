using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

// SurfaceAreaSystem (Modification1 Before GenerateAreasSystem, order 250 = the last of our Mod1 systems).
// Pull model: every frame it reconciles the area decals it owns against the registry and PhasePlan.SurfaceSpans (pieces):
//   none -> spawn (CreationDefinition path), changed -> Node-buffer rewrite + Updated
//   (both verified in game), empty -> delete (a definition made this frame is cancelled instead).
// Hand-over rule: a layer only shrinks / disappears where the covering layer has been applied for >= 1 update.
// Throttles: <= 4 Hz per project, >= 1 m moves, <= 8 rewrites per update globally (nearest project first),
// bypassed on phase changes, NeedsRebuild, ModelReset and geometry changes. Also: node caps, scars/curing, splits.
// Staged traffic: the band kind of a layer comes from PhasePlan.BandOf(layer, view): in a staged C4
// Fresh Asphalt (Cover) is drawn per CHAIN half (SurfaceBand.CarriageHalf, spans PhasePlan.SurfaceSpanHalf); the switch from
// the C3 carriageway bands is spawn-first (old polygons retired once the new ones are on screen). Yellow temporary lines live in
// their own store (EdgeSurf.TempRows, ProcessTempLines): EdgeSection.TempLine rows of the open half, dashed lane lines one area
// per dash, shown only while the half is APPLIED open (EdgeRecord.OpenLanesApplied) and RRWSetting.TempMarkingsOn.
// Crew sections: a layer is a UNION of per-crew-section pieces (PhasePlan.SurfaceSpans / SurfaceSpansHalf). Per
// edge the chain pieces are clipped with ToEdgeLocal, merged where they touch or overlap after the never-drop widening, and
// drawn one area per piece, keyed (edge, layer, band, piece). Same piece count -> each piece diffs like a single strip
// (throttle, hand-over clamp against the coverer AND the row's other pieces). A piece-count or band-kind change rewrites the
// whole row in one update (SurfacePieces.cs): pieces that only grow are rewritten in place, the rest spawn new, and the replaced
// areas stay on screen (EdgeSurf.Outgoing) until their row and the covering layer have covered them for >= 1 update (hand-over rule).
// Bypass work (phase change, ModelReset, NeedsRebuild) is spread over updates per project (whole project at once).
// Structural changes only here (Mod1) and in SurfaceTagSystem (Mod4).
namespace RealisticRoadWorks.V3.Surfaces
{
    [RegisterSystem(SystemUpdatePhase.Modification1, Before = typeof(GenerateAreasSystem), Order = RRWOrder.SurfaceAreas)]
    public partial class SurfaceAreaSystem : GameSystemBase
    {
        // ---- tuning (module-local; Core constants are used wherever they exist)
        const float kRewriteThreshold = 0.5f * RRWConst.kFrontQuantum; // spans are already quantised to 2 m; 1 m absorbs edge-end snapping
        const int kPendingTimeout = 4;          // a definition not bound within this many updates is dropped and re-spawned
        const int kRetireTimeout = 30;          // a replaced area is deleted at the latest after this many updates
        internal const int kDeferTimeout = 60;          // hand-over waits never block longer than this (missing prefab etc.)
        const int kSplitHoldMax = 30;
        const int kResnapDelay = 10;            // re-snap the same span 10 updates after a terrain step (CPU readback lag)
        const int kOrphanSweepInterval = 64;
        const int kStarvedUpdates = 30;         // a project deferred by the global cap this long goes first

        private TerrainSystem m_Terrain;
        private PrefabSystem m_PrefabSystem;
        private EntityArchetype m_DefArchetype;
        private EntityQuery m_OurAreas;
        private EntityQuery m_CreatedEdges;     // edges applied this frame (split remnants of a curing road)
        private readonly RRWGuard m_Guard = new RRWGuard("surfaces areas");

        // per-frame context
        private EntityManager m_Em;
        private uint m_Now;
        private TerrainHeightData m_Hd;
        private bool m_HaveHd;
        private int m_Budget;
        private bool m_TopsoilOn;
        private uint m_LastOrphanSweep;
        private uint m_LastProjSweep;
        private bool m_DevRebuild;

        // reusable buffers (no per-frame allocation)
        private readonly List<float3> m_Poly = new List<float3>(256);
        private readonly List<Entity> m_TmpEntities = new List<Entity>(64);
        private readonly List<Entity> m_Conn = new List<Entity>(8);
        private readonly List<ProjKey> m_ProjOrder = new List<ProjKey>(64);
        private readonly HashSet<Entity> m_JunctionNodes = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Referenced = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ClaimedThisFrame = new HashSet<Entity>();
        private readonly Span[] m_ProjSpan = new Span[(int)SurfaceLayer.Count];             // hull of m_ProjSet (TempMarking rows, logs)
        private readonly Span[,] m_HalfSpan = new Span[(int)SurfaceLayer.Count, 2];          // hull of m_HalfSet (logs)
        // Chain pieces per layer (PhasePlan.SurfaceSpans) and per C4 chain half (SurfaceSpansHalf)
        private readonly SpanSet[] m_ProjSet = new SpanSet[(int)SurfaceLayer.Count];
        private readonly SpanSet[,] m_HalfSet = new SpanSet[(int)SurfaceLayer.Count, 2];
        private readonly SurfaceBand[] m_BandKind = new SurfaceBand[(int)SurfaceLayer.Count]; // PhasePlan.BandOf(layer, view)
        // Desired edge-local pieces per (layer, band slot) of the edge being processed: logical spans (ToEdgeLocal output,
        // merged), only pieces with something to draw (SurfaceGeom.GeometricRange); sorted by s
        private readonly int[,] m_DCount = new int[(int)SurfaceLayer.Count, EdgeSection.kMaxIntervals];
        private readonly float[,,] m_DS0 = new float[(int)SurfaceLayer.Count, EdgeSection.kMaxIntervals, EdgeSurf.kMaxPieces];
        private readonly float[,,] m_DS1 = new float[(int)SurfaceLayer.Count, EdgeSection.kMaxIntervals, EdgeSurf.kMaxPieces];
        private readonly HashSet<Entity> m_Restyled = new HashSet<Entity>();
        private readonly EdgeCtx m_Ctx = new EdgeCtx();
        private int m_FrameCompletedEdges, m_FrameRoadLayersRemoved, m_FrameVergeScars;   // per project, this update (completion log)

        private struct ProjKey
        {
            public float Key;
            public ProjectRecord P;
        }

        private sealed class ProjKeyComparer : IComparer<ProjKey>
        {
            public int Compare(ProjKey a, ProjKey b) => a.Key.CompareTo(b.Key);
        }

        private static readonly ProjKeyComparer s_ByKey = new ProjKeyComparer();   // no per-frame comparer allocation

        // Everything about the edge being processed.
        private sealed class EdgeCtx
        {
            public Entity Edge;
            public EdgeRecord Rec;
            public EdgeSurf Es;
            public ProjectRecord Project;
            public RoadWorksSite Site;
            public RoadWorksRuntime Rt;
            public RoadWorksGround Gr;
            public TerrainProfile Planned;
            public bool PlannedValid;
            public float L;
            public float Floor;
            public bool Heal;          // this update re-verifies that the edge's tracked areas still exist (staggered)
            public ProjectView View;   // the project's view this frame (bands, half spans, open half)
            public bool Reversed;      // the edge curve runs against the chain (+u)
            public bool ModeA;
            public ProjSurf Ps;        // the project's Surfaces state (row stats)
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_DefArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<CreationDefinition>(),
                ComponentType.ReadWrite<Updated>(),
                ComponentType.ReadWrite<Deleted>(),
                ComponentType.ReadWrite<AreaNode>());
            m_OurAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Area>(), ComponentType.ReadOnly<Surface>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_CreatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            SurfaceIntrospection.Register();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            // RRW areas carry LivePath (never saved) and every entity of the old city is gone: forget everything.
            SurfaceState.ClearRuntime("game preload");
            m_Guard.Reset();
            m_LastOrphanSweep = m_LastProjSweep = 0;
        }

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            var gm = Game.SceneFlow.GameManager.instance;
            if (gm == null || gm.gameMode != GameMode.Game) return;
            long t0 = RRWPerf.Start();
            SxPerf.FrameBegin();
            try
            {
                RRWClock.Update(World);
                m_Em = EntityManager;
                m_Now = RRWClock.UpdateIndex;
                var settings = RRWSettings.Current;
                bool on = settings != null && settings.SurfacesOn && SurfaceState.Registered && SurfaceState.CloneEntities.Count > 0 && !SurfaceState.TagFaulted;
                if (!on)
                {
                    if (SurfaceState.SurfacesWereOn && SurfaceState.Registered) DeleteEverything("surfaces off");
                    SurfaceState.SurfacesWereOn = false;
                    m_Guard.Ok();
                    return;
                }
                SurfaceState.SurfacesWereOn = true;
                if (SiteRegistry.Edges.Count == 0 && SurfaceState.Edges.Count == 0 && SurfaceState.Caps.Count == 0
                    && SurfaceState.Scars.Count == 0 && SurfaceState.Retiring.Count == 0 && SurfaceState.Pending.Count == 0
                    && SurfaceState.DevLines.Count == 0 && SurfaceState.DevLineRequests.Count == 0 && SurfaceState.DevLineClear == null)
                {
                    m_Guard.Ok();
                    return;
                }
                m_TopsoilOn = settings.TopsoilOn;
                m_Budget = RRWConst.kAreaRewritesPerUpdateGlobal;
                SxPerf.Begin(SxT.HeightData);
                m_Hd = m_Terrain.GetHeightData();
                SxPerf.End(SxT.HeightData);
                m_HaveHd = m_Hd.isCreated && m_Hd.resolution.x > 2;

                SxPerf.Begin(SxT.Prologue);
                m_DevRebuild = SurfaceState.DevRebuildAll;
                SurfaceState.DevRebuildAll = false;
                DropStalePending();
                // Roundness / LOD bias of the line clones follow the experimental switches; re-batch their areas
                SurfaceStyle.Sync(m_Em, m_Restyled);
                if (m_Restyled.Count > 0) RestyleAreas(m_Restyled);
                ProcessDevLines();
                SxPerf.End(SxT.Prologue);
                SxPerf.Begin(SxT.LostEdges);
                ProcessLostEdges();
                SxPerf.End(SxT.LostEdges);
                m_JunctionNodes.Clear();
                SxPerf.Begin(SxT.Projects);
                ProcessProjects();
                SxPerf.End(SxT.Projects);
                SxPerf.Begin(SxT.Caps);
                ProcessCaps();
                SxPerf.End(SxT.Caps);
                SxPerf.Begin(SxT.ScarConflicts);
                ProcessScarConflicts();   // after the projects: completions added their scars, demolition covers are current
                SxPerf.End(SxT.ScarConflicts);
                SxPerf.Begin(SxT.Scars);
                ProcessScars();
                SxPerf.End(SxT.Scars);
                SxPerf.Begin(SxT.Retiring);
                ProcessRetiring();
                SxPerf.End(SxT.Retiring);
                SxPerf.Begin(SxT.Housekeeping);
                Housekeeping();
                SxPerf.End(SxT.Housekeeping);
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
            finally
            {
                SxPerf.FrameEnd();
                RRWPerf.Stop(PerfSlot.Surfaces, t0);
            }
        }

        // ------------------------------------------------------------------ primitives

        private bool ValidPrefab(Entity prefab)
        {
            if (prefab == Entity.Null || !m_Em.Exists(prefab) || !m_Em.HasComponent<AreaData>(prefab)) return false;
            return m_Em.GetComponentData<AreaData>(prefab).m_Archetype.Valid;
        }

        // Spawns t.Prefab with the polygon in m_Poly (Y already set) through the verified-in-game CreationDefinition path:
        // closed node loop (last == first), Elevation = float.MinValue, Updated + Deleted on the definition.
        private bool SpawnArea(TrackedArea t, List<float3> poly)
        {
            if (poly.Count < 3) return false;
            if (!ValidPrefab(t.Prefab))
            {
                RRWLog.Once("surf-noprefab-" + t.Layer + "-" + t.FadePct, "surfaces: prefab for " + t.Layer + " a" + t.FadePct + " not available (archetype not valid); layer skipped");
                return false;
            }
            SxPerf.Begin(SxT.AreaEcs);
            var arr = m_Em.CreateEntity(m_DefArchetype, 1, Allocator.Temp);
            SxPerf.End(SxT.AreaEcs);
            SxPerf.Count(SxC.Spawns);
            Entity def = arr[0];
            arr.Dispose();
            m_Em.SetComponentData(def, new CreationDefinition
            {
                m_Prefab = t.Prefab,
                m_Flags = CreationFlags.Permanent,
                m_RandomSeed = (int)(math.hash(poly[0]) & 0x7FFFFFFF),
            });
            var buf = m_Em.GetBuffer<AreaNode>(def);
            buf.ResizeUninitialized(poly.Count + 1);
            for (int i = 0; i < poly.Count; i++) buf[i] = new AreaNode(poly[i], float.MinValue);
            buf[poly.Count] = buf[0];
            t.Area = Entity.Null;
            t.Def = def;
            t.DefUpdate = m_Now;
            t.WriteUpdate = m_Now;
            t.FirstXZ = poly[0].xz;
            t.NodeCount = poly.Count;
            t.DeferSince = 0;
            SurfaceState.Pending.Add(t);
            SurfaceState.Spawns++;
            return true;
        }

        // Rewrites the Node buffer of a live area (Updated -> re-triangulated at Mod2B). Verified in game for moving fronts.
        private bool RewriteArea(TrackedArea t, List<float3> poly)
        {
            if (poly.Count < 3 || !t.Live(m_Em) || !m_Em.HasBuffer<AreaNode>(t.Area)) return false;
            var buf = m_Em.GetBuffer<AreaNode>(t.Area);
            buf.ResizeUninitialized(poly.Count);
            for (int i = 0; i < poly.Count; i++) buf[i] = new AreaNode(poly[i], float.MinValue);
            SxPerf.Count(SxC.Rewrites);
            if (!m_Em.HasComponent<Updated>(t.Area))
            {
                SxPerf.Begin(SxT.AreaEcs);
                m_Em.AddComponent<Updated>(t.Area);
                SxPerf.End(SxT.AreaEcs);
            }
            t.WriteUpdate = m_Now;
            t.FirstXZ = poly[0].xz;
            t.NodeCount = poly.Count;
            t.DeferSince = 0;
            SurfaceState.Rewrites++;
            return true;
        }

        // Deletes (or cancels) a tracked area. A definition made THIS update is not consumed yet (GenerateAreasSystem runs
        // after us): destroying it cancels the area. An older unbound one is dropped from the pending list; if its area
        // still appears, SurfaceTagSystem deletes it as an orphan.
        private void DeleteArea(TrackedArea t)
        {
            if (t == null) return;
            if (t.Area != Entity.Null)
            {
                SxPerf.Begin(SxT.AreaEcs);
                EcsUtil.MarkDeleted(m_Em, t.Area);
                SxPerf.End(SxT.AreaEcs);
                SxPerf.Count(SxC.Deletes);
                SurfaceState.Deletes++;
            }
            else if (t.Def != Entity.Null)
            {
                SxPerf.Begin(SxT.AreaEcs);
                if (t.DefUpdate == m_Now && m_Em.Exists(t.Def) && m_Em.HasComponent<CreationDefinition>(t.Def)) m_Em.DestroyEntity(t.Def);
                SxPerf.End(SxT.AreaEcs);
                SurfaceState.Pending.Remove(t);
            }
            t.Area = Entity.Null;
            t.Def = Entity.Null;
        }

        private void Retire(Entity area, TrackedArea successor, int minUpdates = 2)
        {
            if (area == Entity.Null) return;
            SurfaceState.Retiring.Add(new RetireEntry { Area = area, Successor = successor, Since = m_Now, MinUpdates = minUpdates });
        }

        // Pending definitions that never produced an area: drop them so the diff spawns again.
        private void DropStalePending()
        {
            var list = SurfaceState.Pending;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (t.Area != Entity.Null || t.Def == Entity.Null) { list.RemoveAt(i); continue; }
                if (m_Now - t.DefUpdate > kPendingTimeout)
                {
                    RRWLog.Once("surf-unbound", "surfaces: an area definition was not created within " + kPendingTimeout + " updates (" + t.Layer + "); re-spawning");
                    t.Def = Entity.Null;
                    list.RemoveAt(i);
                }
            }
        }

        private void DeleteEverything(string why)
        {
            int n = 0;
            foreach (var es in SurfaceState.Edges.Values)
            {
                for (int l = 0; l < es.Areas.GetLength(0); l++)
                    for (int b = 0; b < es.Areas.GetLength(1); b++)
                        for (int k = 0; k < EdgeSurf.kMaxPieces; k++)
                            if (es.Areas[l, b, k] != null) { DeleteArea(es.Areas[l, b, k]); es.Areas[l, b, k] = null; n++; }
                for (int i = 0; i < es.Outgoing.Count; i++) { DeleteArea(es.Outgoing[i]); n++; }
                es.Outgoing.Clear();
                n += RemoveTempRows(es, null);
            }
            foreach (var dl in SurfaceState.DevLines) foreach (var t in dl.Pieces) { DeleteArea(t); n++; }
            foreach (var c in SurfaceState.Caps.Values) { DeleteArea(c.Cover); DeleteArea(c.Asphalt); n++; }
            foreach (var s in SurfaceState.Scars) { DeleteArea(s.Area); DeleteArea(s.Next); n++; }
            foreach (var r in SurfaceState.Retiring) { EcsUtil.MarkDeleted(m_Em, r.Area); n++; }
            SurfaceState.ClearRuntime(why);
            SweepOrphans(true);
            if (n > 0) RRWLog.Info("surfaces: removed every work-site texture (" + why + ")");
        }

        // ------------------------------------------------------------------ lost edges, splits, combines

        private void ProcessLostEdges()
        {
            m_TmpEntities.Clear();
            foreach (var kv in SurfaceState.Edges)
                if (!SiteRegistry.Edges.ContainsKey(kv.Key)) m_TmpEntities.Add(kv.Key);
            if (m_TmpEntities.Count == 0) return;
            m_ClaimedThisFrame.Clear();
            for (int i = 0; i < m_TmpEntities.Count; i++)
            {
                Entity lost = m_TmpEntities[i];
                var es = SurfaceState.Edges[lost];
                SurfaceState.Edges.Remove(lost);
                if (es.Completed || es.CountTracked() == 0) continue;
                HandleLost(es);
            }
        }

        // A lost edge with new remnants in the same project (split) re-keys its areas to the best-overlapping remnant;
        // the remnant's shrinking writes wait until the siblings' areas are live (no blink). Other overlapping cases
        // (second edge of a combine) retire their areas a few updates later. Everything else is deleted.
        private void HandleLost(EdgeSurf es)
        {
            Entity best = Entity.Null;
            float bestOverlap = 0f;
            RoadWorksSite bestSite = default;
            bool anyOverlap = false;
            if (SiteRegistry.TryGetProject(es.ProjectId, out var proj))
            {
                for (int i = 0; i < proj.Edges.Count; i++)
                {
                    Entity c = proj.Edges[i];
                    if (SurfaceState.Edges.ContainsKey(c) || !m_Em.Exists(c) || !m_Em.HasComponent<RoadWorksSite>(c)) continue;
                    var s = m_Em.GetComponentData<RoadWorksSite>(c);
                    float ov = math.min(es.ChainHi, s.ChainHi) - math.max(es.ChainLo, s.ChainLo);
                    if (ov <= 0.05f) continue;
                    anyOverlap = true;
                    if (m_ClaimedThisFrame.Contains(c)) continue;
                    if (ov > bestOverlap) { bestOverlap = ov; best = c; bestSite = s; }
                }
            }
            if (best != Entity.Null && SiteRegistry.TryGetEdge(best, out var rec) && rec.Arc != null)
            {
                m_ClaimedThisFrame.Add(best);
                var n = new EdgeSurf { Edge = best, ProjectId = es.ProjectId, ChainU0 = bestSite.m_ChainU0, ChainU1 = bestSite.m_ChainU1, Length = rec.Arc.Length };
                for (int l = 0; l < es.Areas.GetLength(0); l++)
                    for (int b = 0; b < es.Areas.GetLength(1); b++)
                        for (int k = 0; k < EdgeSurf.kMaxPieces; k++)
                    {
                        var t = es.Areas[l, b, k];
                        if (t == null) continue;
                        if (!t.Live(m_Em)) { DeleteArea(t); continue; }
                        // logical span re-expressed in the remnant's edge-local s (may reach past its ends: it still covers the siblings)
                        float u0 = PhasePlan.ChainU(t.LS0, es.ChainU0, es.ChainU1, es.Length);
                        float u1 = PhasePlan.ChainU(t.LS1, es.ChainU0, es.ChainU1, es.Length);
                        float a = ChainToS(u0, n.ChainU0, n.ChainU1, n.Length), c2 = ChainToS(u1, n.ChainU0, n.ChainU1, n.Length);
                        t.LS0 = math.min(a, c2);
                        t.LS1 = math.max(a, c2);
                        t.GeomRev = int.MinValue;   // geometry belongs to another edge: rewrite once the hold allows it
                        t.Site = best;
                        EcsUtil.TagDerived(m_Em, t.Area, best, t.ProjectId, t.Group);
                        n.Put(l, b, k, t);      // re-sorted by the remnant's next row compaction (CompactRow)
                    }
                RetireOutgoing(es);         // replaced pieces still on screen: a few more updates, then gone
                RetireTempRows(es, true);   // the remnant spawns its own rows next update; the old ones stay 3 updates (no blink)
                n.SplitSince = m_Now;
                if (proj != null)
                    for (int i = 0; i < proj.Edges.Count; i++)
                    {
                        Entity c = proj.Edges[i];
                        if (c == best || SurfaceState.Edges.ContainsKey(c) || !m_Em.Exists(c) || !m_Em.HasComponent<RoadWorksSite>(c)) continue;
                        var s = m_Em.GetComponentData<RoadWorksSite>(c);
                        if (math.min(es.ChainHi, s.ChainHi) - math.max(es.ChainLo, s.ChainLo) > 0.05f) n.SplitSiblings.Add(c);
                    }
                SurfaceState.Edges[best] = n;
                RRWLog.Verbose("surfaces: split " + RRWLog.E(es.Edge) + " -> areas re-keyed to " + RRWLog.E(best) + " (+" + n.SplitSiblings.Count + " siblings)");
                return;
            }
            for (int l = 0; l < es.Areas.GetLength(0); l++)
                for (int b = 0; b < es.Areas.GetLength(1); b++)
                    for (int k = 0; k < EdgeSurf.kMaxPieces; k++)
                {
                    var t = es.Areas[l, b, k];
                    if (t == null) continue;
                    if (anyOverlap && t.Live(m_Em)) Retire(t.Area, null, 3);   // combine: the merged edge rewrites over it first
                    else DeleteArea(t);
                }
            if (anyOverlap) RetireOutgoing(es);
            else { for (int i = 0; i < es.Outgoing.Count; i++) DeleteArea(es.Outgoing[i]); es.Outgoing.Clear(); }
            RetireTempRows(es, anyOverlap);
        }

        // Unclamped edge-local distance of chain coordinate u.
        private static float ChainToS(float u, float u0, float u1, float len)
        {
            float span = math.abs(u1 - u0);
            if (span <= 1e-3f) return 0f;
            float k = u1 >= u0 ? (u - u0) / span : (u0 - u) / span;
            return k * len;
        }

        // ------------------------------------------------------------------ projects and strips

        // Bypass work (phase change, ModelReset, NeedsRebuild, rrw.surf.rebuild) rewrites every
        // changed area of a project in ONE update. To keep each update below the 2 ms spike budget, at most
        // kBypassProjectsPerUpdate projects do it per update, and a further one only starts while this update's project pass is
        // below kBypassBudgetMs; the first one always runs. A deferred project is skipped WHOLE (its areas keep the previous,
        // consistent state; its one-frame flags are latched in ProjSurf) and goes first in the next update. Never deferred: a
        // project that completes (frame N must clean up), nor one without written areas (!ProjSurf.HasWritten: after a
        // load / for a new project there is no previous state to keep, so it is drawn in its first update even past the budget).
        const int kBypassProjectsPerUpdate = RRWConst.kAreaRewritesPerUpdateGlobal;
        const double kBypassBudgetMs = 1.0;

        private void ProcessProjects()
        {
            m_ProjOrder.Clear();
            bool devRebuild = m_DevRebuild;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                var ps = GetProj(p.Id);
                if (devRebuild) ps.DevRebuildLatched = true;
                float key = float.IsNaN(p.CameraDistance) ? 1e7f : p.CameraDistance;
                if (ps.PendingSince != 0 && m_Now - ps.PendingSince > kStarvedUpdates) key -= 1e8f;
                if (ps.BypassWaitSince != 0) key -= 2e8f;   // deferred bypass work goes first
                m_ProjOrder.Add(new ProjKey { Key = key, P = p });
            }
            m_ProjOrder.Sort(s_ByKey);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int bypassRuns = 0;
            for (int i = 0; i < m_ProjOrder.Count; i++)
            {
                var p = m_ProjOrder[i].P;
                try
                {
                    var ps = GetProj(p.Id);
                    bool phaseChanged = ps.LastPhase != p.Phase || ps.LastKind != p.Kind;
                    if (bypassRuns > 0 && (bypassRuns >= kBypassProjectsPerUpdate || ElapsedMs(start) > kBypassBudgetMs))
                    {
                        ScanProject(p, out bool reset, out bool rebuild, out bool completing);
                        bool heavy = phaseChanged || reset || rebuild || ps.ResetLatched || ps.RebuildLatched || ps.DevRebuildLatched;
                        // A project that has written no area yet (first update after a load - RRW areas carry
                        // LivePath and are never saved - or a new project) has NO previous state to keep: deferring it would show
                        // the bare hidden-road bed (C0-C2) or the full vanilla road (C3/C4, a flicker seen in game). It always runs.
                        if (heavy && ps.HasWritten && !completing && p.Phase != WorksPhase.Complete)
                        {
                            if (reset) ps.ResetLatched = true;
                            if (rebuild) ps.RebuildLatched = true;
                            if (ps.BypassWaitSince == 0) ps.BypassWaitSince = m_Now;
                            SurfaceState.BypassDeferred++;
                            SxPerf.Count(SxC.BypassDeferred);
                            KeepJunctions(p);   // its node caps stay (they are evaluated from the project's current phase)
                            continue;
                        }
                    }
                    if (ps.BypassWaitSince != 0)
                        RRWLog.Verbose("surfaces: project #" + p.Id + " bypass work ran " + (m_Now - ps.BypassWaitSince) + " update(s) late (spread)");
                    bool didBypass = ProcessProject(p, ps, phaseChanged);
                    ps.ResetLatched = ps.RebuildLatched = ps.DevRebuildLatched = false;
                    ps.BypassWaitSince = 0;
                    if (didBypass) bypassRuns++;
                }
                catch (Exception e) { RRWLog.ErrorOnce("surfaces project", e); }
            }
        }

        static double ElapsedMs(long start) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // One-frame runtime flags of a project's edges (only read when the project might be deferred).
        private void ScanProject(ProjectRecord p, out bool reset, out bool rebuild, out bool completing)
        {
            reset = rebuild = completing = false;
            for (int i = 0; i < p.Edges.Count; i++)
            {
                Entity e = p.Edges[i];
                if (!m_Em.Exists(e) || !m_Em.HasComponent<RoadWorksRuntime>(e)) continue;
                var rt = m_Em.GetComponentData<RoadWorksRuntime>(e);
                if (rt.Has(RuntimeFlags.ModelReset)) reset = true;
                if (rt.Has(RuntimeFlags.NeedsRebuild)) rebuild = true;
                if (rt.Has(RuntimeFlags.Completing)) completing = true;
            }
        }

        // A deferred project's works junctions (from the last classification) stay in this update's cap set.
        private void KeepJunctions(ProjectRecord p)
        {
            for (int i = 0; i < p.Edges.Count; i++)
            {
                Entity e = p.Edges[i];
                if (!SurfaceState.Edges.TryGetValue(e, out var es) || !es.EndsValid || es.Completed) continue;
                if (!SiteRegistry.TryGetEdge(e, out var rec) || !m_Em.Exists(e)) continue;
                bool hasEdge = m_Em.HasComponent<Edge>(e);
                if (es.Ends.Start == EndKind.WorksJunction)
                {
                    Entity n = hasEdge ? m_Em.GetComponentData<Edge>(e).m_Start : rec.StartNode;
                    if (n != Entity.Null) m_JunctionNodes.Add(n);
                }
                if (es.Ends.End == EndKind.WorksJunction)
                {
                    Entity n = hasEdge ? m_Em.GetComponentData<Edge>(e).m_End : rec.EndNode;
                    if (n != Entity.Null) m_JunctionNodes.Add(n);
                }
            }
        }

        private ProjSurf GetProj(uint id)
        {
            if (!SurfaceState.Projects.TryGetValue(id, out var ps)) { ps = new ProjSurf(); SurfaceState.Projects.Add(id, ps); }
            return ps;
        }

        // Returns true when the project did bypass work (counted against the per-update bypass spread).
        private bool ProcessProject(ProjectRecord p, ProjSurf ps, bool phaseChanged)
        {
            ps.SeenUpdate = m_Now;
            if (phaseChanged || ps.PhaseSince == 0) ps.PhaseSince = m_Now;
            ps.LastPhase = p.Phase;
            ps.LastKind = p.Kind;
            var v = p.View();
            for (int l = 0; l < (int)SurfaceLayer.Count; l++)
            {
                var layer = (SurfaceLayer)l;
                // The union of per-crew-section pieces (one crew: a single piece over the whole span)
                PhasePlan.SurfaceSpans(layer, v, out m_ProjSet[l]);
                m_ProjSpan[l] = m_ProjSet[l].Hull;
                // Per-half Fresh Asphalt (Cover) in a staged C4 (Core decides: HalvesActive && RRWGates.HalfCovers)
                m_BandKind[l] = PhasePlan.BandOf(layer, v);
                if (m_BandKind[l] == SurfaceBand.CarriageHalf)
                {
                    PhasePlan.SurfaceSpansHalf(layer, v, RoadZones.LeftHalf, out m_HalfSet[l, 0]);
                    PhasePlan.SurfaceSpansHalf(layer, v, RoadZones.RightHalf, out m_HalfSet[l, 1]);
                }
                else
                {
                    m_HalfSet[l, 0] = default;
                    m_HalfSet[l, 1] = default;
                }
                m_HalfSpan[l, 0] = m_HalfSet[l, 0].Hull;
                m_HalfSpan[l, 1] = m_HalfSet[l, 1].Hull;
            }
            LogHalves(p, ps, v);
            LogCrews(p, ps, v);
            bool tickOpen = !ps.HasWritten || m_Now - ps.LastWriteUpdate >= (uint)RRWConst.kAreaMinUpdatesBetweenWrites;
            bool wrote = false, deferred = false, didBypass = false;
            m_FrameCompletedEdges = m_FrameRoadLayersRemoved = m_FrameVergeScars = 0;
            m_FrameLineEdges = m_FrameLineRows = m_FrameLinePieces = 0;
            m_FrameLineWhy = null;
            ps.MaxPieces = 0;
            for (int i = 0; i < p.Edges.Count; i++)
            {
                Entity e = p.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec) || rec.Arc == null) continue;
                if (!m_Em.Exists(e) || !m_Em.HasComponent<RoadWorksSite>(e) || !m_Em.HasComponent<RoadWorksRuntime>(e)) continue;
                var c = m_Ctx;
                c.Edge = e;
                c.Rec = rec;
                c.Project = p;
                c.Site = m_Em.GetComponentData<RoadWorksSite>(e);
                c.Rt = m_Em.GetComponentData<RoadWorksRuntime>(e);
                c.Gr = m_Em.HasComponent<RoadWorksGround>(e) ? m_Em.GetComponentData<RoadWorksGround>(e) : RoadWorksGround.Initial;
                c.PlannedValid = false;
                c.L = rec.Arc.Length;
                c.Floor = SurfaceGeom.FloorMetric(c.Gr);
                c.Heal = ((m_Now + (uint)e.Index) & (uint)(RRWConst.kSelfHealInterval - 1)) == 0u;
                c.View = v;
                c.Reversed = RoadZoneMath.ChainReversed(c.Site.m_ChainU0, c.Site.m_ChainU1);
                c.ModeA = c.Site.Mode == VisualMode.FullDig && p.Mode == VisualMode.FullDig;
                c.Es = GetEdge(e, p.Id, c.Site, c.L);
                c.Ps = ps;
                SxPerf.Count(SxC.Edges);
                ProcessEdge(c, ps, phaseChanged, tickOpen, ref wrote, ref deferred, ref didBypass);
            }
            if (ps.MaxPieces > SurfaceState.MaxPiecesSeen) SurfaceState.MaxPiecesSeen = ps.MaxPieces;
            LogLines(p, ps, v);
            // The write tick closes once all of its writes went out; writes held back by the global cap keep it open, so the
            // rest of this tick's front step follows on the next update instead of 15 updates later.
            if (m_FrameCompletedEdges > 0) LogCompletion(p);
            if (wrote && !(deferred && tickOpen)) { ps.LastWriteUpdate = m_Now; ps.HasWritten = true; }
            if (deferred) { if (ps.PendingSince == 0) ps.PendingSince = m_Now; }
            else ps.PendingSince = 0;
            return didBypass || phaseChanged;
        }

        // One Info line per project when its crew layout changes (for in-game verification: pieces per layer).
        private void LogCrews(ProjectRecord p, ProjSurf ps, in ProjectView v)
        {
            int crews = math.max(v.CrewCount, v.Cancelled ? v.CancelCrewCount : 1);
            if (crews == ps.LastCrews) return;
            bool first = ps.LastCrews < 0;
            ps.LastCrews = crews;
            if (first && crews <= 1) return;
            var sb = m_LogSb;
            sb.Length = 0;
            sb.Append("project #").Append(p.Id).Append(' ').Append(p.Phase).Append(" crews=").Append(v.CrewCount);
            if (v.Cancelled) sb.Append(" cancelCrews=").Append(v.CancelCrewCount);
            sb.Append(" U=").Append(RRWLog.F(v.U)).Append(" layer pieces:");
            for (int l = 0; l < (int)SurfaceLayer.Count; l++)
                if (!m_ProjSet[l].IsEmpty) sb.Append(' ').Append((SurfaceLayer)l).Append('=').Append(m_ProjSet[l].ToString());
            string line = sb.ToString();
            SurfaceState.LastCrews = "u" + m_Now + " " + line;
            RRWLog.Info("surfaces: " + line);
        }

        private readonly System.Text.StringBuilder m_LogSb = new System.Text.StringBuilder(256);

        private EdgeSurf GetEdge(Entity e, uint projectId, in RoadWorksSite site, float length)
        {
            if (!SurfaceState.Edges.TryGetValue(e, out var es))
            {
                es = new EdgeSurf { Edge = e };
                SurfaceState.Edges.Add(e, es);
            }
            es.ProjectId = projectId;
            es.ChainU0 = site.m_ChainU0;
            es.ChainU1 = site.m_ChainU1;
            es.Length = length;
            es.SeenUpdate = m_Now;
            return es;
        }

        private void ProcessEdge(EdgeCtx c, ProjSurf ps, bool phaseChanged, bool tickOpen, ref bool wrote, ref bool deferred, ref bool didBypass)
        {
            var es = c.Es;
            var kind = c.Project.Kind;
            if (c.Rt.Has(RuntimeFlags.Completing) || c.Project.Phase == WorksPhase.Complete)
            {
                if (!es.Completed) CompleteEdge(es, kind);
                return;
            }
            es.Completed = false;   // dev jumps back from Complete respawn the layers
            RefreshEnds(c);
            bool needsRebuild = c.Rt.Has(RuntimeFlags.NeedsRebuild) || ps.RebuildLatched;
            bool rebuild = (needsRebuild && !es.RebuildDone) || ps.DevRebuildLatched;
            es.RebuildDone = needsRebuild;
            bool bypass = phaseChanged || needsRebuild || ps.ResetLatched || c.Rt.Has(RuntimeFlags.ModelReset) || c.Rec.GeometryChangedUpdate == m_Now;
            if (bypass || rebuild) didBypass = true;
            DesiredPieces(c);
            if (es.EndsValid)
            {
                if (es.Ends.Start == EndKind.WorksJunction && c.Rec.StartNode != Entity.Null) m_JunctionNodes.Add(StartNodeOf(c));
                if (es.Ends.End == EndKind.WorksJunction && c.Rec.EndNode != Entity.Null) m_JunctionNodes.Add(EndNodeOf(c));
            }
            for (int l = 0; l < (int)SurfaceLayer.Count; l++)
            {
                var layer = (SurfaceLayer)l;
                if (layer == SurfaceLayer.TempMarking)
                {
                    // Yellow lines never live in the band slots (the TempRows store holds them): drop any leftover there
                    for (int b = 0; b < EdgeSection.kMaxIntervals; b++)
                    {
                        for (int k = 0; k < es.RowN[l, b]; k++)
                            if (es.Areas[l, b, k] != null) { DeleteArea(es.Areas[l, b, k]); es.Areas[l, b, k] = null; }
                        es.RowN[l, b] = 0;
                    }
                    continue;
                }
                // While a layer switches its band kind every row of it is written in this update (no throttle), so the
                // old polygons never go before all new bands exist (e.g. the right half spawned a tick after the left one)
                bool switching = false;
                for (int b = 0; b < EdgeSection.kMaxIntervals && !switching; b++)
                    for (int k = 0; k < es.RowN[l, b]; k++)
                    {
                        var t = es.Areas[l, b, k];
                        if (t != null && t.BandKind != m_BandKind[l]) { switching = true; break; }
                    }
                for (int b = 0; b < EdgeSection.kMaxIntervals; b++)
                {
                    try { DiffRow(c, layer, b, m_BandKind[l], rebuild, bypass || switching, tickOpen, ref wrote, ref deferred); }
                    catch (Exception e) { RRWLog.ErrorOnce("surfaces diff " + layer, e); }
                }
            }
            try { ProcessOutgoing(c); }
            catch (Exception e) { RRWLog.ErrorOnce("surfaces outgoing", e); }
            SxPerf.Begin(SxT.TempLines);
            try { ProcessTempLines(c, rebuild, bypass, tickOpen, ref wrote, ref deferred); }
            catch (Exception e) { RRWLog.ErrorOnce("surfaces temp lines", e); }
            SxPerf.End(SxT.TempLines);
            if (es.SplitSiblings.Count > 0 && m_Now - es.SplitSince > kSplitHoldMax) es.SplitSiblings.Clear();
        }

        private Entity StartNodeOf(EdgeCtx c) => m_Em.HasComponent<Edge>(c.Edge) ? m_Em.GetComponentData<Edge>(c.Edge).m_Start : c.Rec.StartNode;
        private Entity EndNodeOf(EdgeCtx c) => m_Em.HasComponent<Edge>(c.Edge) ? m_Em.GetComponentData<Edge>(c.Edge).m_End : c.Rec.EndNode;

        // Junction-end classification (+ own trims), refreshed on geometry / registry changes and every 8 updates (staggered).
        private void RefreshEnds(EdgeCtx c)
        {
            var es = c.Es;
            bool need = !es.EndsValid || es.GeomRev != c.Rec.GeometryRevision || es.EndsRegistryRev != SiteRegistry.Revision
                        || ((m_Now + (uint)c.Edge.Index) & 7u) == 0u;
            if (!need) return;
            SxPerf.Begin(SxT.RefreshEnds);
            SxPerf.Count(SxC.EndsRefresh);
            Entity sn = StartNodeOf(c), en = EndNodeOf(c);
            var ends = es.Ends;
            ends.Start = SurfaceGeom.ClassifyEnd(m_Em, c.Edge, sn, out ends.CutStart);
            ends.End = SurfaceGeom.ClassifyEnd(m_Em, c.Edge, en, out ends.CutEnd);
            uint ch = SurfaceGeom.CornerHash(m_Em, c.Edge);
            if (!es.EndsValid || ch != es.CornerHash || es.GeomRev != c.Rec.GeometryRevision)
            {
                SurfaceGeom.MeasureTrims(m_Em, c.Edge, c.Rec.Arc, out ends.TrimStart, out ends.TrimEnd);
                es.CornerHash = ch;
            }
            ends.Hash = SurfaceGeom.HashEnds(ends);
            es.Ends = ends;
            es.EndsValid = true;
            es.GeomRev = c.Rec.GeometryRevision;
            es.EndsRegistryRev = SiteRegistry.Revision;
            SxPerf.End(SxT.RefreshEnds);
        }

        private TerrainProfile Planned(EdgeCtx c)
        {
            if (!c.PlannedValid)
            {
                c.Planned = PhasePlan.Terrain(PlanInput.From(c.Site, c.Rt));
                c.PlannedValid = true;
            }
            return c.Planned;
        }

        // Builds the strip polygon of (layer, band) over the logical span into m_Poly, Y set by the vertex-Y rule.
        // The band kind is explicit (PhasePlan.BandOf(layer, view)): CarriageHalf i = 0 chain-left / 1 chain-right half.
        private bool BuildStrip(EdgeCtx c, SurfaceLayer layer, int band, SurfaceBand kind, float ls0, float ls1, out float gs0, out float gs1)
        {
            SxPerf.Begin(SxT.BuildStrip);
            SxPerf.Count(SxC.Strips);
            bool ok = BuildStripCore(c, layer, band, kind, ls0, ls1, out gs0, out gs1);
            SxPerf.End(SxT.BuildStrip);
            return ok;
        }

        private bool BuildStripCore(EdgeCtx c, SurfaceLayer layer, int band, SurfaceBand kind, float ls0, float ls1, out float gs0, out float gs1)
        {
            if (!SurfaceGeom.GeometricRange(layer, ls0, ls1, c.L, c.Es.Ends, out gs0, out gs1, out var cutS, out var cutE,
                                            out bool rs, out bool re, out bool ts, out bool te))
            {
                m_Poly.Clear();
                return false;
            }
            c.Rec.Section.Band(kind, layer, band, c.Reversed, RoadZones.None, false, out float left, out float right);
            // a C4 half's inner edge (at the split) carries no margin (EdgeSection.HalfBand): never taper it away
            bool half = kind == SurfaceBand.CarriageHalf && c.Rec.Section.TwoDirections;
            bool innerRight = half && ((band == 0) != c.Reversed);
            var lat = new BandLat
            {
                Left = left, Right = right, Margin = PhasePlan.MarginOf(layer),
                TaperStart = ts, TaperEnd = te, TaperS0 = c.Es.Ends.TrimStart, TaperS1 = c.Es.Ends.TrimEnd,
                MinWidth = 0f, NoMarginRight = innerRight, NoMarginLeft = half && !innerRight,
            };
            SurfaceGeom.Strip(c.Rec.Arc, gs0, gs1, lat, cutS, cutE, rs, re, m_Poly);
            return ApplyVertexY(c);
        }

        private bool ApplyVertexY(EdgeCtx c)
        {
            if (m_Poly.Count < 3) return false;
            SxPerf.Count(SxC.Vertices, m_Poly.Count);
            var planned = Planned(c);
            for (int i = 0; i < m_Poly.Count; i++)
            {
                float3 p = m_Poly[i];
                p.y = SurfaceGeom.VertexY(ref m_Hd, m_HaveHd, c.Rt, c.Gr, planned, p);
                m_Poly[i] = p;
            }
            return true;
        }

        private void StampWrite(EdgeCtx c, TrackedArea t, float ls0, float ls1, float gs0, float gs1)
        {
            t.HasPrev = t.BoundUpdate != 0 || t.Area != Entity.Null;
            t.PrevLS0 = t.LS0; t.PrevLS1 = t.LS1;
            t.LS0 = ls0; t.LS1 = ls1;
            t.GS0 = gs0; t.GS1 = gs1;
            t.GeomRev = c.Rec.GeometryRevision;
            t.EndsHash = c.Es.Ends.Hash;
            t.FloorAtWrite = c.Floor;
            t.StepAtWrite = c.Gr.m_StepUpdate;
            t.ResnapDue = 0;
        }

        private TrackedArea NewStrip(EdgeCtx c, SurfaceLayer layer, int band, SurfaceBand kind) => new TrackedArea
        {
            Layer = layer,
            Band = band,
            BandKind = kind,
            Prefab = SurfaceState.Prefab(layer, 100),
            Site = c.Edge,
            ProjectId = c.Project.Id,
            Group = DerivedGroup.Area,
        };

        static bool Touches(float v, float end) => math.abs(v - end) <= SurfaceGeom.kTouch;

        // ------------------------------------------------------------------ completion -> scars

        // Construction layers that may outlive the works as a (fast-fading) scar: the verge soil only.
        internal static bool IsVergeLayer(SurfaceLayer l) => l == SurfaceLayer.TopsoilStrip || l == SurfaceLayer.Subgrade;

        // A completed construction must never keep a road-surface layer (fresh asphalt, covers, temp marking): defence in
        // depth on top of Core's PhasePlan.BecomesScar, so a later Core change cannot bring a dark curing layer back.
        internal static bool MayBecomeScar(WorksKind kind, SurfaceLayer l) =>
            PhasePlan.BecomesScar(kind, l) && PrefabNames.HasAlphaVariants(l) && (kind != WorksKind.Construction || IsVergeLayer(l));

        // Frame N of Completing: the layers MayBecomeScar keeps turn into fading scars
        // (RRWDerived{site = Null, project, Scar}); everything else - for a construction the fresh asphalt, both covers and
        // the yellow temporary lines - is deleted in this same frame, so the finished road looks vanilla the moment it is
        // handed back (no curing layer). Construction verge scars fade out and are deleted at kVergeEndHours (~1 h).
        // The scar list survives the site.
        private void CompleteEdge(EdgeSurf es, WorksKind kind)
        {
            int scars = 0, removed = 0;
            // Replaced pieces still waiting for their hand-over duplicate a current piece: gone with the frame-N cleanup
            for (int i = 0; i < es.Outgoing.Count; i++) DeleteArea(es.Outgoing[i]);
            es.Outgoing.Clear();
            for (int l = 0; l < es.Areas.GetLength(0); l++)
                for (int b = 0; b < es.Areas.GetLength(1); b++)
                {
                    int rowN = es.RowN[l, b];
                    es.RowN[l, b] = 0;
                    for (int k = 0; k < rowN; k++)
                {
                    var t = es.Areas[l, b, k];
                    if (t == null) continue;
                    es.Areas[l, b, k] = null;
                    var layer = (SurfaceLayer)l;
                    if (t.Live(m_Em) && MayBecomeScar(kind, layer))
                    {
                        // construction verges belong to the finished road (removed when it is worked on / deleted /
                        // replaced); the demolition scar has no anchor and outlives the deleted edge
                        bool constr = kind == WorksKind.Construction;
                        AddScar(t, constr, constr ? es.Edge : Entity.Null, false);
                        scars++;
                    }
                    else
                    {
                        DeleteArea(t);
                        if (!IsVergeLayer(layer)) removed++;
                    }
                }
                }
            removed += RemoveTempRows(es, "completed");   // yellow lines go in frame N with the covers (never a scar)
            es.Completed = true;
            es.SplitSiblings.Clear();
            m_FrameCompletedEdges++;
            m_FrameRoadLayersRemoved += removed;
            m_FrameVergeScars += scars;
            SurfaceState.CompletedEdges++;
            SurfaceState.CompletedRoadLayers += removed;
            if (kind == WorksKind.Construction) SurfaceState.CompletedVergeScars += scars;
            RRWLog.Verbose("surfaces: edge " + RRWLog.E(es.Edge) + " completed (" + kind + "): " + removed + " road layer(s) removed, " + scars + " scar(s) fade");
        }

        // One Info line per project and completion frame (for in-game verification: "no curing layer, verges gone in 1 h").
        private void LogCompletion(ProjectRecord p)
        {
            string line = "project #" + p.Id + " " + p.Kind + " completed on " + m_FrameCompletedEdges + " edge(s): "
                          + m_FrameRoadLayersRemoved + " road-surface area(s) removed in this frame (no curing layer), "
                          + m_FrameVergeScars + (p.Kind == WorksKind.Construction
                              ? " verge scar(s) fade out by " + RRWLog.F(RRWConst.kVergeEndHours) + " in-game h"
                              : " demolition scar(s) fade out by " + RRWLog.F(RRWConst.kScarEndHours) + " in-game h");
            SurfaceState.LastCompletion = "u" + m_Now + " " + line;
            RRWLog.Info("surfaces: " + line);
        }
    }
}
