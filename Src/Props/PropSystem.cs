using System;
using System.Collections.Generic;
using Colossal.Collections;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.SceneFlow;
using Game.Simulation;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Bezier4x3 = Colossal.Mathematics.Bezier4x3;
using Bounds2 = Colossal.Mathematics.Bounds2;
using CMath = Colossal.Mathematics.MathUtils;
using NetSearchSystem = Game.Net.SearchSystem;
using ObjElevation = Game.Objects.Elevation;
using ObjSubObject = Game.Objects.SubObject;
using ObjTransform = Game.Objects.Transform;

namespace RealisticRoadWorks.V3.Props
{
    // PropSystem (Modification1, order 230, after Director / Traffic, before Machines / Surfaces).
    // Static work-site props in FIXED CHAIN SLOTS keyed by (project, kind, sub, k): end barriers (with blinking beacons),
    // survey / edge cones, spoil / dump heaps and stone windrows that grow and shrink through their fill (never pop),
    // rubble, crew props. Each slot's fill comes from PhasePlan (Core); this system diffs it against the entities it owns:
    //   fill < 0 -> no entity (delete);  fill >= 1 -> entity exists (spawn) with RRWHeapFill = fill.
    // Replacement entities are created in the same frame the old ones are deleted. Splits never touch props (chain keys).
    // Placement: verge props stay OnGround (vanilla follows the terrain); every prop whose Y we set gets
    // Elevation{0, 0} and is re-placed when its edge's floor changes (RoadWorksGround.m_StepUpdate, + a delayed re-snap).
    // Owner is never set at creation (a failure mode seen in game): PropOwnerSystem adds it at Mod4 of the same update.
    // Further rules:
    //  - no prop outside [TrimU0, TrimU1] (only the hidden dead-end cap line), none inside a non-works road, and the
    //    whole project is torn down in the frame Releasing / Completing / Complete first appears;
    //  - a change of a chain-end node's non-works edges (a road was connected) respawns that end's barrier group
    //    and the crew props (new first, same frame) with the junction layout; Overridden props that stand inside a non-works
    //    road are deleted (their slot is skipped while the road is there) instead of being made visible again;
    //  - site / kerb fences, WorksHalf barrier lines, centre cones (+ approach signals behind a setting and switch).
    [RegisterSystem(SystemUpdatePhase.Modification1, Before = typeof(Game.Tools.GenerateAreasSystem), Order = RRWOrder.Props)]
    public partial class PropSystem : GameSystemBase
    {
        private const int kMaxSpawnsPerUpdate = 2000;   // after a load every project spawns at once; cap pathological cities
        private const int kResnapDelayUpdates = 10;     // CPU heightmap readback lag after a terrain step
        private const float kMoveEpsilon = 0.01f;
        private const int kHealDiffsPerUpdate = 1;      // full diffs asked for ONLY by the self-heal, per update
        private const int kHealMaxDefer = 8;            // ... a project waits at most this many updates for its turn
        private int m_FullDiffsThisUpdate;

        private readonly RRWGuard m_Guard = new RRWGuard("props");
        private PrefabSystem m_PrefabSystem;
        private TerrainSystem m_TerrainSystem;
        private EntityQuery m_OverriddenQuery;
        private EntityQuery m_VfxQuery;
        private NetSearchSystem m_NetSearch;
        private Game.Rendering.CameraUpdateSystem m_Camera;
        private float3 m_Cam;
        private bool m_HaveCam;

        // ---- prefab cache (cleared on preload; re-resolved when RRWGates.Revision changes: fence variant, ...)
        private bool m_PrefabsResolved;
        private int m_GateRevision = int.MinValue;
        private Entity[] m_Safety, m_Concrete, m_Cones, m_Rubble, m_Crew;
        private Entity m_TallCone, m_Ore, m_Stone, m_Beacon, m_DustOre, m_DustStone;
        private float m_SafetyPitch = 1.88f, m_ConcretePitch = 1.88f, m_KerbPitch = 1.88f;
        // site fences; kerb fences from PrefabNames.KerbFence(RRWGates.KerbFenceVariant, short)
        private Entity[] m_SiteFence = new Entity[0], m_SiteFenceShort = new Entity[0], m_KerbFence = new Entity[0], m_KerbFenceShort = new Entity[0];
        private float m_SiteFencePitch = RRWConst.kFenceSpacing, m_KerbFencePitch = RRWConst.kFenceSpacing, m_SiteShortLen = 2f, m_KerbShortLen = 2f;
        private readonly List<ForeignEdge> m_ScratchForeign = new List<ForeignEdge>(8);
        private readonly Dictionary<Entity, PrefabInfo> m_PrefabInfo = new Dictionary<Entity, PrefabInfo>();

        private struct PrefabInfo
        {
            public bool Valid;        // has ObjectData with a valid archetype
            public float3 Size;
            public float3 Centre;     // ObjectGeometryData.m_Bounds centre (local; pivot correction of chord-placed panels)
            public bool Quantity;
            public uint StepMask;
            public Game.Objects.GeometryFlags Flags;   // dev: Overridable / OccupyZone ... (collision class)
        }

        // ---- per-update scratch (no per-frame allocations)
        private TerrainHeightData m_Height;
        private bool m_HeightTried, m_HeightValid;
        private int m_SpawnBudget;
        private bool m_BeaconsOn, m_DustOn;
        private uint m_Update;
        private readonly List<ProjectRecord> m_Sorted = new List<ProjectRecord>(64);
        private readonly List<uint> m_RemoveProjects = new List<uint>(16);
        private readonly List<int> m_RemoveKeys = new List<int>(64);
        private readonly List<Entity> m_TmpEdges = new List<Entity>(8);

        // What one slot wants this pass.
        private struct Want
        {
            public int Key;
            public PropKind Kind;
            public float U;              // chain u
            public float Lateral;        // metres; edge frame unless LatChain
            public bool LatChain;        // Lateral is in the chain frame (converted with the edge's direction sign)
            public YMode Y;
            public RotMode Rot;
            public float YawExtra;       // degrees
            public float JitterDeg;
            public bool ChainFacing;     // Tangent yaw follows the chain direction (else the curve direction)
            public Entity Prefab;
            public Entity DustPrefab;    // dust-emitting alternative of a heap slot (Null if none)
            public bool DustSlot;        // this slot index is a dust slot (kHeapDustEvery)
            public int Fill;             // 1..255
            public ushort Seed;          // 0 = derive from project seed + key
            public byte Variant;
            public Entity OwnerNode;     // Null = start node of the slot's edge, else end node
            public DerivedGroup Group;
            public bool AllowOutside;    // may stand outside [TrimU0, TrimU1] (hidden dead-end cap line only)
            public bool NoOwner;         // never gets a late Owner (amber head: never a node sub-object)
            // world (chord) placement of fence panels (RotMode.Chord)
            public bool World;           // WorldPos / WorldDir decide the transform (U / Lateral stay for keys and checks)
            public float3 WorldPos;      // chord midpoint, Y already final (min of both feet - kPanelDrop + lift)
            public float3 WorldDir;      // chord direction (foot A -> foot B)
            public float3 FootA, FootB;  // both feet on the curve (rrw.props.check)
            public float Lift;
            public bool Pivot;           // subtract the bounds centre (ObjectGeometryData.m_Bounds) so the PANEL is centred on the chord
        }

        // Net search tree iterator (non-works roads under a prop).
        private struct BoundsIter : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds2 Box;
            public NativeList<Entity> Hits;
            public bool Intersect(QuadTreeBoundsXZ bounds) => CMath.Intersect(bounds.m_Bounds.xz, Box);
            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (CMath.Intersect(bounds.m_Bounds.xz, Box) && Hits.Length < 64) Hits.Add(item);
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_OverriddenQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RRWProp>(), ComponentType.ReadOnly<Overridden>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_VfxQuery = GetEntityQuery(ComponentType.ReadOnly<VFXData>());
            m_NetSearch = World.GetOrCreateSystemManaged<NetSearchSystem>();
            m_Camera = World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
            RRWIntrospection.RegisterDumper("Props", Dump);
            RRWIntrospection.RegisterChecker("Props", Check);
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            PropState.Clear();
            m_PrefabsResolved = false;
            m_PrefabInfo.Clear();
            m_FenceCache.Clear();
            m_Guard.Reset();
            DustVfx.Reset();   // VFXSystem rebuilds its table for every load: re-verify the dust clone
            DevPreload();
        }

        // DEVTOOLS builds (PropsDevFx.cs): rrw.props.fx / rrw.props.sig requests, run at Modification1. Absent in release.
        partial void DevUpdate();
        partial void DevPreload();

        protected override void OnUpdate()
        {
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game) return;
            if (m_Guard.Faulted) return;
            long t = RRWPerf.Start();
            PxPerf.FrameBegin();
            try
            {
                Run();
                m_Guard.Ok();
            }
            catch (Exception e)
            {
                m_Guard.Fail(e);
            }
            finally
            {
                PxPerf.FrameEnd();
                RRWPerf.Stop(PerfSlot.Props, t);
            }
        }

        // ------------------------------------------------------------------ frame

        private void Run()
        {
            RRWClock.Update(World);
            m_Update = RRWClock.UpdateIndex;
            m_HeightTried = false;
            m_FullDiffsThisUpdate = 0;
            // Props is the single writer of the dust source Machines' dusty excavator follows
            RRWPrefabRegistry.DustVanilla = DustVfx.UseVanilla;
            PxPerf.Begin(PxT.HealOverridden);
            HealOverridden();
            PxPerf.End(PxT.HealOverridden);
            DevUpdate();
            if (SiteRegistry.Projects.Count == 0 && PropState.Projects.Count == 0) return;
            CheckDustVfx();
            if (PropState.DustReresolve) { PropState.DustReresolve = false; m_PrefabsResolved = false; }
            if (m_GateRevision != RRWGates.Revision)
            {
                // a gate changed (rrw.gate): re-resolve (fence variant) and re-diff every project in this update (ProcessKey
                // carries the revision), so old and new devices swap in one frame
                if (m_GateRevision != int.MinValue) RRWLog.Info("props: gates changed: " + RRWGates.Describe());
                m_GateRevision = RRWGates.Revision;
                m_PrefabsResolved = false;
            }
            if (!m_PrefabsResolved) ResolvePrefabs();

            var settings = RRWSettings.Current;
            bool propsOn = settings != null ? settings.PropsOn : RRWDebug.On(DebugLayers.Props);
            m_BeaconsOn = settings == null || settings.BeaconsOn;
            m_DustOn = settings != null && settings.DustOn;
            m_ApproachOn = settings != null && settings.ApproachSignalsOn;   // = setting && RRWGates.AmberHead && PropsOn
            m_SpeedKmh = settings != null ? settings.WorkZoneSpeedKmh : 30;
            m_Na = RRWCity.Known && RRWCity.NaTheme;
            m_LampSites = 0;
            m_SpawnBudget = kMaxSpawnsPerUpdate;

            m_Sorted.Clear();
            if (propsOn)
                foreach (var kv in SiteRegistry.Projects) m_Sorted.Add(kv.Value);
            m_Cam = CameraPivot(out m_HaveCam);
            PxPerf.Begin(PxT.CamDist);
            for (int i = 0; i < m_Sorted.Count; i++)
            {
                var p = m_Sorted[i];
                if (!PropState.Projects.TryGetValue(p.Id, out var pp))
                {
                    pp = new ProjectProps { Id = p.Id };
                    PropState.Projects.Add(p.Id, pp);
                }
                pp.CamDist = ChainDistance(p, pp);
            }
            PxPerf.End(PxT.CamDist);
            SortByDistance(m_Sorted);

            int budgetUsed = 0;
            PxPerf.Begin(PxT.Projects);
            for (int i = 0; i < m_Sorted.Count; i++)
            {
                var p = m_Sorted[i];
                var pp = PropState.Projects[p.Id];
                PxPerf.Count(PxC.Projects);
                try { ProcessProject(p, pp, ref budgetUsed); }
                catch (Exception e)
                {
                    pp.LastKey = 0;   // retry next update
                    pp.FillCacheValid = false;
                    RRWLog.ErrorOnce("props project", e);
                }
            }
            PxPerf.End(PxT.Projects);
            PropState.DevRespawnAll = false;
            PropState.DevRespawnProject = 0;

            // tear down projects that left the registry (completion, cancel, load) or everything when the layer is off
            PxPerf.Begin(PxT.Teardown);
            m_RemoveProjects.Clear();
            foreach (var kv in PropState.Projects)
                if (!propsOn || !SiteRegistry.Projects.ContainsKey(kv.Key)) m_RemoveProjects.Add(kv.Key);
            for (int i = 0; i < m_RemoveProjects.Count; i++)
            {
                var pp = PropState.Projects[m_RemoveProjects[i]];
                int n = TearDown(pp);
                PropState.Projects.Remove(m_RemoveProjects[i]);
                m_FenceCache.Remove(m_RemoveProjects[i]);
                if (n > 0) RRWLog.Verbose("props: project #" + pp.Id + " torn down (" + n + " props)");
            }
            PxPerf.End(PxT.Teardown);

            int total = 0;
            foreach (var kv in PropState.Projects) total += kv.Value.Slots.Count;
            PropState.TotalProps = total;
        }

        // Nearest projects first (insertion sort: few projects, no allocation). Distance = camera to the nearest chain point.
        private static void SortByDistance(List<ProjectRecord> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                var x = list[i];
                float dx = Dist(x);
                int j = i - 1;
                while (j >= 0 && Dist(list[j]) > dx) { list[j + 1] = list[j]; j--; }
                list[j + 1] = x;
            }
        }

        private static float Dist(ProjectRecord p) =>
            PropState.Projects.TryGetValue(p.Id, out var pp) && pp.CamDist >= 0f ? pp.CamDist : p.CameraDistance;

        // Found in game: a far rule on ProjectRecord.CameraDistance (the distance to the MAIN FRONT) breaks. At
        // every phase boundary the front jumps from the chain end (u = U) back to the start (u = 0), so on an 816 m chain a
        // camera watching the chain end saw the project turn "far" in the reveal frame (cam=816 m) and every heap, survey cone
        // and crew prop vanish at once. Props now measure the XZ distance from the camera pivot to the NEAREST point of the
        // chain (17 samples per edge; < 0.5 m error on a 164 m edge vs the 600 m threshold), which is continuous over phases.
        // The sample points are cached per project (rebuilt when the chain, the registry or an edge's geometry
        // changes), so a frame costs one dictionary lookup per edge and 17 float2 distances instead of 17 arc evaluations.
        // This is the far / LOD rule with several crews too: the nearest chain point, independent of which crew
        // ProjectRecord.CameraDistance (the focus crew's front) follows.
        private float ChainDistance(ProjectRecord p, ProjectProps pp)
        {
            if (!m_HaveCam) return 0f;   // no camera (tests, load): treat as near, never thin by distance
            const int n = 16;
            uint key = Mix(Mix((uint)p.Revision, (uint)SiteRegistry.Revision), (uint)p.Edges.Count);
            for (int i = 0; i < p.Edges.Count; i++)
                key = SiteRegistry.TryGetEdge(p.Edges[i], out var r0) && r0.Arc != null ? Mix(key, Mix((uint)p.Edges[i].Index, (uint)r0.GeometryRevision)) : Mix(key, 0x9E3779B9u);
            if (key != pp.CamSampleKey || pp.CamSampleCount == 0)
            {
                pp.CamSampleKey = key;
                pp.CamSampleCount = 0;
                int need = p.Edges.Count * (n + 1);
                if (pp.CamSamples.Length < need) pp.CamSamples = new float2[need];
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    if (!SiteRegistry.TryGetEdge(p.Edges[i], out var r) || r.Arc == null) continue;
                    var arc = r.Arc;
                    for (int k = 0; k <= n; k++) pp.CamSamples[pp.CamSampleCount++] = arc.Position(arc.Length * k / n).xz;
                }
            }
            float best = float.MaxValue;
            float2 c = m_Cam.xz;
            for (int i = 0; i < pp.CamSampleCount; i++)
            {
                float d = math.distancesq(pp.CamSamples[i], c);
                if (d < best) best = d;
            }
            return best == float.MaxValue ? -1f : math.sqrt(best);
        }

        private float3 CameraPivot(out bool ok)
        {
            ok = false;
            try
            {
                var c = m_Camera != null ? m_Camera.gamePlayController : null;
                if (c != null)
                {
                    var v = c.pivot;
                    ok = true;
                    return new float3(v.x, v.y, v.z);
                }
                if (m_Camera != null) { ok = true; return m_Camera.position; }
            }
            catch (Exception e) { RRWLog.ErrorOnce("props camera", e); }
            return default;
        }

        // Dust clone check: verify the "RRW Dust VFX" clone against VFXSystem's table once per load (retry every 30 updates until the
        // table exists); a failed check switches the dust heaps to the vanilla-effect carriers in place (slot prefab swap,
        // new entity first). Logged once per load: "props: dust vfx check ...".
        private void CheckDustVfx()
        {
            if (DustVfx.State != 0 || m_Update < DustVfx.NextTry) return;
            if (DustVfx.Attempts >= DustVfx.kMaxAttempts) return;
            DustVfx.NextTry = m_Update + 30u;
            if (!DustVfx.Check(World, m_PrefabSystem, m_VfxQuery))
            {
                if (DustVfx.Attempts >= DustVfx.kMaxAttempts) RRWLog.Warn("props: dust vfx check gave up: " + DustVfx.Report + " (source=" + DustVfx.ModeName + ")");
                return;
            }
            if (DustVfx.Mode != DustSource.Auto)
                RRWLog.Info("props: dust vfx check " + DustVfx.Report + " (source=" + DustVfx.ModeName + ": dust heaps use " +
                            (DustVfx.Mode == DustSource.Vanilla ? "vanilla " + PrefabNames.DustSmallVfx + " carriers" : PrefabNames.DustVfx) + ")");
            else if (DustVfx.State > 0) RRWLog.Info("props: dust vfx check " + DustVfx.Report + " -> dust heaps use " + PrefabNames.DustVfx);
            else RRWLog.Warn("props: dust vfx check " + DustVfx.Report + " -> dust heaps switched to vanilla " + PrefabNames.DustSmallVfx + " carriers");
            if (DustVfx.Mode == DustSource.Auto) PropState.DustReresolve = true;
        }

        // Overridden self-heal (v2 KeepPropsVisible pattern): every update, but the query is empty almost always.
        // Vanilla OverrideSystem overrides our props when a player builds a road over them
        // (they are Overridable, so the road builds). A prop that now stands inside a NON-works road is deleted while it is
        // still hidden (no visible frame) and the road joins the project's foreign list, so its slot is skipped from now on
        // (the layout "moves out of the road"); every other overridden prop is made visible again as before.
        private void HealOverridden()
        {
            if (m_OverriddenQuery.IsEmptyIgnoreFilter) return;
            var arr = m_OverriddenQuery.ToEntityArray(Allocator.Temp);
            int healed = 0, deleted = 0;
            try
            {
                var em = EntityManager;
                for (int i = 0; i < arr.Length; i++)
                {
                    Entity e = arr[i];
                    bool gone = false;
                    try { gone = DeleteIfInsideForeign(em, e); }
                    catch (Exception ex) { RRWLog.ErrorOnce("props overridden foreign check", ex); }
                    if (gone) { deleted++; continue; }
                    em.RemoveComponent<Overridden>(e);
                    if (!em.HasComponent<BatchesUpdated>(e)) em.AddComponent<BatchesUpdated>(e);
                    healed++;
                }
                if (healed > 0) RRWLog.Verbose("props: " + healed + " overridden props made visible again");
                if (deleted > 0) RRWLog.Info("props: " + deleted + " overridden props stood inside a non-works road: deleted, their slots are skipped while that road exists");
            }
            finally { arr.Dispose(); }
        }

        private bool DeleteIfInsideForeign(EntityManager em, Entity e)
        {
            if (!em.HasComponent<RRWProp>(e) || !em.HasComponent<ObjTransform>(e)) return false;
            var rp = em.GetComponentData<RRWProp>(e);
            if (!PropState.Projects.TryGetValue(rp.m_ProjectId, out var pp)) return false;
            if (!pp.Slots.TryGetValue(rp.m_Key, out var slot) || slot.Entity != e) return false;
            var tr = em.GetComponentData<ObjTransform>(e);
            var size = Info(slot.Prefab).Size;
            int added = ScanForeignNear(em, tr.m_Position, size, pp.Foreign, false);
            if (!InsideForeign(pp.Foreign, tr.m_Position, tr.m_Rotation, size)) return false;
            EcsUtil.MarkDeleted(em, e);
            slot.Entity = Entity.Null;      // the next diff skips the slot (inside a foreign road) and drops it
            pp.Deleted++;
            pp.HealDeleted++;
            pp.LastKey = 0;                 // re-diff this update (the rest of the layout re-checks against the new road)
            if (added > 0) RRWLog.Info("props: project #" + pp.Id + " " + PropKeys.Name(rp.m_Key) + " stood inside a non-works road (" + added + " road(s) added to the foreign list)");
            return true;
        }

        // ------------------------------------------------------------------ foreign roads

        // A road edge that is not one of ours and could collide with a prop: alive, permanent, a valid composition.
        private bool IsForeignEdge(EntityManager em, Entity e)
        {
            if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e) || em.HasComponent<Game.Tools.Temp>(e)) return false;
            if (!em.HasComponent<Edge>(e) || !em.HasComponent<Curve>(e) || !em.HasComponent<Composition>(e)) return false;
            if (SiteRegistry.Edges.ContainsKey(e)) return false;
            return EcsUtil.ValidComposition(em, em.GetComponentData<Composition>(e).m_Edge);
        }

        private bool AddForeign(EntityManager em, List<ForeignEdge> list, Entity e, bool connected)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Edge == e) { if (connected) list[i].Connected = true; return false; }
            float hw = EcsUtil.CompositionWidth(em, e) * 0.5f;
            if (hw < 0.5f) return false;   // power lines, pipes, markers
            list.Add(new ForeignEdge { Edge = e, Curve = em.GetComponentData<Curve>(e).m_Bezier, HalfWidth = hw, Connected = connected });
            return true;
        }

        // Non-works roads under / next to a prop box (net search tree). Returns how many were added to `list`.
        private int ScanForeignNear(EntityManager em, float3 pos, float3 size, List<ForeignEdge> list, bool clear)
        {
            if (clear) list.Clear();
            if (m_NetSearch == null) return 0;
            float r = math.max(size.x, size.z) * 0.5f + 1f;
            var hits = new NativeList<Entity>(16, Allocator.Temp);
            int added = 0;
            try
            {
                var tree = m_NetSearch.GetNetSearchTree(true, out JobHandle dep);
                dep.Complete();
                var it = new BoundsIter { Box = new Bounds2(pos.xz - r, pos.xz + r), Hits = hits };
                tree.Iterate(ref it);
                for (int i = 0; i < hits.Length; i++)
                    if (IsForeignEdge(em, hits[i]) && AddForeign(em, list, hits[i], false)) added++;
            }
            finally { hits.Dispose(); }
            return added;
        }

        // Connected part of the foreign list: every non-works road at a node of the chain (end junctions, mid-edge splits).
        // Entries found under overridden props stay while their road exists.
        private void RebuildForeign(ProjectProps pp)
        {
            var em = EntityManager;
            var list = pp.Foreign;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Connected || !IsForeignEdge(em, list[i].Edge)) list.RemoveAt(i);
            for (int i = 0; i < pp.Chain.Count; i++)
            {
                var r = pp.Chain[i].Record;
                for (int n = 0; n < 2; n++)
                {
                    EcsUtil.ConnectedEdges(em, n == 0 ? r.StartNode : r.EndNode, m_TmpEdges);
                    for (int k = 0; k < m_TmpEdges.Count; k++)
                        if (IsForeignEdge(em, m_TmpEdges[k])) AddForeign(em, list, m_TmpEdges[k], true);
                }
            }
        }

        // True when the prop (centre, and both ends of its long axis for long props) stands on a road of `list`. A point
        // behind the end of a road's curve is outside it (that is the junction / the works road beyond the shared node, which
        // the footprint rule covers); a road more than kForeignMaxDy above / below (bridge, tunnel) does not count.
        private static bool InsideForeign(List<ForeignEdge> list, float3 pos, quaternion rot, float3 size)
        {
            if (list.Count == 0) return false;
            bool longZ = size.z >= size.x;
            float half = math.max(0f, math.max(size.x, size.z) * 0.5f - 0.1f);
            float3 axis = math.mul(rot, longZ ? new float3(0f, 0f, 1f) : new float3(1f, 0f, 0f));
            int pts = half > 0.4f ? 3 : 1;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                var c = f.Curve;
                for (int k = 0; k < pts; k++)
                {
                    float3 q = k == 0 ? pos : pos + axis * (k == 1 ? half : -half);
                    float d = CMath.Distance(c.xz, q.xz, out float t);
                    if (d >= f.HalfWidth + PropLayout.kForeignClearance) continue;
                    if (t <= 0.001f)
                    {
                        float2 tan = math.normalizesafe(c.b.xz - c.a.xz);
                        if (math.dot(q.xz - c.a.xz, tan) < -0.01f) continue;   // behind the curve start
                    }
                    else if (t >= 0.999f)
                    {
                        float2 tan = math.normalizesafe(c.d.xz - c.c.xz);
                        if (math.dot(q.xz - c.d.xz, tan) > 0.01f) continue;    // beyond the curve end
                    }
                    float y = CMath.Position(c, t).y;
                    if (math.abs(y - q.y) > PropLayout.kForeignMaxDy) continue;
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ topology

        private static uint Bit(PropKind k) => 1u << (int)k;

        // Hash of a node and its NON-works connected edges (order independent); junction = it has one.
        private uint NodeTopo(Entity node, out bool junction)
        {
            junction = false;
            var em = EntityManager;
            uint h = Mix((uint)node.Index, (uint)node.Version);
            if (!EcsUtil.Alive(em, node) || !em.HasBuffer<ConnectedEdge>(node)) return h;
            var buf = em.GetBuffer<ConnectedEdge>(node, true);
            uint acc = 0u;
            for (int i = 0; i < buf.Length; i++)
            {
                Entity e = buf[i].m_Edge;
                if (e == Entity.Null || !em.Exists(e) || em.HasComponent<Deleted>(e)) continue;
                if (SiteRegistry.Edges.ContainsKey(e)) continue;
                junction = true;
                acc += Mix((uint)e.Index, (uint)e.Version);
            }
            return Mix(h, acc);
        }

        // Order-independent hash of the foreign roads (edge, curve, width): a rebuild that changes nothing does not re-diff.
        private static uint ForeignHash(List<ForeignEdge> list)
        {
            uint acc = (uint)list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                uint h = Mix(Mix((uint)f.Edge.Index, (uint)f.Edge.Version), Bits(f.HalfWidth));
                h = Mix(h, Mix(Mix(Bits(f.Curve.a.x), Bits(f.Curve.a.z)), Mix(Bits(f.Curve.d.x), Bits(f.Curve.d.z))));
                h = Mix(h, Mix(Mix(Bits(f.Curve.b.x), Bits(f.Curve.c.z)), (f.Connected ? 1u : 0u) | (Bits(f.Curve.a.y) << 1)));
                acc += h;
            }
            return acc;
        }

        // Chain-end nodes (u = chain start / end).
        private static Entity StartNodeOf(ProjectProps pp)
        {
            var ce = pp.Chain[0];
            return ce.DirSign > 0f ? ce.Record.StartNode : ce.Record.EndNode;
        }

        private static Entity EndNodeOf(ProjectProps pp)
        {
            var ce = pp.Chain[pp.Chain.Count - 1];
            return ce.DirSign > 0f ? ce.Record.EndNode : ce.Record.StartNode;
        }

        // Detects connections at the chain ends / chain nodes. A changed end respawns that end's group (barrier line, crew
        // props at the start, signals) once the Director's junction flag agrees with the node (its trims follow one update
        // after the geometry) or after kTopoAgreeTimeout updates. Returns true when the project must be re-diffed now.
        // The chain-END nodes are hashed every update (their change respawns the end group with the junction
        // layout, timed against the Director's trims); the hash over EVERY chain node only marks the foreign list dirty, so it
        // runs on the first check and on self-heal passes (a road connected mid-chain is also caught at once by the Overridden
        // self-heal when it overlaps a prop). A foreign rebuild re-diffs only when the foreign roads really changed.
        private bool CheckTopology(ProjectRecord p, ProjectProps pp, bool heal)
        {
            if (pp.Chain.Count == 0) return false;
            uint hs = NodeTopo(StartNodeOf(pp), out bool sj);
            uint he = NodeTopo(EndNodeOf(pp), out bool ej);
            uint ha = pp.TopoAll;
            if (heal || !pp.TopoKnown || pp.TopoAllRevision != p.Revision)
            {
                ha = 0x2545F491u;
                for (int i = 0; i < pp.Chain.Count; i++)
                {
                    var r = pp.Chain[i].Record;
                    ha += NodeTopo(r.StartNode, out _) * 3u + NodeTopo(r.EndNode, out _);
                }
                pp.TopoAllRevision = p.Revision;
            }
            bool force = false;
            if (pp.TopoKnown)
            {
                if (hs != pp.TopoStart)
                {
                    pp.PendingStart = math.max(1u, m_Update);
                    RRWLog.Verbose("props: project #" + p.Id + " chain-start node topology changed (junction=" + sj + ")");
                }
                if (he != pp.TopoEnd)
                {
                    pp.PendingEnd = math.max(1u, m_Update);
                    RRWLog.Verbose("props: project #" + p.Id + " chain-end node topology changed (junction=" + ej + ")");
                }
                if (ha != pp.TopoAll) pp.ForeignDirty = true;
            }
            pp.TopoKnown = true;
            pp.TopoStart = hs;
            pp.TopoEnd = he;
            pp.TopoAll = ha;
            if (pp.PendingStart != 0 && (p.StartIsJunction == sj || m_Update - pp.PendingStart >= (uint)PropLayout.kTopoAgreeTimeout))
            {
                pp.RespawnMask |= Bit(PropKind.BarrierStart) | Bit(PropKind.Crew) | Bit(PropKind.Signal) | Bit(PropKind.Sign) | Bit(PropKind.ConePair) | Bit(PropKind.FenceCone);
                pp.PendingStart = 0;
                pp.EndRespawns++;
                pp.ForeignDirty = true;
                force = true;
                RRWLog.Info("props: respawn end group project #" + p.Id + " start (junction=" + sj + ", director=" + p.StartIsJunction + ", trim0=" + RRWLog.F(p.TrimU0) + ")");
            }
            if (pp.PendingEnd != 0 && (p.EndIsJunction == ej || m_Update - pp.PendingEnd >= (uint)PropLayout.kTopoAgreeTimeout))
            {
                pp.RespawnMask |= Bit(PropKind.BarrierEnd) | Bit(PropKind.Signal) | Bit(PropKind.Sign) | Bit(PropKind.ConePair) | Bit(PropKind.FenceCone);
                pp.PendingEnd = 0;
                pp.EndRespawns++;
                pp.ForeignDirty = true;
                force = true;
                RRWLog.Info("props: respawn end group project #" + p.Id + " end (junction=" + ej + ", director=" + p.EndIsJunction + ", trim1=" + RRWLog.F(p.TrimU1) + ")");
            }
            if (pp.ForeignDirty)
            {
                int before = pp.Foreign.Count;
                uint fh = ForeignHash(pp.Foreign);
                RebuildForeign(pp);
                pp.ForeignDirty = false;
                // re-diff only when the list changed (else every self-heal pass of a project with a junction end
                // would rebuild the list and force a full diff)
                if (ForeignHash(pp.Foreign) != fh || pp.Foreign.Count != before) force = true;
                if (pp.Foreign.Count != before) RRWLog.Verbose("props: project #" + p.Id + " non-works roads next to the chain: " + pp.Foreign.Count);
            }
            return force;
        }

        // ------------------------------------------------------------------ project

        private void ProcessProject(ProjectRecord p, ProjectProps pp, ref int budgetUsed)
        {
            var em = EntityManager;
            var view = p.View();

            // From the frame the project hands the road back (completion frame N, a D0
            // call-off) nothing of ours stands on it any more and nothing spawns again.
            if (p.Releasing || view.Phase == WorksPhase.Complete)
            {
                ReleaseProject(p, pp, p.Releasing ? "releasing" : "complete");
                return;
            }

            if (pp.ChainRevision != p.Revision || pp.RegistryRevision != SiteRegistry.Revision) RebuildChain(p, pp);
            bool heal = ((m_Update + p.Id) % (uint)RRWConst.kSelfHealInterval) == 0u;

            // per-edge stamps (placement inputs) and the NeedsRebuild flag
            PxPerf.Begin(PxT.Refresh);
            bool stampsChanged = false;
            bool rebuild = false;
            uint projGeo = Mix(Mix(Mix((uint)p.Revision, Bits(view.Trim0)), Bits(view.Trim1)), Bits(view.U));
            for (int i = 0; i < pp.Chain.Count; i++)
            {
                var ce = pp.Chain[i];
                uint prevGeo = ce.GeoStamp, prevY = ce.YStamp;
                RefreshEdge(ce, projGeo, heal);
                if (ce.GeoStamp != prevGeo || ce.YStamp != prevY) stampsChanged = true;
                if (ce.HaveRuntime && ce.Runtime.Has(RuntimeFlags.NeedsRebuild)) rebuild = true;
            }
            PxPerf.End(PxT.Refresh);
            for (int i = 0; i < pp.Chain.Count; i++)
            {
                var ce = pp.Chain[i];
                if (ce.HaveRuntime && (ce.Runtime.Has(RuntimeFlags.Completing) || ce.Runtime.Has(RuntimeFlags.Releasing)))
                {
                    ReleaseProject(p, pp, ce.Runtime.Has(RuntimeFlags.Releasing) ? "releasing" : "completing");
                    return;
                }
            }
            pp.Released = false;
            uint bld = 0x3C6EF372u;
            for (int i = 0; i < pp.Chain.Count; i++) if (pp.Chain[i].HasBuildings) bld = Mix(bld, (uint)i + 1u);
            if (bld != pp.BuildingsKey) { pp.BuildingsKey = bld; stampsChanged = true; }
            // NeedsRebuild (Rebuild visuals / load): respawn every prop once, on the first update the flag is seen
            if (rebuild && m_Update - pp.LastRebuildUpdate > 1u) pp.RespawnAll = true;
            if (rebuild) pp.LastRebuildUpdate = m_Update;
            if (PropState.DevRespawnAll || PropState.DevRespawnProject == p.Id) pp.RespawnAll = true;

            // owner death (a node of ours was deleted: node reduction, bulldozer, Director delete)
            bool ownerDead = false;
            for (int i = 0; i < pp.OwnerNodes.Count; i++)
                if (!EcsUtil.Alive(em, pp.OwnerNodes[i])) { ownerDead = true; break; }
            if (ownerDead) pp.OwnersDirty = true;

            // The device plan of this update (in-lane readiness and thinning inputs) and the building access points (driveway gaps)
            var plan0 = PhasePlan.Props(view);
            try { UpdateAccess(p, pp, plan0, heal); }
            catch (Exception e) { RRWLog.ErrorOnce("props access points", e); }
            // cone lamps (experimental switch RRWGates.ConeLamps): the nearest kStagedLampSites sites with a divider (projects run nearest first)
            bool lampSite = RRWGates.ConeLamps && plan0.Divider != DividerStyle.None && pp.CamDist >= 0f && pp.CamDist < RRWConst.kPropFarDistance
                            && m_LampSites < RRWConst.kStagedLampSites;
            if (lampSite) m_LampSites++;
            pp.LampSite = lampSite;

            // budget: thinning per project (sticky per layout). Kerb fences, closed-end lines and signs count at
            // every level (never thinned per site); heaps -> edge cones -> divider cones thin first.
            int fixedCount = PropLayout.FixedEstimate(view, plan0, m_FenceFallback ? m_SafetyPitch : m_KerbFencePitch, pp.Access.Count)
                             + PropLayout.FenceEstimate(view, plan0, m_SiteFencePitch);
            uint layout = Mix(Mix(Mix((uint)view.Kind, (uint)view.Mode), (uint)p.Revision), Mix(Bits(math.round(view.TrimmedLength * 2f)), view.Cancelled ? 1u : 0u));
            layout = Mix(layout, Mix((uint)fixedCount, (uint)plan0.Divider));
            if (pp.Thin < 0 || pp.ThinLayout != layout)
            {
                pp.Thin = PropLayout.ThinLevel(view, plan0, fixedCount, out pp.Estimate);
                pp.ThinLayout = layout;
                if (pp.Thin > 0) RRWLog.Verbose("props: project #" + p.Id + " thinned to level " + pp.Thin + " (estimate " + pp.Estimate + ", never-thinned devices " + fixedCount + ")");
            }
            float far = RRWConst.kPropFarDistance;
            float dist = pp.CamDist >= 0f ? pp.CamDist : p.CameraDistance;
            bool wasFar = pp.Far;
            if (!pp.Far && dist > far) pp.Far = true;
            else if (pp.Far && dist < far * 0.9f) pp.Far = false;
            if (pp.Far != wasFar)
            {
                pp.FarChanges++;
                RRWLog.Info("props: project #" + p.Id + " far=" + (pp.Far ? "on" : "off") + " (camera " + RRWLog.F(dist) + " m from the chain, " +
                            RRWLog.F(p.CameraDistance) + " m from the front of crew " + p.FocusCrew + "/" + view.CrewCount + ", phase " + view.Phase + " f=" + RRWLog.F(view.F) + ")");
            }
            // Fences are never dropped by the per-site budget; only the GLOBAL budget drops them, and projects
            // run nearest first, so far projects lose their fences first. `global` = over the global budget: barriers, edge
            // cones, closed-end lines, divider (12 m) and signs only. Far projects keep the reduced slot-group set
            // (barriers + edge cones) and their fences while the global budget allows.
            pp.FenceEstimate = fixedCount;
            int estimate = pp.Estimate + PropCrewLayout.DepotEstimate(view);   // crew depots (never thinned, not in Thin)
            bool global = budgetUsed + estimate > RRWConst.kPropBudgetGlobal;
            if (!global) budgetUsed += estimate;
            bool fencesFit = !global;
            if (fencesFit != pp.FencesFit && plan0.Fence != FenceStyle.None)
                RRWLog.Info("props: project #" + p.Id + " fences " + (fencesFit ? "on" : "dropped (global budget " + RRWConst.kPropBudgetGlobal + ", used " + budgetUsed + ")") +
                            " (" + plan0.Fence + ", camera " + RRWLog.F(dist) + " m)");
            pp.FencesFit = fencesFit;
            bool limited = pp.Far || global;   // slot groups: barriers + edge cones only

            if (heal && pp.Foreign.Count > 0) pp.ForeignDirty = true;   // foreign curves can change without a topology change
            bool topo = false;
            PxPerf.Begin(PxT.Topology);
            try { topo = CheckTopology(p, pp, heal); }
            catch (Exception e) { RRWLog.ErrorOnce("props topology", e); }
            PxPerf.End(PxT.Topology);

            // The diff key is split. `key` holds every structural input (layout, plan, stamps, crews, ...): a change
            // re-diffs everything in this update (old and new devices swap in one diff). The front inputs (per-crew
            // front steps, phase-fraction steps, divider pick-up bucket) only re-evaluate the slot groups (re-applying the slots
            // whose fill changed), the crew depots and the divider: the same entities, the same frame, a fraction of the work.
            uint key = ProcessKey(p, view, pp, plan0, limited);
            uint fkey = Mix(PropCrewLayout.FrontStep(view), (uint)math.floor(view.F * 500f));   // 0.25 m crew-front steps, f windows
            uint dkey = PropCrewLayout.DividerPickKey(view);
            // Self-heal = alive check of 1/kSelfHealInterval of the slots per update (each slot every
            // kSelfHealInterval updates) instead of a full diff of the whole project every kSelfHealInterval updates.
            PxPerf.Begin(PxT.HealScan);
            if (HealScan(pp)) pp.HealPending = true;
            PxPerf.End(PxT.HealScan);
            bool urgent = key != pp.LastKey || stampsChanged || pp.OwnersDirty || pp.RespawnAll || topo || !pp.FillCacheValid;
            bool full = urgent || pp.HealPending;
            if (full && !urgent && m_FullDiffsThisUpdate >= kHealDiffsPerUpdate && pp.HealDeferStreak < kHealMaxDefer)
            {
                // only the self-heal asks for it: one such full diff per update (spike guard); the rest wait (bounded)
                pp.HealDeferred++;
                pp.HealDeferStreak++;
                PxPerf.Count(PxC.HealDeferred);
                full = false;
            }
            bool front = !full && (fkey != pp.LastFrontKey || dkey != pp.LastDividerKey);
            if (!full && !front) return;

            // demolition mobilisation gate: nothing goes up (and nothing standing changes) before the traffic has drained
            // (demolition start); only dead owners are still fixed this update.
            if (p.ClearingTraffic)
            {
                if (pp.OwnersDirty) ReOwnDead(pp);
                pp.OwnersDirty = false;
                pp.LastKey = key;
                pp.LastFrontKey = fkey;
                pp.LastDividerKey = dkey;
                pp.HealPending = false;
                return;
            }
            if (pp.Chain.Count == 0) { pp.LastKey = 0; pp.FillCacheValid = false; return; }   // records not ready yet: keep what stands, retry

            int budgetBefore = m_SpawnBudget;
            if (!full)
            {
                PxPerf.Begin(PxT.FrontDiff);
                bool ok = FrontDiff(p, pp, view, plan0, limited, dkey != pp.LastDividerKey);
                PxPerf.End(PxT.FrontDiff);
                if (ok)
                {
                    pp.FrontDiffs++;
                    PxPerf.Count(PxC.FrontDiffs);
                    pp.LastFrontKey = fkey;
                    pp.LastDividerKey = dkey;
                    if (m_SpawnBudget <= 0 && budgetBefore > 0) pp.LastKey = 0u;   // spawn budget ran out: full diff next update
                    return;
                }
                // the fill cache does not match the layout (never expected: the layout is in `key`): do the full diff now
            }
            PxPerf.Begin(PxT.FullDiff);
            Diff(p, pp, view, plan0, limited);
            PxPerf.End(PxT.FullDiff);
            m_FullDiffsThisUpdate++;
            pp.FullDiffs++;
            PxPerf.Count(PxC.FullDiffs);
            pp.LastKey = m_SpawnBudget <= 0 && budgetBefore > 0 ? 0u : key;
            pp.LastFrontKey = fkey;
            pp.LastDividerKey = dkey;
            if (pp.Crews != view.CrewCount)
            {
                // one line per crew re-latch (the Director's latch; depots shared by both layouts stayed)
                RRWLog.Info("props: project #" + p.Id + " crews " + pp.Crews + " -> " + view.CrewCount + " (" + view.Phase + " f=" + RRWLog.F(view.F) +
                            "): crew depots " + pp.Depots + " [" + DepotList(view) + "]");
                pp.Crews = view.CrewCount;
            }
            pp.HealPending = false;
            pp.HealDeferStreak = 0;
            pp.RespawnAll = false;
            pp.RespawnMask = 0;
            pp.OwnersDirty = false;
        }

        // "1/3@166.0 2/3@334.0" (dev / log only)
        internal static string DepotList(in ProjectView v)
        {
            int n = v.CrewCount;
            if (n <= 1) return "-";
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < n; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(PropCrewLayout.DepotName(PropCrewLayout.DepotId(i, n))).Append('@').Append(RRWLog.F(PropCrewLayout.DepotU(v, i, 0)));
                if (!PropCrewLayout.DepotFits(v, i)) sb.Append("(no room)");
            }
            return sb.ToString();
        }

        // Time-sliced self-heal. Checks ceil(n / kSelfHealInterval) slots per update round robin, so every slot
        // is checked once per kSelfHealInterval updates. A dead or missing entity (deleted by something else, or a spawn that
        // failed) asks for a full diff, which respawns it. The amber head's state is re-asserted when its slot comes up.
        private bool HealScan(ProjectProps pp)
        {
            int n = pp.SlotList.Count;
            if (n == 0) return false;
            var em = EntityManager;
            int per = (n + RRWConst.kSelfHealInterval - 1) / RRWConst.kSelfHealInterval;
            bool missing = false;
            for (int j = 0; j < per; j++)
            {
                if (pp.HealCursor >= n || pp.HealCursor < 0) pp.HealCursor = 0;
                var sl = pp.SlotList[pp.HealCursor++];
                pp.HealScanned++;
                if (sl.Entity == Entity.Null || !EcsUtil.Alive(em, sl.Entity))
                {
                    missing = true;
                    pp.HealMissing++;
                    PxPerf.Count(PxC.HealMissing);
                    continue;
                }
                if (PropKeys.Kind(sl.Key) == PropKind.Signal) AssertSignal(pp, sl.Entity);   // amber head: once per self-heal cycle
            }
            PxPerf.Count(PxC.HealScanned, per);
            return missing;
        }

        // Tear everything down once, in the first frame the project releases the road / completes.
        private void ReleaseProject(ProjectRecord p, ProjectProps pp, string why)
        {
            if (pp.Released && pp.Slots.Count == 0) return;
            int n = TearDown(pp);
            pp.Released = true;
            pp.LastKey = 0;
            RRWLog.Info("props: project #" + p.Id + " " + why + ": " + n + " props torn down, none spawn any more (phase " + p.Phase + ")");
        }

        private uint ProcessKey(ProjectRecord p, in ProjectView v, ProjectProps pp, in PropPlan plan, bool limited)
        {
            // Every device-plan input, so FenceSides / WorksHalf / Barriers / InLaneReady / gate changes re-diff in the
            // update they happen (old and new devices swap in the same frame)
            uint r3 = Mix((uint)plan.FenceSides | ((uint)plan.WorksHalf << 16), (uint)plan.Barriers | ((uint)plan.Divider << 4) | ((uint)plan.Fence << 8)
                          | (plan.Signs ? 1u << 12 : 0u) | (plan.InLaneReady ? 1u << 13 : 0u) | (plan.OpenEntryAtEnd ? 1u << 14 : 0u)
                          | (plan.ApproachSignals ? 1u << 15 : 0u) | (m_ApproachOn ? 1u << 16 : 0u) | (m_Na ? 1u << 17 : 0u)
                          | (pp.LampSite ? 1u << 18 : 0u) | (pp.Far ? 1u << 19 : 0u) | (pp.FencesFit ? 1u << 20 : 0u));
            r3 = Mix(r3, Mix((uint)RRWGates.Revision, (uint)m_SpeedKmh));
            r3 = Mix(r3, Mix(pp.AccessKey, ClosedBKeyOf(pp)));
            // Barrier keeps in 1/256 steps (they sweep continuously over f .90-.98; the raw bits would re-diff EVERY
            // update of the pick-up). A line has < 64 barriers, so a pick-up waits < 1/256 of the .04 window at most.
            r3 = Mix(r3, Mix((uint)math.floor(plan.StartBarrierKeep * 256f), (uint)math.floor(plan.EndBarrierKeep * 256f)));
            uint h = Mix((uint)v.Phase, (uint)v.Kind);
            h = Mix(h, (uint)v.Mode);
            h = Mix(h, (uint)v.Closure);
            h = Mix(h, (uint)v.Flags);
            h = Mix(h, (uint)p.Revision);
            // The front (0.25 m steps) and phase-fraction (f * 500) components live in the front key (ProcessProject)
            // The latched crew layout (sections move every fill and the crew depots)
            h = Mix(h, (uint)v.CrewCount | ((uint)v.CancelCrewCount << 8));
            h = Mix(h, (uint)v.Switch | (v.Ctx.Staged ? 0x100u : 0u) | (v.Ctx.SwapOn ? 0x200u : 0u) | (v.Ctx.CarHalfAllowed ? 0x400u : 0u));
            h = Mix(h, Bits(v.Trim0));
            h = Mix(h, Bits(v.Trim1));
            h = Mix(h, (p.StartShared ? 1u : 0u) | (p.EndShared ? 2u : 0u) | (p.StartIsJunction ? 4u : 0u) | (p.EndIsJunction ? 8u : 0u)
                       | (p.ClearingTraffic ? 16u : 0u) | (limited ? 32u : 0u) | (m_BeaconsOn ? 64u : 0u));
            h = Mix(h, (uint)pp.Thin);
            h = Mix(h, (uint)v.CancelPhase);
            h = Mix(h, Bits(v.CancelFront));
            h = Mix(h, Mix((uint)m_DustOre.Index, (uint)m_DustStone.Index));   // dust source swap (DustVfx) re-diffs every project
            h = Mix(h, (uint)v.OpenLanes | (pp.FencesFit ? 0x10000u : 0u));    // staged opening / fence swaps in the same frame
            h = Mix(h, pp.BuildingsKey);
            h = Mix(h, r3);
            return h == 0 ? 1u : h;
        }

        private void RebuildChain(ProjectRecord p, ProjectProps pp)
        {
            var em = EntityManager;
            pp.Chain.Clear();
            for (int i = 0; i < p.Edges.Count; i++)
            {
                Entity e = p.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var r) || r.Arc == null) continue;
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                pp.Chain.Add(new ChainEdge { Edge = e, Record = r, U0 = site.m_ChainU0, U1 = site.m_ChainU1 });
            }
            pp.Chain.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            pp.ForeignDirty = true;
            pp.ChainRevision = p.Revision;
            pp.RegistryRevision = SiteRegistry.Revision;
        }

        // Reads the edge's saved chain range, runtime and ground output; computes its placement stamps.
        // The saved chain range and the ConnectedBuilding buffer are re-read on the "slow" path only (first
        // refresh, edge geometry change, self-heal pass): a re-chain changes p.Revision (RebuildChain makes new ChainEdges), and
        // buildings appear along an edge within one self-heal cycle. Runtime and ground are read every update.
        private void RefreshEdge(ChainEdge ce, uint projGeo, bool heal)
        {
            var em = EntityManager;
            ce.PlannedValid = false;
            ce.HaveRuntime = false;
            if (!em.Exists(ce.Edge)) return;
            bool slow = heal || !ce.SlowKnown || ce.SlowGeoRevision != ce.Record.GeometryRevision;
            if (slow)
            {
                if (em.HasComponent<RoadWorksSite>(ce.Edge))
                {
                    var site = em.GetComponentData<RoadWorksSite>(ce.Edge);
                    ce.U0 = site.m_ChainU0;
                    ce.U1 = site.m_ChainU1;
                }
                ce.HasBuildingBuffer = em.HasBuffer<Game.Buildings.ConnectedBuilding>(ce.Edge) && em.GetBuffer<Game.Buildings.ConnectedBuilding>(ce.Edge, true).Length > 0;
                ce.SlowKnown = true;
                ce.SlowGeoRevision = ce.Record.GeometryRevision;
            }
            if (em.HasComponent<RoadWorksRuntime>(ce.Edge))
            {
                ce.Runtime = em.GetComponentData<RoadWorksRuntime>(ce.Edge);
                ce.HaveRuntime = true;
            }
            ce.Ground = em.HasComponent<RoadWorksGround>(ce.Edge) ? em.GetComponentData<RoadWorksGround>(ce.Edge) : RoadWorksGround.Initial;
            ce.HasBuildings = ce.Record.BuildingsWaiting > 0 || ce.HasBuildingBuffer;

            var st = ce.Record.GetOrCreate<PropEdgeState>(ModuleSlot.Props);
            if (ce.Ground.m_StepUpdate != st.LastStepUpdate)
            {
                st.LastStepUpdate = ce.Ground.m_StepUpdate;
                st.ResnapAt = m_Update + kResnapDelayUpdates;
            }
            if (st.ResnapAt != 0 && m_Update >= st.ResnapAt)
            {
                st.ResnapAt = 0;
                st.ResnapEpoch++;
            }
            bool hidden = ce.HaveRuntime && ce.Runtime.Hidden;
            bool known = ce.Ground.Known && ce.Ground.m_Kind != TerrainProfileKind.Vanilla;
            ce.GeoStamp = Mix(Mix(Mix(projGeo, (uint)ce.Record.GeometryRevision), Bits(ce.U0)), Bits(ce.U1));
            ce.YStamp = Mix(Mix(Mix(ce.Ground.m_StepUpdate, (hidden ? 1u : 0u) | (known ? 2u : 0u)), st.ResnapEpoch), (uint)ce.Ground.m_Kind);
        }

        // ------------------------------------------------------------------ diff

        private void Diff(ProjectRecord p, ProjectProps pp, in ProjectView view, in PropPlan plan, bool limited)
        {
            pp.Pass++;
            bool A = view.Mode == VisualMode.FullDig;
            pp.Trim0 = view.Trim0;
            pp.Trim1 = view.Trim1;
            pp.LastBarriers = plan.Barriers;
            bool footprint = plan.Fence == FenceStyle.Footprint && !limited;
            bool kerbs = plan.Fence == FenceStyle.Kerbs && pp.FencesFit;   // never per-site thinned; global budget only
            pp.LastFence = footprint || kerbs ? plan.Fence : FenceStyle.None;
            pp.LastPlan = plan;
            pp.HavePlan = true;
            RoadZones works = WorksCarHalf(view, plan);
            pp.LastWorksHalf = works;

            // fixed slot groups (+ crew depots). PropGroup.Divider / Signs are NOT slot groups (laid out below);
            // PropKind 7/8 stay the barrier lines, so the loop stops at PropGroup.Divider explicitly.
            SlotGroups(p, pp, view, plan, limited, A, true);
            CrewDepots(p, pp, view, plan, limited, A, true);
            pp.FillCacheValid = true;
            if (plan.Barriers != BarrierStyle.None)
            {
                BarrierLine(p, pp, view, plan, true);
                BarrierLine(p, pp, view, plan, false);
            }
            // trench fences (none for far / budget-limited projects)
            if (footprint) FootprintFences(p, pp, view);
            // staged-traffic devices: kerb fences on FenceSides (out-of-lane: follow OpenLanes at once), divider, signs
            // and the amber head (in-lane: PropPlan gives them only while InLaneReady). Closed-end lines: BarrierLine above.
            if (kerbs) KerbFences(p, pp, view, plan);
            else { pp.FencePanels = pp.FenceShorts = pp.FenceCones = pp.FenceGaps = pp.FenceRuns = 0; }
            Divider(p, pp, view, plan, works, limited);
            bool signsOn = plan.Signs && RRWGates.Signs;
            bool headOn = plan.ApproachSignals && m_ApproachOn;
            if (signsOn || headOn) SignsAndHead(p, pp, view, plan, signsOn, headOn);
            else pp.Signs = 0;
            DeviceSummary(p, pp, plan, works);

            // delete what no slot wants any more
            m_RemoveKeys.Clear();
            foreach (var kv in pp.Slots)
                if (kv.Value.Seen != pp.Pass) m_RemoveKeys.Add(kv.Key);
            for (int i = 0; i < m_RemoveKeys.Count; i++)
            {
                var slot = pp.Slots[m_RemoveKeys[i]];
                if (slot.Entity != Entity.Null) { EcsUtil.MarkDeleted(EntityManager, slot.Entity); pp.Deleted++; }
                pp.RemoveSlot(m_RemoveKeys[i]);
            }

            // distinct owners in use (death check every update)
            pp.OwnerNodes.Clear();
            foreach (var kv in pp.Slots)
            {
                var o = kv.Value.Owner;
                if (o != Entity.Null && !pp.OwnerNodes.Contains(o)) pp.OwnerNodes.Add(o);
            }
        }

        // Slot groups. full: every slot with fill >= 1 is applied (the caller deletes what was not seen) and the
        // fill cache is rebuilt. Front-only (full = false): the same fills are evaluated, but only slots whose fill
        // changed since the last diff are touched: re-applied (spawn / fill write) or, when the fill dropped below 1, deleted.
        // Everything else of a slot (stamps, owner, prefab, respawn requests) is a structural input that forces a full diff.
        // Several heaps growing / shrinking at once (one per crew section) is the normal case here: each slot is
        // independent (HeapFillSystem writes every RRWHeapFill it is given, no per-update limit).
        private void SlotGroups(ProjectRecord p, ProjectProps pp, in ProjectView view, in PropPlan plan, bool limited, bool A, bool full)
        {
            for (int gi = 0; gi < (int)PropGroup.Divider; gi++)
            {
                var g = (PropGroup)gi;
                var kind = (PropKind)gi;
                int n = PhasePlan.SlotCount(g, view);
                var cache = pp.FillCache[gi];
                if (full)
                {
                    if (cache == null || cache.Length != n) pp.FillCache[gi] = cache = new int[n];   // layout change only
                    if (limited && kind != PropKind.EdgeCone) { for (int k = 0; k < n; k++) cache[k] = -3; continue; }
                }
                else
                {
                    if (limited && kind != PropKind.EdgeCone) continue;
                    // a group that cannot fill in this view has no slot standing (the last full diff deleted them)
                    if (s_GroupPredicateOk && !PropCrewLayout.GroupMayFill(g, view)) continue;
                }
                if (n <= 0) continue;
                bool mayFill = PropCrewLayout.GroupMayFill(g, view);
                int factor = PropLayout.ThinFactor(kind, pp.Thin);
                for (int k = 0; k < n; k++)
                {
                    if (factor > 1 && k % factor != 0) { if (full) cache[k] = -2; continue; }
                    int fill = PropLayout.Fill(g, k, view, plan);
                    PxPerf.Count(PxC.FillEvals);
                    if (fill < 1) fill = -1;
                    if (full)
                    {
                        cache[k] = fill;
                        if (fill < 1) continue;
                        if (!mayFill && s_GroupPredicateOk)
                        {
                            s_GroupPredicateOk = false;   // never skip groups again this session (front diffs evaluate every group)
                            RRWLog.Warn("props: group predicate contradicted by PhasePlan.PropFill (" + g + " slot " + k + " fill " + fill + ", " + view.Kind + " " +
                                        view.Mode + " " + view.Phase + " f=" + RRWLog.F(view.F) + "): front-only diffs now evaluate every group");
                        }
                        WantGroup(p, pp, view, plan, kind, k, PhasePlan.SlotU(g, k, view), fill, A);
                        continue;
                    }
                    if (cache == null || k >= cache.Length || cache[k] == fill) continue;
                    cache[k] = fill;
                    pp.FrontApplied++;
                    PxPerf.Count(PxC.FrontApplies);
                    if (fill < 1) RemoveGroupSlot(pp, kind, k);
                    else WantGroup(p, pp, view, plan, kind, k, PhasePlan.SlotU(g, k, view), fill, A);
                }
            }
        }

        // Crew props of crews 1..n-1 (PropCrewLayout). Keys use the stable depot id (reduced fraction
        // of the boundary): a depot shared by the old and the new crew layout keeps its entities at a re-latch; the others are
        // created / deleted in that frame (full diff: crews are in the key). Front-only diffs re-check the teardown (f > .90).
        private void CrewDepots(ProjectRecord p, ProjectProps pp, in ProjectView view, in PropPlan plan, bool limited, bool A, bool full)
        {
            int n = view.CrewCount;
            if (full)
            {
                for (int i = 0; i < pp.DepotCache.Length; i++) pp.DepotCache[i] = -1;
                pp.Depots = 0;
            }
            if (n <= 1 || limited) return;   // far / over the global budget: barriers + edge cones only (like crew 0's set)
            float keep = plan.StartBarrierKeep;
            for (int i = 1; i < n; i++)
            {
                int id = PropCrewLayout.DepotId(i, n);
                if (id < 0) continue;
                bool any = false;
                for (int k = 0; k < PropCrewLayout.kDepotSlots; k++)
                {
                    int fill = PropCrewLayout.DepotFill(view, i, k, keep);
                    PxPerf.Count(PxC.FillEvals);
                    int ci = id * PropCrewLayout.kDepotSlots + k;
                    if (fill >= 1) any = true;
                    if (full)
                    {
                        pp.DepotCache[ci] = fill;
                        if (fill >= 1) WantCrewSet(p, pp, PropKind.CrewDepot, id * 2, k, PropCrewLayout.DepotU(view, i, k), fill, A);
                        continue;
                    }
                    if (pp.DepotCache[ci] == fill) continue;
                    pp.DepotCache[ci] = fill;
                    pp.FrontApplied++;
                    if (fill < 1) RemoveGroupSlot(pp, PropKind.CrewDepot, k, id * 2);
                    else WantCrewSet(p, pp, PropKind.CrewDepot, id * 2, k, PropCrewLayout.DepotU(view, i, k), fill, A);
                }
                if (full && any) pp.Depots++;
            }
        }

        // Front-only diff: slot groups and depots whose fill changed, and the divider when its pick-up bucket
        // changed. Returns false when the fill cache does not fit the layout (the caller then runs the full diff).
        private bool FrontDiff(ProjectRecord p, ProjectProps pp, in ProjectView view, in PropPlan plan, bool limited, bool divider)
        {
            if (!pp.FillCacheValid) return false;
            for (int gi = 0; gi < (int)PropGroup.Divider; gi++)
            {
                var c = pp.FillCache[gi];
                if (c == null || c.Length != PhasePlan.SlotCount((PropGroup)gi, view)) return false;
            }
            pp.Pass++;
            bool A = view.Mode == VisualMode.FullDig;
            SlotGroups(p, pp, view, plan, limited, A, false);
            CrewDepots(p, pp, view, plan, limited, A, false);
            if (divider)
            {
                // divider cones picked up behind the crew truck (C4 f .92-.96): re-lay the divider, drop the cones not wanted
                Divider(p, pp, view, plan, pp.LastWorksHalf, limited);
                m_RemoveKeys.Clear();
                for (int i = 0; i < pp.SlotList.Count; i++)
                {
                    var sl = pp.SlotList[i];
                    if (PropKeys.Kind(sl.Key) == PropKind.CentreCone && sl.Seen != pp.Pass) m_RemoveKeys.Add(sl.Key);
                }
                for (int i = 0; i < m_RemoveKeys.Count; i++) RemoveKey(pp, m_RemoveKeys[i]);
            }
            return true;
        }

        // Deletes the entities of slot-group slot k (both sides of cone pairs, the tall cones of a crew set).
        private void RemoveGroupSlot(ProjectProps pp, PropKind kind, int k, int subBase = 0)
        {
            switch (kind)
            {
                case PropKind.SurveyCone:
                case PropKind.EdgeCone:
                    RemoveKey(pp, PropKeys.Make(kind, 0, k));
                    RemoveKey(pp, PropKeys.Make(kind, 1, k));
                    return;
                case PropKind.Crew:
                case PropKind.CrewDepot:
                    RemoveKey(pp, PropKeys.Make(kind, subBase, k));
                    RemoveKey(pp, PropKeys.Make(kind, subBase + 1, k));
                    return;
                default:
                    RemoveKey(pp, PropKeys.Make(kind, 0, k));
                    return;
            }
        }

        private void RemoveKey(ProjectProps pp, int key)
        {
            if (!pp.Slots.TryGetValue(key, out var slot)) return;
            if (slot.Entity != Entity.Null && EcsUtil.Alive(EntityManager, slot.Entity)) { EcsUtil.MarkDeleted(EntityManager, slot.Entity); pp.Deleted++; }
            pp.RemoveSlot(key);
        }

        // False once a full diff found a fill >= 1 in a group PropCrewLayout.GroupMayFill called empty.
        private static bool s_GroupPredicateOk = true;

        private void WantGroup(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, PropKind kind, int k, float u, int fill, bool A)
        {
            var ce = PropLayout.Locate(pp.Chain, u, out float _, out float gap);
            if (ce == null || gap > PropLayout.kCoverTolerance) return;   // slot lies on no project edge (split-off part)
            var sec = ce.Record.Section;
            float hw = sec.HalfWidth;
            float fw = 2f * sec.FlatHalfWidth;
            var w = new Want { Kind = kind, U = u, Fill = fill, Rot = RotMode.Tangent, Group = DerivedGroup.PropStatic };
            switch (kind)
            {
                case PropKind.SurveyCone:
                    w.Y = YMode.Ground; w.Rot = RotMode.Random;
                    w.Prefab = Pick(m_Cones, 1);   // SafetyCone02
                    for (int side = 0; side < 2; side++)
                    {
                        w.Key = PropKeys.Make(kind, side, k);
                        w.Lateral = side == 0 ? -(hw + PropLayout.kSurveyConeOffset) : hw + PropLayout.kSurveyConeOffset;
                        Apply(p, pp, w);
                    }
                    return;
                case PropKind.EdgeCone:
                {
                    if (u - v.Trim0 < PropLayout.kEndClearance || v.Trim1 - u < PropLayout.kEndClearance) return;
                    // D1/D2: the road is gone, cones move to the verge and stay OnGround there (verge cones follow
                    // the terrain through vanilla, which is max(TerrainY, grade) outside the composition)
                    bool verge = plan.EdgeConesOnVerge;
                    w.Y = verge ? YMode.Ground : YMode.Curve;
                    w.Rot = RotMode.Random;
                    w.Variant = verge ? (byte)1 : (byte)0;
                    for (int side = 0; side < 2; side++)
                    {
                        w.Key = PropKeys.Make(kind, side, k);
                        w.Prefab = m_Cones.Length > 0 ? m_Cones[PropLayout.Pick(p.Seed, w.Key, m_Cones.Length)] : Entity.Null;
                        w.Lateral = verge ? (side == 0 ? -(hw + PropLayout.kVergeConeOffset) : hw + PropLayout.kVergeConeOffset)
                                          : (side == 0 ? sec.CarriageLo - PropLayout.kEdgeConeOffset : sec.CarriageHi + PropLayout.kEdgeConeOffset);
                        Apply(p, pp, w);
                    }
                    return;
                }
                case PropKind.SpoilHeap:
                    w.Key = PropKeys.Make(kind, 0, k);
                    w.Y = YMode.Ground; w.LatChain = true; w.Lateral = hw + RRWConst.kVergeOffset; w.JitterDeg = 15f;
                    w.Prefab = m_Ore; w.DustPrefab = m_DustOre; w.DustSlot = IsDustSlot(k); w.Group = DerivedGroup.PropWork;
                    Apply(p, pp, w);
                    return;
                case PropKind.DumpHeap:
                {
                    bool ore = plan.DumpHeapLoad == LoadKind.Ore;
                    w.Key = PropKeys.Make(kind, 0, k);
                    w.Y = YMode.Floor; w.LatChain = true; w.Lateral = 0.35f * fw;
                    w.Prefab = ore ? m_Ore : m_Stone; w.DustPrefab = ore ? m_DustOre : m_DustStone; w.DustSlot = IsDustSlot(k);
                    w.Group = DerivedGroup.PropWork;
                    Apply(p, pp, w);
                    return;
                }
                case PropKind.Windrow:
                    w.Key = PropKeys.Make(kind, 0, k);
                    w.Y = YMode.Floor; w.LatChain = true; w.Lateral = -0.3f * fw; w.JitterDeg = 10f;
                    w.Prefab = m_Stone; w.DustPrefab = m_DustStone; w.DustSlot = IsDustSlot(k); w.Group = DerivedGroup.PropWork;
                    Apply(p, pp, w);
                    return;
                case PropKind.Rubble:
                    w.Key = PropKeys.Make(kind, 0, k);
                    w.Y = YMode.Floor; w.LatChain = true; w.Lateral = 0.15f * fw; w.JitterDeg = 5f;
                    w.Prefab = m_Rubble.Length > 0 ? m_Rubble[PropLayout.Pick(p.Seed, w.Key, m_Rubble.Length)] : Entity.Null;
                    w.Group = DerivedGroup.PropWork;
                    Apply(p, pp, w);
                    return;
                case PropKind.Crew:
                    WantCrewSet(p, pp, kind, 0, k, u, fill, A);
                    return;
            }
        }

        // Crew props slot k (crew 0: PropKind.Crew, subBase 0; crew depots: PropKind.CrewDepot, subBase = depot id * 2):
        // mode A on the left verge behind the parked crew truck (ground); mode D on the right sidewalk. The truck-load props are
        // ~2 x 3.7 m: their long side goes ACROSS the road so the 2.5 m slots line up side by side (along the road they would
        // interpenetrate). Slots 0 and 2 get a tall cone 1.5 m beyond them, 1 m closer to the road.
        private void WantCrewSet(ProjectRecord p, ProjectProps pp, PropKind kind, int subBase, int k, float u, int fill, bool A)
        {
            var ce = PropLayout.Locate(pp.Chain, u, out float _, out float gap);
            if (ce == null || gap > PropLayout.kCoverTolerance) return;   // slot lies on no project edge (split-off part)
            float hw = ce.Record.Section.HalfWidth;
            bool onVerge = A;
            var w = new Want { Kind = kind, U = u, Fill = fill, Group = DerivedGroup.PropStatic };
            w.Key = PropKeys.Make(kind, subBase, k);
            w.Rot = RotMode.Across;
            w.YawExtra = 90f;
            w.JitterDeg = 3f;
            if (onVerge) { w.Y = YMode.Ground; w.LatChain = true; w.Lateral = -(hw + RRWConst.kVergeOffset); }
            else { w.Y = YMode.Sidewalk; w.Lateral = math.max(0.5f, hw - PropLayout.kSidewalkInset); }
            w.Prefab = m_Crew.Length > 0 ? m_Crew[k % m_Crew.Length] : Entity.Null;
            Apply(p, pp, w);
            if (k == 0 || k == 2)
            {
                var c = w;
                c.Key = PropKeys.Make(kind, subBase + 1, k);
                c.U = u + (k == 0 ? -1.5f : 1.5f);
                c.Lateral = onVerge ? w.Lateral + 1f : w.Lateral - 1f;
                c.Rot = RotMode.Random;
                c.Prefab = m_TallCone;
                Apply(p, pp, c);
            }
        }

        private static bool IsDustSlot(int k)
        {
            int every = RRWConst.kHeapDustEvery;
            return every > 0 && k % every == every / 2;
        }

        // End barrier line at one chain end.
        private void BarrierLine(ProjectRecord p, ProjectProps pp, in ProjectView v, in PropPlan plan, bool start)
        {
            if (start ? p.StartShared : p.EndShared) return;   // the other project's line stands there
            float keep = start ? plan.StartBarrierKeep : plan.EndBarrierKeep;
            if (keep <= 0f) return;
            var ce = start ? pp.Chain[0] : pp.Chain[pp.Chain.Count - 1];
            if (ce.Record.Arc == null) return;
            bool fwd = ce.DirSign > 0f;
            Entity node = start ? (fwd ? ce.Record.StartNode : ce.Record.EndNode) : (fwd ? ce.Record.EndNode : ce.Record.StartNode);
            var sec = ce.Record.Section;
            bool constr = v.Kind == WorksKind.Construction;
            var kind = start ? PropKind.BarrierStart : PropKind.BarrierEnd;
            float lo = sec.CarriageLo, hi = sec.CarriageHi;
            if (hi - lo < 1f) { float c = (lo + hi) * 0.5f; lo = c - 0.5f; hi = c + 0.5f; }
            bool beacons = m_BeaconsOn && m_Beacon != Entity.Null;

            if (plan.Barriers == BarrierStyle.AcrossCarriageway || plan.Barriers == BarrierStyle.WorksHalf)
            {
                bool junction = start ? p.StartIsJunction : p.EndIsJunction;
                bool deadEnd = !junction && ConnectedCount(node) <= 1;
                bool hiddenEnd = ce.HaveRuntime && ce.Runtime.Hidden;
                float u;
                YMode y;
                bool outside = false;
                // A junction-end line stands INSIDE the trimmed chain (never on the junction node); FloorWorldY
                // puts it on the ground at the mouth of the trench while hidden and on the road once visible. The line beyond
                // the excavated cul-de-sac cap (natural ground) is kept only while the dead end is hidden.
                if (junction) { u = start ? v.Trim0 + PropLayout.kJunctionInset : v.Trim1 - PropLayout.kJunctionInset; y = YMode.Floor; }
                else if (deadEnd && v.Mode == VisualMode.FullDig && hiddenEnd)
                {
                    float cap = sec.HalfWidth + PropLayout.kDeadEndGap;   // cul-de-sac cap length ~ HalfWidth
                    u = start ? v.Trim0 - cap : v.Trim1 + cap;
                    y = YMode.Ground;
                    outside = true;
                }
                else { u = start ? v.Trim0 + PropLayout.kOtherEndInset : v.Trim1 - PropLayout.kOtherEndInset; y = YMode.Floor; }

                if (plan.Barriers == BarrierStyle.WorksHalf)
                {
                    WorksHalfLine(p, pp, v, plan, ce, kind, node, u, y, keep, constr, beacons, start);
                    return;
                }

                float pitch = (constr ? m_SafetyPitch : m_ConcretePitch);
                int n = math.max(1, (int)math.ceil((hi - lo) / pitch - 0.05f));
                int nKeep = (int)math.round(keep * n);
                float centre = (lo + hi) * 0.5f;
                int beaconIndex = 0;
                for (int i = 0; i < n; i++)
                {
                    bool isBeacon = beacons && (constr ? (i % 2) == 0 : (i == 0 || i == n - 1));
                    int chase = beaconIndex;
                    if (isBeacon && constr) beaconIndex++;
                    if (i >= nKeep) continue;   // picked up one by one from the CarriageHi side
                    var w = new Want
                    {
                        Key = PropKeys.Make(kind, 0, i),
                        Kind = kind,
                        U = u,
                        Lateral = centre + (i - (n - 1) * 0.5f) * pitch,
                        Y = y,
                        Rot = RotMode.Across,
                        Fill = 255,
                        OwnerNode = node,
                        Group = DerivedGroup.PropStatic,
                        AllowOutside = outside,
                    };
                    w.JitterDeg = 2f;
                    if (isBeacon)
                    {
                        w.Prefab = m_Beacon;
                        w.Seed = constr ? BeaconPhase.Chase(chase) : BeaconPhase.Random(p.Seed, w.Key);
                    }
                    else w.Prefab = constr ? Pick(m_Safety, i) : Pick(m_Concrete, i);
                    Apply(p, pp, w);
                }
                return;
            }

            // KerbLine (SlowZone / Open): 3 barriers per side along the kerbs at each end, the outer one tapered 20 deg,
            // the first and last of each side a beacon with a random phase; nothing across the lanes.
            int keepPerSide = (int)math.round(keep * PropLayout.kKerbPerSide);
            for (int side = 0; side < 2; side++)
            {
                float lat = side == 0 ? lo - PropLayout.kKerbOffset : hi + PropLayout.kKerbOffset;
                for (int i = 0; i < PropLayout.kKerbPerSide && i < keepPerSide; i++)
                {
                    float along = PropLayout.kOtherEndInset + m_KerbPitch * (i + 0.5f);
                    var w = new Want
                    {
                        Key = PropKeys.Make(kind, 1 + side, i),
                        Kind = kind,
                        U = start ? v.Trim0 + along : v.Trim1 - along,
                        Lateral = lat,
                        Y = YMode.Curve,
                        Rot = RotMode.Tangent,
                        Fill = 255,
                        OwnerNode = node,
                        Group = DerivedGroup.PropStatic,
                        JitterDeg = 1f,
                    };
                    if (i == 0) w.YawExtra = PropLayout.kKerbTaperDeg * (side == 0 ? 1f : -1f) * (start ? 1f : -1f) * ce.DirSign;
                    bool isBeacon = beacons && (i == 0 || i == PropLayout.kKerbPerSide - 1);
                    if (isBeacon) { w.Prefab = m_Beacon; w.Seed = BeaconPhase.Random(p.Seed, w.Key); }
                    else w.Prefab = constr ? Pick(m_Safety, side * 3 + i) : Pick(m_Concrete, side * 3 + i);
                    Apply(p, pp, w);
                }
            }
        }

        // Trench fence panels while the road is hidden (FenceStyle.Footprint): site fences on both sides at
        // HalfWidth + kFootprintFenceOffset (inside the old footprint, never in the building lots), only along edges with
        // buildings, on the ground; every pitch (prefab length) over [Trim0 + gap, Trim1 - gap], a short panel fills the end.
        // The kerb fences (FenceStyle.Kerbs) are laid out per side by KerbFences (PropDevices.cs).
        private void FootprintFences(ProjectRecord p, ProjectProps pp, in ProjectView v)
        {
            Entity[] main = m_SiteFence;
            Entity[] shortF = m_SiteFenceShort;
            float pitch = m_SiteFencePitch;
            float shortLen = m_SiteShortLen;
            if (main == null || main.Length == 0 || pitch < 0.5f) return;
            float a = v.Trim0 + PropLayout.kFenceEndGap, b = v.Trim1 - PropLayout.kFenceEndGap;
            if (b - a < pitch) return;
            int n = (int)math.floor((b - a) / pitch);
            float rest = (b - a) - n * pitch;
            for (int side = 0; side < 2; side++)   // chain frame: 0 = left, 1 = right
            {
                for (int k = 0; k < n; k++)
                    WantFootprintFence(p, pp, side, k, a + pitch * (k + 0.5f), Pick(main, k + side));
                if (shortF != null && shortF.Length > 0 && rest >= shortLen * 0.9f)
                    WantFootprintFence(p, pp, side, 0xFFF0, a + n * pitch + shortLen * 0.5f, Pick(shortF, side));
            }
        }

        private void WantFootprintFence(ProjectRecord p, ProjectProps pp, int side, int k, float u, Entity prefab)
        {
            var ce = PropLayout.Locate(pp.Chain, u, out float _, out float gap);
            if (ce == null || gap > PropLayout.kCoverTolerance) return;
            if (!ce.HasBuildings) return;   // trench fence only where people live / work along the road
            float off = ce.Record.Section.HalfWidth + RRWConst.kFootprintFenceOffset;
            Apply(p, pp, new Want
            {
                Key = PropKeys.Make(PropKind.Fence, side, k),
                Kind = PropKind.Fence,
                U = u,
                Lateral = side == 0 ? -off : off,
                LatChain = true,
                Y = YMode.Ground,
                Rot = RotMode.Tangent,
                JitterDeg = 0.5f,
                Fill = 255,
                Prefab = prefab,
                Group = DerivedGroup.PropStatic,
            });
        }

        private int ConnectedCount(Entity node)
        {
            EcsUtil.ConnectedEdges(EntityManager, node, m_TmpEdges);
            return m_TmpEdges.Count;
        }

        // ------------------------------------------------------------------ slot reconcile

        private void Apply(ProjectRecord p, ProjectProps pp, in Want w)
        {
            if (w.Prefab == Entity.Null) return;
            var em = EntityManager;
            // Nothing outside the trimmed chain (junction nodes, finished roads beyond the trims); the only
            // exception is the barrier line beyond a hidden dead end's excavated cap. A slot that is not wanted is deleted.
            if (!w.AllowOutside && (w.U < pp.Trim0 - PropLayout.kFootprintSlack || w.U > pp.Trim1 + PropLayout.kFootprintSlack))
            {
                pp.SkippedOutside++;
                return;
            }
            pp.Slots.TryGetValue(w.Key, out var slot);
            var ce = PropLayout.Locate(pp.Chain, w.U, out float s, out float _);
            if (ce == null || ce.Record.Arc == null) return;

            // Never inside a non-works road (a road connected at a chain node, or one a player built
            // over our props): the slot is skipped while that road is there.
            if (pp.Foreign.Count > 0)
            {
                Entity pf = slot != null && slot.Prefab != Entity.Null ? slot.Prefab : w.Prefab;
                var pinfo = Info(pf);
                ushort pseed = slot != null && slot.Seed != 0 ? slot.Seed : w.Seed != 0 ? w.Seed : BeaconPhase.Random(p.Seed, w.Key);
                Place(ce, s, w, pinfo, pseed, out float3 ppos, out quaternion prot, out bool _);
                if (InsideForeign(pp.Foreign, ppos, prot, pinfo.Size))
                {
                    pp.SkippedForeign++;
                    return;
                }
            }

            bool alive = slot != null && slot.Entity != Entity.Null && EcsUtil.Alive(em, slot.Entity);
            if (alive)
            {
                // prefab: dust variant is fixed for the entity's lifetime; beacons follow the setting (same-frame swap)
                Entity want = slot.Dust && w.DustPrefab != Entity.Null ? w.DustPrefab : w.Prefab;
                // A chain-end topology change replaces that end's group (new entity first, old deleted).
                // An explicit seed change (a beacon switching between chase and random phase) also re-spawns: the
                // PseudoRandomSeed is written at spawn only.
                if (pp.RespawnAll || (pp.RespawnMask & Bit(w.Kind)) != 0 || slot.Prefab != want || (w.Seed != 0 && slot.Seed != w.Seed))
                {
                    if (!Spawn(p, pp, w, ce, s, slot, replace: true)) slot.Seen = pp.Pass;   // keep the old one if the new failed
                    return;
                }
                slot.Seen = pp.Pass;
                bool geo = slot.SiteEdge != ce.Edge || slot.GeoStamp != ce.GeoStamp || slot.Variant != w.Variant;
                // the slot's own u / Y mode changed (end layout switched: dead end <-> junction, hidden <-> visible)
                geo |= math.abs(slot.U - w.U) > 0.01f || slot.Y != w.Y;
                // lateral / yaw / chord inputs changed (rrw.gate fencelat / signflip, C4 side swap, DirSplit re-measured)
                geo |= slot.PlaceKey != PlaceKeyOf(w);
                bool y = slot.Explicit && slot.YStamp != ce.YStamp;
                if (geo || y) Replace(p, pp, slot, w, ce, s);
                UpdateFill(slot, w.Fill);
                if (pp.OwnersDirty && slot.Owner != Entity.Null && !EcsUtil.Alive(em, slot.Owner)) ReOwn(slot, w, ce);
                if (w.Kind == PropKind.Signal) AssertSignal(pp, slot.Entity);   // amber head: re-asserted on every diff / self-heal pass
                return;
            }
            if (slot == null)
            {
                slot = new PropSlot { Key = w.Key };
                pp.AddSlot(slot);
            }
            if (!Spawn(p, pp, w, ce, s, slot, replace: false))
            {
                // could not spawn now (budget / prefab): keep the slot empty but wanted so it is retried
                slot.Entity = Entity.Null;
                slot.Seen = pp.Pass;
            }
        }

        // Spawns the slot's entity (replace = the old entity is deleted in the same frame, after the new one exists).
        private bool Spawn(ProjectRecord p, ProjectProps pp, in Want w, ChainEdge ce, float s, PropSlot slot, bool replace)
        {
            var em = EntityManager;
            if (m_SpawnBudget <= 0) return false;
            bool dust = w.DustPrefab != Entity.Null && w.DustSlot && m_DustOn;
            if (replace && !pp.RespawnAll && slot.Dust && w.DustPrefab != Entity.Null) dust = true;   // keep the variant on a family swap
            Entity prefab = dust ? w.DustPrefab : w.Prefab;
            var info = Info(prefab);
            if (!info.Valid)
            {
                if (dust && w.Prefab != Entity.Null && Info(w.Prefab).Valid) { prefab = w.Prefab; info = Info(prefab); dust = false; }
                else
                {
                    RRWLog.Once("props-noprefab-" + prefab.Index, "props: prefab " + EcsUtil.PrefabName(m_PrefabSystem, em, prefab) + " has no valid archetype; slot " + PropKeys.Name(w.Key) + " skipped");
                    return false;
                }
            }
            ushort seed = w.Seed != 0 ? w.Seed : BeaconPhase.Random(p.Seed, w.Key);
            Place(ce, s, w, info, seed, out float3 pos, out quaternion rot, out bool explicitY);

            var arch = em.GetComponentData<ObjectData>(prefab).m_Archetype;
            var arr = em.CreateEntity(arch, 1, Allocator.Temp);
            Entity e = arr[0];
            arr.Dispose();
            em.SetComponentData(e, new PrefabRef(prefab));
            em.SetComponentData(e, new ObjTransform(pos, rot));
            if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed(seed));
            if (!em.HasComponent<Created>(e)) em.AddComponent<Created>(e);
            if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
            if (explicitY)
            {
                var el = new ObjElevation(0f, (ElevationFlags)0);
                if (em.HasComponent<ObjElevation>(e)) em.SetComponentData(e, el); else em.AddComponentData(e, el);
            }
            byte pct = PropLayout.Percent(w.Fill);
            if (info.Quantity)
            {
                em.AddComponentData(e, new RRWHeapFill { m_Fullness = pct });
                if (em.HasComponent<Quantity>(e)) em.SetComponentData(e, new Quantity { m_Fullness = pct });
            }
            em.AddComponentData(e, new RRWProp { m_ProjectId = p.Id, m_Key = w.Key });
            EcsUtil.TagDerived(em, e, ce.Edge, p.Id, w.Group);
            if (w.Kind == PropKind.Signal && em.HasComponent<Game.Objects.TrafficLight>(e))
            {
                em.SetComponentData(e, new Game.Objects.TrafficLight { m_State = Game.Objects.TrafficLightState.Yellow | Game.Objects.TrafficLightState.Flashing });
                pp.SignalWrites++;
            }

            Entity owner = OwnerFor(w, ce);
            if (owner != Entity.Null)
            {
                PropState.OwnerRequests.Add(new OwnerRequest { Prop = e, Owner = owner });
                if (!pp.OwnerNodes.Contains(owner)) pp.OwnerNodes.Add(owner);   // front-only diffs spawn too (death check)
            }
            PxPerf.Count(PxC.Spawns);

            if (replace && slot.Entity != Entity.Null) { EcsUtil.MarkDeleted(em, slot.Entity); pp.Replaced++; }
            slot.Entity = e;
            slot.Prefab = prefab;
            slot.SiteEdge = ce.Edge;
            slot.Owner = owner;
            slot.GeoStamp = ce.GeoStamp;
            slot.YStamp = ce.YStamp;
            slot.Variant = w.Variant;
            slot.Y = w.Y;
            slot.Explicit = explicitY;
            slot.Heap = info.Quantity;
            slot.Dust = dust;
            slot.Fullness = pct;
            slot.Seed = seed;
            slot.Pos = pos;
            slot.Rot = rot;
            slot.Seen = pp.Pass;
            slot.U = w.U;
            slot.AllowOutside = w.AllowOutside;
            slot.PlaceKey = PlaceKeyOf(w);
            slot.FootA = w.FootA;
            slot.FootB = w.FootB;
            slot.Lift = w.Lift;
            pp.Spawned++;
            m_SpawnBudget--;
            return true;
        }

        // Hash of the placement inputs that are not covered by the edge stamps (lateral, yaw, rot mode, chord).
        private static uint PlaceKeyOf(in Want w)
        {
            uint h = Mix(Bits(math.round(w.Lateral * 1000f)), (uint)w.Rot | (w.LatChain ? 16u : 0u) | (w.ChainFacing ? 32u : 0u) | (w.World ? 64u : 0u));
            h = Mix(h, Bits(math.round(w.YawExtra * 10f)));
            if (w.World)
            {
                h = Mix(h, Mix(Bits(math.round(w.WorldPos.x * 1000f)), Bits(math.round(w.WorldPos.z * 1000f))));
                h = Mix(h, Mix(Bits(math.round(w.WorldPos.y * 1000f)), Bits(math.round(math.atan2(w.WorldDir.x, w.WorldDir.z) * 10000f))));
            }
            return h;
        }

        // Re-placement (terrain step, geometry change, variant change): Transform + Updated only if it really moved.
        private void Replace(ProjectRecord p, ProjectProps pp, PropSlot slot, in Want w, ChainEdge ce, float s)
        {
            var em = EntityManager;
            var info = Info(slot.Prefab);
            Place(ce, s, w, info, slot.Seed, out float3 pos, out quaternion rot, out bool explicitY);
            if (explicitY != slot.Explicit)
            {
                // Y mode changed (e.g. a cone moved from the road to the verge): fix the Elevation component
                if (explicitY)
                {
                    var el = new ObjElevation(0f, (ElevationFlags)0);
                    if (em.HasComponent<ObjElevation>(slot.Entity)) em.SetComponentData(slot.Entity, el); else em.AddComponentData(slot.Entity, el);
                }
                else if (em.HasComponent<ObjElevation>(slot.Entity)) em.RemoveComponent<ObjElevation>(slot.Entity);
                slot.Explicit = explicitY;
            }
            bool moved = math.distancesq(pos, slot.Pos) > kMoveEpsilon * kMoveEpsilon || math.abs(math.dot(rot.value, slot.Rot.value)) < 0.99999f;
            if (moved)
            {
                em.SetComponentData(slot.Entity, new ObjTransform(pos, rot));
                if (!em.HasComponent<Updated>(slot.Entity)) em.AddComponent<Updated>(slot.Entity);
                slot.Pos = pos;
                slot.Rot = rot;
                pp.Moved++;
            }
            if (slot.SiteEdge != ce.Edge)
            {
                // the slot now lies on another (split remnant / re-chained) edge: re-tag so the Director's GC keeps it
                EcsUtil.TagDerived(em, slot.Entity, ce.Edge, p.Id, w.Group);
                slot.SiteEdge = ce.Edge;
            }
            slot.GeoStamp = ce.GeoStamp;
            slot.YStamp = ce.YStamp;
            slot.Variant = w.Variant;
            slot.Y = w.Y;
            slot.U = w.U;
            slot.AllowOutside = w.AllowOutside;
            slot.PlaceKey = PlaceKeyOf(w);
            slot.FootA = w.FootA;
            slot.FootB = w.FootB;
            slot.Lift = w.Lift;
        }

        private void UpdateFill(PropSlot slot, int fill)
        {
            if (!slot.Heap) return;
            byte pct = PropLayout.Percent(fill);
            if (pct == slot.Fullness) return;
            var em = EntityManager;
            uint mask = Info(slot.Prefab).StepMask;
            bool stepChanged = PropLayout.MeshStep(mask, pct) != PropLayout.MeshStep(mask, slot.Fullness);
            if (em.HasComponent<RRWHeapFill>(slot.Entity)) em.SetComponentData(slot.Entity, new RRWHeapFill { m_Fullness = pct });
            else em.AddComponentData(slot.Entity, new RRWHeapFill { m_Fullness = pct });
            // Quantity itself is written too. HeapFillSystem only visits Updated/BatchesUpdated heaps, so a fill change
            // inside one mesh step (e.g. a windrow 32 -> 20 % while D1 loads it away, or the first fills after a load) left
            // Quantity at the old value: harmless on screen (same step) but wrong data ("quantity 32 != fill 20"). Writing it
            // here is a plain SetComponentData; QuantityUpdateSystem still resets it on Updated/BatchesUpdated and HeapFill
            // restores it in the same frame.
            if (em.HasComponent<Quantity>(slot.Entity)) em.SetComponentData(slot.Entity, new Quantity { m_Fullness = pct });
            // re-batch only when the visible mesh step changes (QuantityUpdateSystem resets, HeapFillSystem rewrites)
            if (stepChanged && !em.HasComponent<BatchesUpdated>(slot.Entity)) em.AddComponent<BatchesUpdated>(slot.Entity);
            slot.Fullness = pct;
        }

        // Owner node died: request a live node of the slot's edge at Mod4 of this update, or remove the Owner then.
        private void ReOwn(PropSlot slot, in Want w, ChainEdge ce)
        {
            Entity owner = OwnerFor(w, ce);
            PropState.OwnerRequests.Add(new OwnerRequest { Prop = slot.Entity, Owner = owner });
            slot.Owner = owner;
        }

        // Owner death while the project is frozen: re-own every prop whose owner died (or drop the Owner) this update.
        private void ReOwnDead(ProjectProps pp)
        {
            var em = EntityManager;
            foreach (var kv in pp.Slots)
            {
                var sl = kv.Value;
                if (sl.Owner == Entity.Null || EcsUtil.Alive(em, sl.Owner) || !EcsUtil.Alive(em, sl.Entity)) continue;
                Entity owner = Entity.Null;
                if (SiteRegistry.TryGetEdge(sl.SiteEdge, out var r))
                    owner = ValidOwner(em, r.StartNode) ? r.StartNode : ValidOwner(em, r.EndNode) ? r.EndNode : Entity.Null;
                PropState.OwnerRequests.Add(new OwnerRequest { Prop = sl.Entity, Owner = owner });
                sl.Owner = owner;
            }
            pp.OwnerNodes.Clear();
            foreach (var kv in pp.Slots)
            {
                var o = kv.Value.Owner;
                if (o != Entity.Null && !pp.OwnerNodes.Contains(o)) pp.OwnerNodes.Add(o);
            }
        }

        // Late owner: the line's chain-end node for barriers, else the start node of the slot's edge (one owner
        // per edge, so neighbouring props never override each other), else its end node. Only nodes with a SubObject buffer.
        private Entity OwnerFor(in Want w, ChainEdge ce)
        {
            var em = EntityManager;
            if (w.NoOwner) return Entity.Null;
            if (w.OwnerNode != Entity.Null && ValidOwner(em, w.OwnerNode)) return w.OwnerNode;
            if (ValidOwner(em, ce.Record.StartNode)) return ce.Record.StartNode;
            if (ValidOwner(em, ce.Record.EndNode)) return ce.Record.EndNode;
            return Entity.Null;
        }

        internal static bool ValidOwner(EntityManager em, Entity node) =>
            EcsUtil.Alive(em, node) && em.HasComponent<Game.Net.Node>(node) && em.HasBuffer<ObjSubObject>(node);

        // ------------------------------------------------------------------ placement

        // World transform of a slot. explicitY = the prop gets Elevation{0, 0} (we own its Y).
        private void Place(ChainEdge ce, float s, in Want w, in PrefabInfo info, ushort seed, out float3 pos, out quaternion rot, out bool explicitY)
        {
            if (w.World)
            {
                PlaceChord(w, info, seed, out pos, out rot);
                explicitY = true;
                return;
            }
            var arc = ce.Record.Arc;
            float lat = w.LatChain ? PropLayout.EdgeLateral(ce, w.Lateral) : w.Lateral;
            pos = arc.Offset(s, lat);
            float curveY = pos.y;
            explicitY = w.Y != YMode.Ground;
            switch (w.Y)
            {
                case YMode.Ground:
                {
                    float ty = TerrainY(pos);
                    pos.y = float.IsNaN(ty) ? curveY : ty;
                    break;
                }
                case YMode.Curve:
                    break;
                case YMode.Floor:
                    pos.y = FloorY(ce, curveY, pos);
                    break;
                case YMode.Verge:
                {
                    float ty = TerrainY(pos);
                    float grade = curveY + (ce.HaveRuntime ? ce.Runtime.m_GradeOffset : ce.Record.Section.GradeOffset);
                    pos.y = float.IsNaN(ty) ? grade : math.max(ty, grade);
                    break;
                }
                case YMode.Sidewalk:
                    pos.y = curveY + SidewalkLift(ce.Edge);
                    break;
                case YMode.Panel:
                case YMode.PanelSidewalk:
                    // a non-world panel (none in the product): centre curve Y - drop
                    pos.y = curveY - PropLayout.kPanelDrop + (w.Y == YMode.PanelSidewalk ? SidewalkLift(ce.Edge) : 0f);
                    break;
            }

            float sc = math.clamp(s, 0f, arc.Length);
            float3 d = arc.Direction(sc);
            if ((w.Rot == RotMode.Tangent && w.ChainFacing) || w.Rot == RotMode.Facing) d *= ce.DirSign;   // Facing: local +Z along +u
            float2 dxz = math.normalizesafe(d.xz, new float2(0f, 1f));
            quaternion q = quaternion.LookRotationSafe(new float3(dxz.x, 0f, dxz.y), math.up());
            float extra;
            if (w.Rot == RotMode.Random)
                extra = (PropLayout.Jitter(seed, w.Key, 3u) + 1f) * 180f;
            else
            {
                extra = w.YawExtra + PropLayout.Jitter(seed, w.Key, 1u) * w.JitterDeg;
                if (w.Rot == RotMode.Tangent && info.Size.x > info.Size.z + 0.05f) extra += 90f;   // long side along the road
            }
            rot = math.mul(q, quaternion.RotateY(math.radians(extra)));
        }

        // Chord placement. The long axis (local Z, or X turned by 90 degrees) runs along the chord foot A -> foot B,
        // the prefab's bounds centre sits on the chord midpoint (pivot correction from ObjectGeometryData.m_Bounds), vertical
        // (no pitch), deterministic +-JitterDeg yaw and +-kFenceJitterLat lateral jitter per slot. Y is final in WorldPos.
        private static void PlaceChord(in Want w, in PrefabInfo info, ushort seed, out float3 pos, out quaternion rot)
        {
            float2 d = math.normalizesafe(w.WorldDir.xz, new float2(0f, 1f));
            quaternion q = quaternion.LookRotationSafe(new float3(d.x, 0f, d.y), math.up());
            float extra = PropLayout.Jitter(seed, w.Key, 1u) * w.JitterDeg;
            if (info.Size.x > info.Size.z + 0.05f) extra += 90f;   // long side along the chord
            rot = math.mul(q, quaternion.RotateY(math.radians(extra)));
            float3 right = new float3(d.y, 0f, -d.x);
            pos = w.WorldPos + right * (PropLayout.Jitter(seed, w.Key, 2u) * PropLayout.kFenceJitterLat);
            if (w.Pivot)
            {
                float3 c = info.Centre;
                c.y = 0f;
                pos -= math.mul(rot, c);
            }
        }

        private float FloorY(ChainEdge ce, float curveY, float3 pos)
        {
            if (!ce.HaveRuntime) return curveY;
            if (!ce.Runtime.Hidden) return curveY;
            float natural = TerrainY(pos);
            if (!ce.PlannedValid)
            {
                ce.Planned = TerrainProfile.Vanilla;
                if (!(ce.Ground.Known && ce.Ground.m_Kind != TerrainProfileKind.Vanilla) && EntityManager.HasComponent<RoadWorksSite>(ce.Edge))
                {
                    var site = EntityManager.GetComponentData<RoadWorksSite>(ce.Edge);
                    ce.Planned = PhasePlan.Terrain(PlanInput.From(site, ce.Runtime));
                }
                ce.PlannedValid = true;
            }
            float y = GroundMath.FloorWorldY(ce.Runtime, ce.Ground, ce.Planned, curveY, natural);
            return float.IsNaN(y) || float.IsInfinity(y) ? curveY : y;
        }

        // Sidewalk surface above the curve (objects on a net stand at curve Y + NetCompositionData.m_SurfaceHeight.max).
        private float SidewalkLift(Entity edge)
        {
            var em = EntityManager;
            if (!em.HasComponent<Composition>(edge)) return 0f;
            var c = em.GetComponentData<Composition>(edge).m_Edge;
            if (!EcsUtil.ValidComposition(em, c)) return 0f;
            float m = em.GetComponentData<NetCompositionData>(c).m_SurfaceHeight.max;
            return float.IsNaN(m) ? 0f : math.clamp(m, 0f, 0.5f);
        }

        private float TerrainY(float3 p)
        {
            if (!m_HeightTried)
            {
                m_HeightTried = true;
                m_HeightValid = false;
                try
                {
                    if (m_TerrainSystem != null)
                    {
                        m_Height = m_TerrainSystem.GetHeightData();
                        m_HeightValid = m_Height.isCreated && m_Height.resolution.x > 2;
                    }
                }
                catch { m_HeightValid = false; }
            }
            if (!m_HeightValid) return float.NaN;
            return TerrainUtils.SampleHeight(ref m_Height, p);
        }

        // ------------------------------------------------------------------ teardown / prefabs

        private int TearDown(ProjectProps pp)
        {
            int n = 0;
            foreach (var kv in pp.Slots)
                if (kv.Value.Entity != Entity.Null && EcsUtil.Alive(EntityManager, kv.Value.Entity))
                {
                    EcsUtil.MarkDeleted(EntityManager, kv.Value.Entity);
                    n++;
                }
            pp.ClearSlots();
            pp.OwnerNodes.Clear();
            return n;
        }

        private void ResolvePrefabs()
        {
            var ps = m_PrefabSystem;
            m_Safety = PrefabCatalog.Statics(ps, PrefabNames.SafetyBarriers).ToArray();
            m_Concrete = PrefabCatalog.Statics(ps, PrefabNames.ConcreteBarriers).ToArray();
            m_Cones = PrefabCatalog.Statics(ps, PrefabNames.Cones).ToArray();
            m_Rubble = PrefabCatalog.Statics(ps, PrefabNames.Rubble).ToArray();
            m_Crew = PrefabCatalog.Statics(ps, PrefabNames.CrewProps).ToArray();
            m_TallCone = PrefabCatalog.Static(ps, PrefabNames.TallCone);
            m_Ore = PrefabCatalog.Static(ps, PrefabNames.OreHeap);
            m_Stone = PrefabCatalog.Static(ps, PrefabNames.StoneHeap);
            m_Beacon = PropClones.Resolve(ps, PropClones.BarrierBeacon);
            if (!Info(m_Beacon).Valid) m_Beacon = Entity.Null;
            m_DustOre = PickDust(ps, PropClones.DustHeapOre, PropClones.DustHeapOreVanilla);
            m_DustStone = PickDust(ps, PropClones.DustHeapStone, PropClones.DustHeapStoneVanilla);

            m_SafetyPitch = math.max(MaxWidth(m_Safety), Info(m_Beacon).Size.x) + PropLayout.kBarrierGap;
            m_ConcretePitch = math.max(MaxWidth(m_Concrete), Info(m_Beacon).Size.x) + PropLayout.kBarrierGap;
            m_KerbPitch = m_SafetyPitch;
            if (m_SafetyPitch < 0.5f) m_SafetyPitch = 1.88f;
            if (m_ConcretePitch < 0.5f) m_ConcretePitch = 1.88f;
            if (m_KerbPitch < 0.5f) m_KerbPitch = 1.88f;

            // site fences fall back to the safety barriers; signals are skipped when missing
            m_SiteFence = ValidOnly(PrefabCatalog.Statics(ps, PrefabNames.SiteFences).ToArray());
            m_SiteFenceShort = ValidOnly(PrefabCatalog.Statics(ps, PrefabNames.SiteFencesShort).ToArray());
            if (m_SiteFence.Length == 0) { m_SiteFence = m_Safety; m_SiteFenceShort = new Entity[0]; RRWLog.Warn("props: site fence prefabs missing, using safety barriers"); }
            m_SiteFencePitch = LongAxis(m_SiteFence, RRWConst.kFenceSpacing);
            m_SiteShortLen = LongAxis(m_SiteFenceShort, 2f);

            // kerb fence panel pair from RRWGates.KerbFenceVariant (PrefabNames.KerbFenceVariants);
            // -1 or an unresolved panel -> SafetyBarrier line at the same lateral (same fallback as the site fences)
            int variant = RRWGates.KerbFenceVariant;
            string mainName = PrefabNames.KerbFence(variant, false), shortName = PrefabNames.KerbFence(variant, true);
            m_KerbFence = mainName != null ? ValidOnly(PrefabCatalog.Statics(ps, new[] { mainName }).ToArray()) : new Entity[0];
            m_KerbFenceShort = shortName != null ? ValidOnly(PrefabCatalog.Statics(ps, new[] { shortName }).ToArray()) : new Entity[0];
            m_FenceFallback = m_KerbFence.Length == 0;
            if (m_FenceFallback)
            {
                m_KerbFenceShort = new Entity[0];
                if (variant >= 0) RRWLog.Warn("props: kerb fence panel " + (mainName ?? "?") + " (variant " + variant + ") missing, using a safety barrier line");
            }
            m_KerbFencePitch = m_FenceFallback ? m_SafetyPitch : LongAxis(m_KerbFence, RRWConst.kFenceSpacing);
            m_KerbShortLen = LongAxis(m_KerbFenceShort, 2f);
            m_FenceCone = PrefabCatalog.Static(ps, PrefabNames.Cones[1]);   // SafetyCone02
            if (!Info(m_FenceCone).Valid) m_FenceCone = m_TallCone;

            // signs and the amber head (RRWGates.Signs / AmberHead) per theme ([0] EU, [1] NA; NA falls back to EU)
            ResolvePair(ps, m_SignOneway, PrefabNames.Oneway(false), PrefabNames.Oneway(true));
            ResolvePair(ps, m_SignNoEntry, PrefabNames.NoEntry(false), PrefabNames.NoEntry(true));
            ResolvePair(ps, m_SignalHead, PrefabNames.Signal(false), PrefabNames.Signal(true));
            m_ConeLamp = PropClones.Resolve(ps, PropClones.ConeLamp);
            if (!Info(m_ConeLamp).Valid) m_ConeLamp = Entity.Null;
            m_PrefabsResolved = true;
            RRWLog.Info("props: staged-traffic prefabs kerbFence=" + (m_FenceFallback ? "BARRIER FALLBACK" : mainName + "+" + (m_KerbFenceShort.Length > 0 ? shortName : "no short")) +
                        " (variant " + variant + ", pitch " + RRWLog.F(m_KerbFencePitch) + ", short " + RRWLog.F(m_KerbShortLen) +
                        ", bounds centre " + RRWLog.F3(Info(m_KerbFence.Length > 0 ? m_KerbFence[0] : Entity.Null).Centre) + ")" +
                        " signs EU=" + (m_SignOneway[0] != Entity.Null) + "/" + (m_SignNoEntry[0] != Entity.Null) + " NA=" + (m_SignOneway[1] != Entity.Null) + "/" + (m_SignNoEntry[1] != Entity.Null) +
                        " heads EU/NA=" + (m_SignalHead[0] != Entity.Null) + "/" + (m_SignalHead[1] != Entity.Null) + " (" + Info(m_SignalHead[0]).Flags + ")" +
                        " coneLamp=" + (m_ConeLamp != Entity.Null) + " gates: " + RRWGates.Describe());
            RRWLog.Info("props: prefabs safety=" + m_Safety.Length + " concrete=" + m_Concrete.Length + " cones=" + m_Cones.Length +
                        " rubble=" + m_Rubble.Length + " crew=" + m_Crew.Length + " heaps=" + (m_Ore != Entity.Null) + "/" + (m_Stone != Entity.Null) +
                        " beacon=" + (m_Beacon != Entity.Null) + " dustHeaps=" + (m_DustOre != Entity.Null) + "/" + (m_DustStone != Entity.Null) +
                        " dustSource=" + DustVfx.ModeName + " (" + EcsUtil.PrefabName(ps, EntityManager, m_DustOre) + ")" +
                        " pitch=" + RRWLog.F(m_SafetyPitch) + "/" + RRWLog.F(m_ConcretePitch) + " (clones: " + PropClones.Names() + ")" +
                        " fences=" + m_SiteFence.Length + "+" + m_SiteFenceShort.Length + "/" + m_KerbFence.Length + "+" + m_KerbFenceShort.Length +
                        " fencePitch=" + RRWLog.F(m_SiteFencePitch) + "/" + RRWLog.F(m_KerbFencePitch));
        }

        private void ResolvePair(PrefabSystem ps, Entity[] pair, string eu, string na)
        {
            pair[0] = PrefabCatalog.Static(ps, eu);
            pair[1] = PrefabCatalog.Static(ps, na);
            if (!Info(pair[0]).Valid) pair[0] = Entity.Null;
            if (!Info(pair[1]).Valid) pair[1] = Entity.Null;
            if (pair[0] == Entity.Null) pair[0] = pair[1];
        }

        private Entity[] ValidOnly(Entity[] list)
        {
            int n = 0;
            for (int i = 0; i < list.Length; i++) if (Info(list[i]).Valid) n++;
            if (n == list.Length) return list;
            var r = new Entity[n];
            n = 0;
            for (int i = 0; i < list.Length; i++) if (Info(list[i]).Valid) r[n++] = list[i];
            return r;
        }

        // Panel pitch of a fence family = its longest horizontal extent (barrier fallback: width + gap).
        private float LongAxis(Entity[] list, float fallback)
        {
            float l = 0f;
            for (int i = 0; i < list.Length; i++) { var sz = Info(list[i]).Size; l = math.max(l, math.max(sz.x, sz.z)); }
            if (l < 0.5f) return fallback;
            return l < 3f ? l + PropLayout.kBarrierGap : l;
        }

        // Dust carrier for a heap family: clone carrier or vanilla-effect carrier (DustVfx). In auto mode a missing preferred
        // carrier falls back to the other one, except to a clone that failed the VFX slot check.
        private Entity PickDust(PrefabSystem ps, PrefabBase clone, PrefabBase vanilla)
        {
            Entity c = PropClones.Resolve(ps, clone), v = PropClones.Resolve(ps, vanilla);
            if (!Info(c).Valid) c = Entity.Null;
            if (!Info(v).Valid) v = Entity.Null;
            switch (DustVfx.Mode)
            {
                case DustSource.Clone: return c;
                case DustSource.Vanilla: return v;
                default: return DustVfx.UseVanilla ? v : (c != Entity.Null ? c : v);
            }
        }

        internal bool DustUsesClone => m_DustOre != Entity.Null && m_DustOre == PropClones.Resolve(m_PrefabSystem, PropClones.DustHeapOre);

        private float MaxWidth(Entity[] list)
        {
            float w = 0f;
            for (int i = 0; i < list.Length; i++) w = math.max(w, Info(list[i]).Size.x);
            return w;
        }

        private static Entity Pick(Entity[] list, int i) => list == null || list.Length == 0 ? Entity.Null : list[((i % list.Length) + list.Length) % list.Length];

        private PrefabInfo Info(Entity prefab)
        {
            if (prefab == Entity.Null) return default;
            if (m_PrefabInfo.TryGetValue(prefab, out var info)) return info;
            var em = EntityManager;
            info = default;
            if (em.Exists(prefab) && em.HasComponent<ObjectData>(prefab))
            {
                info.Valid = em.GetComponentData<ObjectData>(prefab).m_Archetype.Valid;
                if (em.HasComponent<ObjectGeometryData>(prefab))
                {
                    var g = em.GetComponentData<ObjectGeometryData>(prefab);
                    info.Size = g.m_Size;
                    info.Centre = (g.m_Bounds.min + g.m_Bounds.max) * 0.5f;
                    if (math.any(math.isnan(info.Centre)) || math.any(math.abs(info.Centre) > 100f)) info.Centre = float3.zero;
                    info.Flags = g.m_Flags;
                }
                if (em.HasComponent<QuantityObjectData>(prefab))
                {
                    info.Quantity = true;
                    info.StepMask = em.GetComponentData<QuantityObjectData>(prefab).m_StepMask;
                }
            }
            if (info.Valid) m_PrefabInfo[prefab] = info;   // an archetype that is not valid yet is re-checked later
            return info;
        }

        // ------------------------------------------------------------------ hashing

        private static uint Bits(float f) => math.asuint(f);
        private static uint Mix(uint a, uint b) => BeaconPhase.Hash(a, b);

        // ------------------------------------------------------------------ introspection (rrw.dump / rrw.check)

        private string Dump(EntityManager em, Entity edge)
        {
            if (!SiteRegistry.TryGetEdge(edge, out var r) || !PropState.Projects.TryGetValue(r.ProjectId, out var pp)) return "props: none";
            var counts = new int[(int)PropKind.Count];
            int heaps = 0, explicitY = 0, owned = 0, overridden = 0, dust = 0, beacons = 0, dead = 0;
            foreach (var kv in pp.Slots)
            {
                var sl = kv.Value;
                counts[(int)PropKeys.Kind(sl.Key)]++;
                if (sl.Heap) heaps++;
                if (sl.Explicit) explicitY++;
                if (sl.Dust) dust++;
                if (sl.Prefab == m_Beacon && m_Beacon != Entity.Null) beacons++;
                if (!EcsUtil.Alive(em, sl.Entity)) { dead++; continue; }
                if (em.HasComponent<Owner>(sl.Entity)) owned++;
                if (em.HasComponent<Overridden>(sl.Entity)) overridden++;
            }
            var sb = new System.Text.StringBuilder("props: project #" + pp.Id + " total=" + pp.Slots.Count);
            for (int i = 0; i < counts.Length; i++) if (counts[i] > 0) sb.Append(' ').Append((PropKind)i).Append('=').Append(counts[i]);
            sb.Append(" heaps=" + heaps + " dust=" + dust + " beacons=" + beacons + " explicitY=" + explicitY + " owned=" + owned +
                      " overridden=" + overridden + " dead=" + dead + " thin=" + pp.Thin + " far=" + pp.Far +
                      " camChain=" + RRWLog.F(pp.CamDist) + " farChanges=" + pp.FarChanges + " dustSource=" + DustVfx.ModeName +
                      " spawned=" + pp.Spawned + " replaced=" + pp.Replaced + " moved=" + pp.Moved + " deleted=" + pp.Deleted);
            sb.Append(" | r2: barriers=" + pp.LastBarriers + " fence=" + pp.LastFence + (pp.FencesFit ? "" : "(dropped:budget)") +
                      " foreign=" + pp.Foreign.Count + " endRespawns=" + pp.EndRespawns + " healDeleted=" + pp.HealDeleted +
                      " skippedForeign=" + pp.SkippedForeign + " skippedOutside=" + pp.SkippedOutside + " released=" + pp.Released +
                      " trims=[" + RRWLog.F(pp.Trim0) + "," + RRWLog.F(pp.Trim1) + "]");
            sb.Append(" | r3: " + pp.DeviceLine + " fencesFit=" + pp.FencesFit + " signalWrites=" + pp.SignalWrites + " signalAsserts=" + pp.SignalAsserts);
            if (SiteRegistry.TryGetProject(pp.Id, out var pr))
                sb.Append(" | r4: crews=" + pr.Crews + " depots=" + pp.Depots + " [" + DepotList(pr.View()) + "] fullDiffs=" + pp.FullDiffs + " frontDiffs=" + pp.FrontDiffs +
                          " healMissing=" + pp.HealMissing);
            return sb.ToString();
        }

        private void Check(EntityManager em, List<string> problems)
        {
            try { CheckInto(em, problems, null); }
            catch (Exception e) { problems.Add("props: checker failed: " + e.Message); }
        }

        // Invariants: every prop alive, LivePath, RRWDerived, never Overridden, owner alive, explicit Y = recomputed floor
        // (+-0.05 m), heap fullness written. detail != null collects per-project lines for the dev command.
        internal void CheckInto(EntityManager em, List<string> problems, List<string> detail)
        {
            m_HeightTried = false;
            // dust-rendering invariant: dust heaps never carry a clone whose VFX slot failed the runtime check
            if (DustVfx.State < 0 && DustUsesClone)
                problems.Add("props: dust heaps use " + PrefabNames.DustVfx + " but its VFX slot check failed: " + DustVfx.Report);
            // the dig cycle's dust puff prefab (Props single writer of DustPuffDone / DustPuffOk)
            if (RRWPrefabRegistry.DustPuffDone && !RRWPrefabRegistry.DustPuffOk)
                problems.Add("props: " + PrefabNames.DustPuff + " not usable (" + PropClones.PuffState + "): the IK dig cycle runs without dust puffs");
            else if (RRWPrefabRegistry.DustPuffOk)
            {
                Entity puff = PropClones.Resolve(m_PrefabSystem, PropClones.DustPuff);
                if (puff != Entity.Null && em.HasComponent<ObjectGeometryData>(puff)
                    && (em.GetComponentData<ObjectGeometryData>(puff).m_Flags & Game.Objects.GeometryFlags.Overridable) != 0)
                    problems.Add("props: " + PrefabNames.DustPuff + " prefab " + RRWLog.E(puff) + " is Overridable again (puffs on roads would be Overridden; PuffFixup runs every 30 frames)");
            }
            foreach (var kv in PropState.Projects)
            {
                var pp = kv.Value;
                SiteRegistry.TryGetProject(pp.Id, out var p);
                int yBad = 0, yChecked = 0, outside = 0, foreign = 0, alive = 0;
                int r3Fence = 0, r3NotReady = 0, r3OnOpen = 0, r3Checked = 0;
                int depotStray = 0, depots = 0;
                ProjectView pv = p != null ? p.View() : default;
                float pvKeep = p != null ? PhasePlan.Props(pv).StartBarrierKeep : 0f;
                float worst = 0f;
                bool released = p != null && (p.Releasing || p.Phase == WorksPhase.Complete);
                foreach (var skv in pp.Slots)
                {
                    var sl = skv.Value;
                    string where = "props #" + pp.Id + " " + PropKeys.Name(sl.Key);
                    if (sl.Entity == Entity.Null) continue;   // waiting for spawn budget
                    if (!EcsUtil.Alive(em, sl.Entity)) { problems.Add(where + " entity dead"); continue; }
                    alive++;
                    // footprint and non-works roads
                    if (p != null && !sl.AllowOutside && (sl.U < p.TrimU0 - 0.1f || sl.U > (p.TrimU1 > 0f ? p.TrimU1 : p.ChainLength) + 0.1f))
                    {
                        outside++;
                        if (outside <= 3) problems.Add(where + " outside the footprint: u=" + RRWLog.F(sl.U) + " trims=[" + RRWLog.F(p.TrimU0) + "," + RRWLog.F(p.TrimU1) + "]");
                    }
                    if (em.HasComponent<ObjTransform>(sl.Entity))
                    {
                        var ftr = em.GetComponentData<ObjTransform>(sl.Entity);
                        var size = Info(sl.Prefab).Size;
                        ScanForeignNear(em, ftr.m_Position, size, m_ScratchForeign, true);
                        if (InsideForeign(m_ScratchForeign, ftr.m_Position, ftr.m_Rotation, size))
                        {
                            foreign++;
                            if (foreign <= 3) problems.Add(where + " inside a non-works road (" + m_ScratchForeign.Count + " road(s) near, first " + RRWLog.E(m_ScratchForeign[0].Edge) + ") at " + RRWLog.F3(ftr.m_Position));
                        }
                    }
                    if (!em.HasComponent<LivePath>(sl.Entity)) problems.Add(where + " has no LivePath");
                    if (!em.HasComponent<RRWDerived>(sl.Entity)) problems.Add(where + " has no RRWDerived");
                    else
                    {
                        var d = em.GetComponentData<RRWDerived>(sl.Entity);
                        if (d.m_Site != Entity.Null && !SiteRegistry.Edges.ContainsKey(d.m_Site)) problems.Add(where + " tagged with a non-registry edge " + RRWLog.E(d.m_Site));
                    }
                    if (em.HasComponent<Overridden>(sl.Entity)) problems.Add(where + " Overridden");
                    if (em.HasComponent<Owner>(sl.Entity))
                    {
                        var o = em.GetComponentData<Owner>(sl.Entity).m_Owner;
                        if (!EcsUtil.Alive(em, o)) problems.Add(where + " owner " + RRWLog.E(o) + " dead");
                    }
                    if (sl.Heap && em.HasComponent<Quantity>(sl.Entity) && em.GetComponentData<Quantity>(sl.Entity).m_Fullness != sl.Fullness)
                        problems.Add(where + " quantity " + em.GetComponentData<Quantity>(sl.Entity).m_Fullness + " != fill " + sl.Fullness);
                    if (sl.Heap && em.HasComponent<RRWHeapFill>(sl.Entity) && em.GetComponentData<RRWHeapFill>(sl.Entity).m_Fullness != sl.Fullness)
                        problems.Add(where + " RRWHeapFill " + em.GetComponentData<RRWHeapFill>(sl.Entity).m_Fullness + " != fill " + sl.Fullness);
                    if (p != null) CheckDevice(em, pp, p, sl, problems, ref r3Fence, ref r3NotReady, ref r3OnOpen, ref r3Checked);
                    // a crew depot stands only at a boundary of the CURRENT crew layout, inside that crew's section,
                    // before the teardown
                    if (p != null && PropKeys.Kind(sl.Key) == PropKind.CrewDepot && !released)
                    {
                        depots++;
                        int id = PropKeys.Sub(sl.Key) / 2, k = PropKeys.K(sl.Key);
                        int bi = PropCrewLayout.BoundaryOf(id, pv.CrewCount);
                        bool ok = bi > 0 && PropCrewLayout.DepotFill(pv, bi, k, pvKeep) >= 1;
                        if (ok)
                        {
                            float a = PhasePlan.SectionStart(bi, pv.CrewCount, pv.U), b = PhasePlan.SectionStart(bi + 1, pv.CrewCount, pv.U);
                            ok = sl.U >= a - 0.01f && sl.U <= b + 0.01f;
                        }
                        if (!ok && ++depotStray <= 3)
                            problems.Add(where + " crew depot " + PropCrewLayout.DepotName(id) + " stands but the layout has " + pv.CrewCount + " crews (" + pv.Phase + " f=" + RRWLog.F(pv.F) + ", u=" + RRWLog.F(sl.U) + ")");
                    }
                    if (sl.Explicit && p != null && (sl.Y == YMode.Floor || sl.Y == YMode.Curve || sl.Y == YMode.Panel || sl.Y == YMode.PanelSidewalk))
                    {
                        if (!em.HasComponent<ObjElevation>(sl.Entity)) problems.Add(where + " explicit-Y prop without Elevation");
                        var tr = em.GetComponentData<ObjTransform>(sl.Entity);
                        if (!TryExpectedY(pp, sl, tr.m_Position, out float expect)) continue;
                        yChecked++;
                        float err = math.abs(tr.m_Position.y - expect);
                        worst = math.max(worst, err);
                        if (err > 0.05f) { yBad++; if (yBad <= 3) problems.Add(where + " Y " + RRWLog.F(tr.m_Position.y) + " expected " + RRWLog.F(expect) + " (floor)"); }
                    }
                }
                if (released && alive > 0) problems.Add("props #" + pp.Id + " " + alive + " props still stand on a releasing / completed project");
                if (outside > 3) problems.Add("props #" + pp.Id + " " + outside + " props outside the footprint in total");
                if (foreign > 3) problems.Add("props #" + pp.Id + " " + foreign + " props inside non-works roads in total");
                // camera-distance invariant: never "far" while the camera is near any part of the chain
                if (pp.Far && pp.CamDist >= 0f && pp.CamDist < RRWConst.kPropFarDistance * 0.9f)
                    problems.Add("props #" + pp.Id + " far while the camera is " + RRWLog.F(pp.CamDist) + " m from the chain");
                if (depotStray > 3) problems.Add("props #" + pp.Id + " " + depotStray + " crew depot props off the crew layout in total");
                if (r3Fence > 3) problems.Add("props #" + pp.Id + " " + r3Fence + " kerb fence panels on sides without an open sidewalk in total");
                if (r3NotReady > 3) problems.Add("props #" + pp.Id + " " + r3NotReady + " in-lane devices on a works half that is not ready in total");
                if (r3OnOpen > 3) problems.Add("props #" + pp.Id + " " + r3OnOpen + " signs / amber heads on an open lane or sidewalk in total");
                detail?.Add("props #" + pp.Id + " slots=" + pp.Slots.Count + " camChain=" + RRWLog.F(pp.CamDist) + " yChecked=" + yChecked + " yBad=" + yBad + " worstErr=" + RRWLog.F(worst) +
                            " thin=" + pp.Thin + " far=" + pp.Far + " owners=" + pp.OwnerNodes.Count +
                            " outsideFootprint=" + outside + " insideNonWorksRoad=" + foreign + " releasedAlive=" + (released ? alive : 0) +
                            " foreign=" + pp.Foreign.Count + " endRespawns=" + pp.EndRespawns + " healDeleted=" + pp.HealDeleted +
                            " | r3: devicesChecked=" + r3Checked + " fenceNoOpenSidewalk=" + r3Fence + " inLaneNotReady=" + r3NotReady + " signOnOpen=" + r3OnOpen +
                            " signalAsserts=" + pp.SignalAsserts + " | " + pp.DeviceLine +
                            " | r4: crews=" + (p != null ? pv.CrewCount.ToString() : "-") + " depotProps=" + depots + " depotStray=" + depotStray +
                            " fullDiffs=" + pp.FullDiffs + " frontDiffs=" + pp.FrontDiffs + " frontApplied=" + pp.FrontApplied +
                            " healScanned=" + pp.HealScanned + " healMissing=" + pp.HealMissing + " healDeferred=" + pp.HealDeferred +
                            " slotList=" + pp.SlotList.Count + (pp.SlotList.Count != pp.Slots.Count ? " SLOTLIST MISMATCH" : ""));
                if (pp.SlotList.Count != pp.Slots.Count) problems.Add("props #" + pp.Id + " slot list " + pp.SlotList.Count + " != slots " + pp.Slots.Count);
            }
        }

        // Expected Y of an explicit floor/curve prop at its current XZ (re-derived from the current runtime / ground).
        private bool TryExpectedY(ProjectProps pp, PropSlot sl, float3 pos, out float y)
        {
            y = 0f;
            if (sl.Y == YMode.Panel || sl.Y == YMode.PanelSidewalk)
            {
                // chord-placed panel: min(curve Y at both feet) - kPanelDrop + lift
                if (!CurveYAt(pp, sl.FootA, out float ya) || !CurveYAt(pp, sl.FootB, out float yb)) return false;
                y = math.min(ya, yb) - PropLayout.kPanelDrop + sl.Lift;
                return true;
            }
            ChainEdge ce = null;
            for (int i = 0; i < pp.Chain.Count; i++) if (pp.Chain[i].Edge == sl.SiteEdge) { ce = pp.Chain[i]; break; }
            if (ce == null || ce.Record.Arc == null) return false;
            var em = EntityManager;
            if (!em.Exists(ce.Edge)) return false;
            float s = ce.Record.Arc.Project(pos);
            if (s <= 0.01f || s >= ce.Record.Arc.Length - 0.01f) return false;   // past the curve ends (extension): skip
            float curveY = ce.Record.Arc.Position(s).y;
            if (sl.Y == YMode.Curve) { y = curveY; return true; }
            if (em.HasComponent<RoadWorksRuntime>(ce.Edge)) { ce.Runtime = em.GetComponentData<RoadWorksRuntime>(ce.Edge); ce.HaveRuntime = true; }
            if (em.HasComponent<RoadWorksGround>(ce.Edge)) ce.Ground = em.GetComponentData<RoadWorksGround>(ce.Edge);
            ce.PlannedValid = false;
            y = FloorY(ce, curveY, pos);
            ce.PlannedValid = false;
            return true;
        }
    }
}
