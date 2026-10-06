using System;
using System.Collections.Generic;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

// Per-project chain tracks (path and floor table in one structure): the whole chain (plus
// kTrackExtra beyond both ends) sampled every 2 m with the centre-line position, the chain right vector and the ground
// heights the machines stand on (floor at the centre, wall band, verges). Built on the main thread at Modification1 from
// the registry (EdgeRecord.Arc/Section, saved chain coordinates, RoadWorksRuntime/RoadWorksGround) and packed into
// persistent native arrays that the mover job reads. Y data are refreshed in place when Ground applies a terrain step
// (RoadWorksGround.m_StepUpdate) or the road is hidden/revealed, so machines follow the floor within one update.
namespace RealisticRoadWorks.V3.Machines
{
    public struct TrackEdgeInfo
    {
        public Entity Edge;
        public float Lo, Hi;              // chain range
        public uint StepUpdate;           // RoadWorksGround.m_StepUpdate at the last Y refresh
        public byte GroundKind;
        public bool Hidden;
        public int GeoRevision;
        public bool Minimal;
        public uint LastStepAt;           // UpdateIndex of the last terrain step seen on this edge (0 = none yet)
        public uint LastRefresh;          // UpdateIndex of this edge's last Y refresh (dev check: track age)
        public EdgeSection Section;       // lateral cross-section (EDGE frame) for the lane zones / carriageway mapping
        public bool Reversed;             // the edge curve runs against +u (RoadZoneMath.ChainReversed)
        // sample range of the edge and the time-sliced Y refresh state
        public int K0, K1;                // first / last track sample of this edge
        public bool Pending;              // Y refresh queued (not under a puppet): processed <= kTrackSliceSamples per update
        public int NextK;                 // next sample of a pending refresh
        // the applied ground state the last refresh saw - a change of the applied floor caps / morph
        // re-snaps the edge even without an m_StepUpdate bump (the step stamp alone missed applied-profile writes)
        public float FloorMin, FloorMax, AppliedY, AppliedT;
    }

    public sealed class TrackData
    {
        public int Slot = -1;
        public uint ProjectId;
        public TrackSample[] Samples = Array.Empty<TrackSample>();
        public short[] EdgeOf = Array.Empty<short>();
        public int Count;
        public float U0, Ds, U;
        public readonly List<TrackEdgeInfo> Edges = new List<TrackEdgeInfo>();
        public int ProjectRevision = int.MinValue;
        public int GeoSum = int.MinValue;
        public bool Valid;
        public uint LastYRefresh;
        public int Revision;              // ++ on every rebuild (puppets re-plan)
        public int Users;                 // puppets referencing the slot (post-site puppets keep it alive)
        public bool ProjectGone;
        public bool AnyPending;           // an edge has a queued (sliced) Y refresh
    }

    public static class MachineTrackStore
    {
        private static NativeList<TrackSample> s_Samples;
        private static NativeList<TrackHeader> s_Headers;
        private static readonly List<TrackData> s_Slots = new List<TrackData>();
        private static readonly Dictionary<uint, TrackData> s_ByProject = new Dictionary<uint, TrackData>();
        private static bool s_LayoutDirty;
        private static readonly HashSet<int> s_DataDirty = new HashSet<int>();
        public static JobHandle Readers;   // mover job(s) reading the arrays

        public static int SlotCount => s_Slots.Count;

        static void Ensure()
        {
            if (!s_Samples.IsCreated) s_Samples = new NativeList<TrackSample>(1024, Allocator.Persistent);
            if (!s_Headers.IsCreated) s_Headers = new NativeList<TrackHeader>(16, Allocator.Persistent);
        }

        // Main thread: wait for job readers before any write.
        public static void CompleteReaders()
        {
            Readers.Complete();
            Readers = default;
        }

        public static TrackView View()
        {
            if (!s_Samples.IsCreated || !s_Headers.IsCreated) return default;
            return new TrackView { Samples = s_Samples.AsArray(), Headers = s_Headers.AsArray() };
        }

        public static bool TryGet(uint projectId, out TrackData td) => s_ByProject.TryGetValue(projectId, out td);

        public static TrackData GetOrCreate(uint projectId)
        {
            if (s_ByProject.TryGetValue(projectId, out var td)) return td;
            td = new TrackData { ProjectId = projectId };
            int slot = -1;
            for (int i = 0; i < s_Slots.Count; i++) if (s_Slots[i] == null) { slot = i; break; }
            if (slot < 0) { slot = s_Slots.Count; s_Slots.Add(null); }
            s_Slots[slot] = td;
            td.Slot = slot;
            s_ByProject[projectId] = td;
            s_LayoutDirty = true;
            return td;
        }

        public static void Release(TrackData td)
        {
            if (td == null || td.Slot < 0) return;
            if (td.Slot < s_Slots.Count && s_Slots[td.Slot] == td) s_Slots[td.Slot] = null;
            if (s_ByProject.TryGetValue(td.ProjectId, out var cur) && cur == td) s_ByProject.Remove(td.ProjectId);
            td.Slot = -1;
            td.Valid = false;
            s_LayoutDirty = true;
        }

        public static void MarkLayout() => s_LayoutDirty = true;
        public static void MarkData(TrackData td) { if (td != null && td.Slot >= 0) s_DataDirty.Add(td.Slot); }

        // Frees slots of vanished projects that no puppet uses any more.
        public static void Collect(List<uint> scratch)
        {
            scratch.Clear();
            foreach (var kv in s_ByProject)
            {
                var td = kv.Value;
                if (td.ProjectGone && td.Users <= 0) scratch.Add(kv.Key);
            }
            foreach (var id in scratch) Release(s_ByProject[id]);
        }

        public static IEnumerable<TrackData> All()
        {
            foreach (var td in s_Slots) if (td != null) yield return td;
        }

        // Perf: allocation-free slot access (All() allocates an iterator)
        public static TrackData SlotAt(int i) => i >= 0 && i < s_Slots.Count ? s_Slots[i] : null;

        public static void MarkGone(HashSet<uint> seen)
        {
            for (int i = 0; i < s_Slots.Count; i++)
            {
                var td = s_Slots[i];
                if (td != null && !seen.Contains(td.ProjectId)) td.ProjectGone = true;
            }
        }

        // Copies managed tracks into the native arrays (call after CompleteReaders, before any pose evaluation).
        public static void Flush()
        {
            Ensure();
            if (s_LayoutDirty)
            {
                MxPerf.Count(MxC.TrackFlushLayout);
                s_LayoutDirty = false;
                s_DataDirty.Clear();
                s_Samples.Clear();
                s_Headers.Clear();
                for (int i = 0; i < s_Slots.Count; i++)
                {
                    var td = s_Slots[i];
                    var h = new TrackHeader { Offset = s_Samples.Length, Count = 0, U0 = 0f, Ds = 1f, Valid = 0 };
                    if (td != null && td.Valid && td.Count >= 2)
                    {
                        h.Count = td.Count;
                        h.U0 = td.U0;
                        h.Ds = td.Ds;
                        h.Valid = 1;
                        for (int k = 0; k < td.Count; k++) s_Samples.Add(td.Samples[k]);
                    }
                    s_Headers.Add(h);
                }
                return;
            }
            if (s_DataDirty.Count == 0) return;
            MxPerf.Count(MxC.TrackFlushData);
            foreach (int slot in s_DataDirty)
            {
                if (slot < 0 || slot >= s_Slots.Count || slot >= s_Headers.Length) continue;
                var td = s_Slots[slot];
                var h = s_Headers[slot];
                if (td == null || !td.Valid || h.Valid == 0 || h.Count != td.Count) { s_LayoutDirty = true; continue; }
                for (int k = 0; k < td.Count; k++) s_Samples[h.Offset + k] = td.Samples[k];
            }
            s_DataDirty.Clear();
            if (s_LayoutDirty) Flush();
        }

        public static void Clear()
        {
            CompleteReaders();
            s_Slots.Clear();
            s_ByProject.Clear();
            s_DataDirty.Clear();
            s_LayoutDirty = true;
            if (s_Samples.IsCreated) s_Samples.Clear();
            if (s_Headers.IsCreated) s_Headers.Clear();
        }

        public static void Dispose()
        {
            CompleteReaders();
            if (s_Samples.IsCreated) s_Samples.Dispose();
            if (s_Headers.IsCreated) s_Headers.Dispose();
            s_Slots.Clear();
            s_ByProject.Clear();
            s_DataDirty.Clear();
        }
    }

    // Builds / refreshes one project's track from the registry (main thread, Modification1).
    public static class TrackBuilder
    {
        private static readonly List<EdgeSpan> s_Spans = new List<EdgeSpan>(16);

        struct EdgeSpan
        {
            public Entity Edge;
            public EdgeRecord Rec;
            public RoadWorksSite Site;
            public float Lo, Hi;
        }

        static int GeoSum(ProjectRecord p)
        {
            int h = p.Revision * 31 + p.Edges.Count;
            for (int i = 0; i < p.Edges.Count; i++)
                if (SiteRegistry.TryGetEdge(p.Edges[i], out var r)) h = h * 17 + r.GeometryRevision + r.Edge.Index * 3;
            return h;
        }

        // Returns true when the layout was rebuilt (puppets should re-plan).
        public static bool Update(EntityManager em, TerrainSystem terrain, ProjectRecord p, TrackData td, bool forceY)
        {
            int geo = GeoSum(p);
            bool rebuilt = false;
            if (!td.Valid || td.GeoSum != geo || td.ProjectRevision != p.Revision || math.abs(td.U - p.ChainLength) > 0.01f)
            {
                MxPerf.Begin(MxT.A_TrackBuild);
                MxPerf.Count(MxC.TrackRebuilds);
                SnapshotOld(td);
                rebuilt = Build(em, p, td);
                td.GeoSum = geo;
                td.ProjectRevision = p.Revision;
                if (rebuilt)
                {
                    td.Revision++;
                    // samples whose edge and geometry did not change keep their floor / verge heights (so a
                    // Revision change mid-game does not drop every edge to FloorRel 0 until its slice comes round)
                    CarryOver(td);
                    // edges near a puppet now, the rest time-sliced (<= kTrackSliceSamples per update)
                    for (int i = 0; i < td.Edges.Count; i++)
                    {
                        if (EdgeUnderPuppet(td, i)) RefreshY(em, terrain, td, i);
                        else MarkPending(em, td, i);
                    }
                    SmoothNodes(td);
                    td.LastYRefresh = RRWClock.UpdateIndex - (td.ProjectId * 37u) % 120u;
                    MachineTrackStore.MarkLayout();
                }
                MxPerf.End(MxT.A_TrackBuild);
                return rebuilt;
            }
            // Y refresh: per edge on terrain steps / hide changes, and AGAIN every kYResnapEvery updates for kYResnapWindow
            // updates after the edge's last step: the CPU heightmap readback lags the applied terrain step (more updates at
            // high game speed), so a refresh AT the step reads the previous terrain. In a fill section the C1 floor is the
            // terrain itself (max(natural, fill floor)) and a stale read keeps the track floor steps above the real one
            // until the 600-update sweep (seen in game: TruckB +0.174 m). Props and Surfaces re-snap the same way.
            // Everything every 600 updates.
            bool any = false;
            uint now = RRWClock.UpdateIndex;
            bool all = forceY || now - td.LastYRefresh >= 600u;   // CPU terrain readback drift (verges)
            for (int i = 0; i < td.Edges.Count; i++)
            {
                var ei = td.Edges[i];
                if (!em.Exists(ei.Edge) || !em.HasComponent<RoadWorksGround>(ei.Edge) || !em.HasComponent<RoadWorksRuntime>(ei.Edge)) continue;
                var g = em.GetComponentData<RoadWorksGround>(ei.Edge);
                var r = em.GetComponentData<RoadWorksRuntime>(ei.Edge);
                bool stepped = g.m_StepUpdate != ei.StepUpdate || r.Hidden != ei.Hidden || (byte)g.m_Kind != ei.GroundKind || AppliedChanged(g, ei);
                if (stepped && g.m_StepUpdate == ei.StepUpdate && r.Hidden == ei.Hidden && (byte)g.m_Kind == ei.GroundKind) AppliedOnlyRefreshes++;
                bool active = ei.LastStepAt != 0u && now - ei.LastStepAt <= MxConst.kYResnapWindow;
                bool resnap = active && now - ei.LastRefresh >= MxConst.kYResnapEvery;
                if (stepped || resnap || (all && forceY))
                {
                    // an edge under a puppet within this update, the others time-sliced
                    if (EdgeUnderPuppet(td, i)) { RefreshY(em, terrain, td, i); any = true; }
                    else if (!ei.Pending || stepped) MarkPending(em, td, i);
                }
                if (stepped)
                {
                    var e2 = td.Edges[i];   // RefreshY / MarkPending wrote StepUpdate / Hidden / GroundKind
                    e2.LastStepAt = math.max(1u, now);
                    td.Edges[i] = e2;
                }
            }
            if (all)
            {
                // the periodic sweep (CPU terrain readback drift of the verges): queued, sliced
                for (int i = 0; i < td.Edges.Count; i++) if (!td.Edges[i].Pending) MarkPending(em, td, i);
                td.LastYRefresh = now - (td.ProjectId * 37u) % 120u;
            }
            if (any) { SmoothNodes(td); MachineTrackStore.MarkData(td); }
            return false;
        }

        // applied floor caps / morph state differ from the ones the last refresh saw (NaN-safe: +-inf caps compare equal).
        static bool AppliedChanged(in RoadWorksGround g, in TrackEdgeInfo ei) =>
            !Same(g.m_FloorMinRel, ei.FloorMin) || !Same(g.m_FloorMaxRel, ei.FloorMax) || !Same(g.m_AppliedY, ei.AppliedY) || !Same(g.m_AppliedT, ei.AppliedT);

        static bool Same(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b)) || math.abs(a - b) < 1e-4f;

        static void SnapApplied(ref TrackEdgeInfo info, in RoadWorksGround g)
        {
            info.FloorMin = g.m_FloorMinRel;
            info.FloorMax = g.m_FloorMaxRel;
            info.AppliedY = g.m_AppliedY;
            info.AppliedT = g.m_AppliedT;
        }

        public static int AppliedOnlyRefreshes;   // dev: refreshes triggered by an applied-profile change without a step stamp bump

        // An edge whose chain range a puppet of the project stands on (last report position, +- kNearPuppet).
        // The margin covers a few seconds of driving (a truck at 5.5 m/s reaches the next edge before its slice).
        const float kNearPuppet = 40f;
        static bool EdgeUnderPuppet(TrackData td, int ei)
        {
            var e = td.Edges[ei];
            var all = MachineRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p.ProjectId != td.ProjectId || float.IsNaN(p.NowU)) continue;
                if (p.NowU >= e.Lo - kNearPuppet && p.NowU <= e.Hi + kNearPuppet) return true;
            }
            return false;
        }

        // Refreshes NOW every queued / never-refreshed edge that overlaps [u0, u1] (spawn point of a new puppet, whose NowU is
        // still NaN), so a freshly rebuilt track never leaves it at FloorRel 0. Returns true when something was refreshed (MarkData
        // done; the director's second Flush packs it before the mover runs).
        public static bool EnsureY(EntityManager em, TerrainSystem terrain, TrackData td, float u0, float u1)
        {
            if (td == null || !td.Valid) return false;
            bool any = false;
            for (int i = 0; i < td.Edges.Count; i++)
            {
                var e = td.Edges[i];
                if (!e.Pending && e.StepUpdate != uint.MaxValue) continue;
                if (e.Hi < u0 || e.Lo > u1) continue;
                MxPerf.Count(MxC.TrackSpawnRefresh);
                RefreshRange(em, terrain, td, i, e.K0, e.K1, true);
                e = td.Edges[i];
                e.Pending = false;
                td.Edges[i] = e;
                any = true;
            }
            if (any)
            {
                td.AnyPending = false;
                for (int i = 0; i < td.Edges.Count; i++) if (td.Edges[i].Pending) { td.AnyPending = true; break; }
                SmoothNodes(td);
                MachineTrackStore.MarkData(td);
            }
            return any;
        }

        // ---- carry floor / verge heights over a rebuild (no allocation: grow-only scratch)
        static TrackSample[] s_OldSamples = Array.Empty<TrackSample>();
        static short[] s_OldEdgeOf = Array.Empty<short>();
        static readonly List<TrackEdgeInfo> s_OldEdges = new List<TrackEdgeInfo>(16);
        static int s_OldCount;
        static float s_OldU0, s_OldDs;

        static void SnapshotOld(TrackData td)
        {
            s_OldCount = 0;
            s_OldEdges.Clear();
            if (!td.Valid || td.Count < 2) return;
            if (s_OldSamples.Length < td.Count) { s_OldSamples = new TrackSample[td.Count]; s_OldEdgeOf = new short[td.Count]; }
            Array.Copy(td.Samples, s_OldSamples, td.Count);
            Array.Copy(td.EdgeOf, s_OldEdgeOf, td.Count);
            for (int i = 0; i < td.Edges.Count; i++) s_OldEdges.Add(td.Edges[i]);
            s_OldCount = td.Count;
            s_OldU0 = td.U0;
            s_OldDs = td.Ds;
        }

        static void CarryOver(TrackData td)
        {
            if (s_OldCount < 2 || !(s_OldDs > 0f)) return;
            int carried = 0;
            for (int k = 0; k < td.Count; k++)
            {
                float u = td.U0 + k * td.Ds;
                int ko = (int)math.round((u - s_OldU0) / s_OldDs);
                if (ko < 0 || ko >= s_OldCount) continue;
                int oe = s_OldEdgeOf[ko], ne = td.EdgeOf[k];
                if (oe < 0 || oe >= s_OldEdges.Count || ne < 0 || ne >= td.Edges.Count) continue;
                var oInfo = s_OldEdges[oe];
                var nInfo = td.Edges[ne];
                if (oInfo.Edge != nInfo.Edge || oInfo.GeoRevision != nInfo.GeoRevision || oInfo.StepUpdate == uint.MaxValue) continue;
                var o = s_OldSamples[ko];
                var n = td.Samples[k];
                if (math.distancesq(o.Pos.xz, n.Pos.xz) > 0.25f || math.abs(o.Pos.y - n.Pos.y) > 0.2f) continue;
                n.FloorRel = o.FloorRel + (o.Pos.y - n.Pos.y);
                n.VergeL = float.IsNaN(o.VergeL) ? float.NaN : o.VergeL + (o.Pos.y - n.Pos.y);
                n.VergeR = float.IsNaN(o.VergeR) ? float.NaN : o.VergeR + (o.Pos.y - n.Pos.y);
                n.Flags = (byte)((n.Flags & ~6) | (o.Flags & 6));
                td.Samples[k] = n;
                carried++;
            }
            s_OldCount = 0;
            s_OldEdges.Clear();
            if (carried > 0) MxPerf.Count(MxC.TrackCarryOver);
        }

        // Queue a time-sliced Y refresh of edge ei (its StepUpdate / Hidden / GroundKind snapshot is taken now, so the same step
        // does not queue it again; a newer step restarts it).
        static void MarkPending(EntityManager em, TrackData td, int ei)
        {
            var info = td.Edges[ei];
            if (em.Exists(info.Edge) && em.HasComponent<RoadWorksRuntime>(info.Edge) && em.HasComponent<RoadWorksGround>(info.Edge))
            {
                var g = em.GetComponentData<RoadWorksGround>(info.Edge);
                info.StepUpdate = g.m_StepUpdate;
                info.Hidden = em.GetComponentData<RoadWorksRuntime>(info.Edge).Hidden;
                info.GroundKind = (byte)g.m_Kind;
                SnapApplied(ref info, g);
            }
            info.Pending = true;
            info.NextK = info.K0;
            td.Edges[ei] = info;
            td.AnyPending = true;
            MxPerf.Count(MxC.TrackSliceQueued);
        }

        private static int s_SliceBudget;

        // Processes queued edge refreshes of every track, <= kTrackSliceSamples samples per update in total.
        public static void ProcessSlices(EntityManager em, TerrainSystem terrain)
        {
            s_SliceBudget = MxConst.kTrackSliceSamples;
            // queued edges a puppet now stands on or will reach within a few seconds (+- kNearPuppet) are done
            // whole first, outside the budget (bounded by the puppets: an edge is ~10-100 samples)
            for (int si = 0; si < MachineTrackStore.SlotCount; si++)
            {
                var td = MachineTrackStore.SlotAt(si);
                if (td == null || !td.Valid || !td.AnyPending) continue;
                bool any = false, left = false;
                for (int ei = 0; ei < td.Edges.Count; ei++)
                {
                    var info = td.Edges[ei];
                    if (!info.Pending) continue;
                    if (!EdgeUnderPuppet(td, ei)) { left = true; continue; }
                    MxPerf.Begin(MxT.A_TrackSlice);
                    RefreshRange(em, terrain, td, ei, info.NextK, info.K1, false);
                    MxPerf.End(MxT.A_TrackSlice);
                    info = td.Edges[ei];
                    info.Pending = false;
                    td.Edges[ei] = info;
                    any = true;
                }
                td.AnyPending = left;
                if (any) { SmoothNodes(td); MachineTrackStore.MarkData(td); }
            }
            for (int si = 0; si < MachineTrackStore.SlotCount && s_SliceBudget > 0; si++)
            {
                var td = MachineTrackStore.SlotAt(si);
                if (td == null || !td.Valid || !td.AnyPending) continue;
                MxPerf.Begin(MxT.A_TrackSlice);
                bool any = false, left = false;
                for (int ei = 0; ei < td.Edges.Count; ei++)
                {
                    var info = td.Edges[ei];
                    if (!info.Pending) continue;
                    if (s_SliceBudget <= 0) { left = true; break; }
                    int k1 = math.min(info.K1, info.NextK + s_SliceBudget - 1);
                    RefreshRange(em, terrain, td, ei, info.NextK, k1, false);
                    s_SliceBudget -= k1 - info.NextK + 1;
                    info = td.Edges[ei];
                    info.NextK = k1 + 1;
                    if (info.NextK > info.K1) info.Pending = false;
                    else left = true;
                    td.Edges[ei] = info;
                    any = true;
                }
                td.AnyPending = left;
                if (any) { SmoothNodes(td); MachineTrackStore.MarkData(td); }
                MxPerf.End(MxT.A_TrackSlice);
            }
        }

        static bool Build(EntityManager em, ProjectRecord p, TrackData td)
        {
            s_Spans.Clear();
            td.Edges.Clear();
            for (int i = 0; i < p.Edges.Count; i++)
            {
                Entity e = p.Edges[i];
                if (!SiteRegistry.TryGetEdge(e, out var rec) || rec.Arc == null) continue;
                if (!em.Exists(e) || !em.HasComponent<RoadWorksSite>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                s_Spans.Add(new EdgeSpan { Edge = e, Rec = rec, Site = site, Lo = site.ChainLo, Hi = site.ChainHi });
            }
            if (s_Spans.Count == 0) { td.Valid = false; return false; }
            s_Spans.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            float U = math.max(1f, p.ChainLength);
            float extra = MxConst.kTrackExtra;
            float span = U + 2f * extra;
            float ds = math.max(MxConst.kTrackStep, span / (MxConst.kMaxTrackSamples - 1));
            int count = (int)math.ceil(span / ds) + 1;
            if (td.Samples.Length < count) { td.Samples = new TrackSample[count]; td.EdgeOf = new short[count]; }
            td.Count = count;
            td.U0 = -extra;
            td.Ds = ds;
            td.U = p.ChainLength;
            for (int i = 0; i < s_Spans.Count; i++)
            {
                var sp = s_Spans[i];
                td.Edges.Add(new TrackEdgeInfo
                {
                    Edge = sp.Edge, Lo = sp.Lo, Hi = sp.Hi, GeoRevision = sp.Rec.GeometryRevision,
                    Minimal = sp.Site.Mode == VisualMode.Minimal, StepUpdate = uint.MaxValue, GroundKind = 255,
                    Section = sp.Rec.Section, Reversed = RoadZoneMath.ChainReversed(sp.Site.m_ChainU0, sp.Site.m_ChainU1),
                });
            }
            int cur = 0;
            for (int k = 0; k < count; k++)
            {
                float u = td.U0 + k * ds;
                while (cur + 1 < s_Spans.Count && u > s_Spans[cur].Hi + 1e-3f) cur++;
                var sp = s_Spans[cur];
                var arc = sp.Rec.Arc;
                float u0 = sp.Site.m_ChainU0, u1 = sp.Site.m_ChainU1;
                float den = u1 - u0;
                float s = math.abs(den) > 1e-3f ? (u - u0) / den * arc.Length : 0f;
                float sc = math.clamp(s, 0f, arc.Length);
                float3 pos = arc.Offset(s, 0f);
                float3 dir = arc.Direction(sc) * (den >= 0f ? 1f : -1f);
                float3 right = EdgeArc.RightOf(dir);
                var sec = sp.Rec.Section;
                byte flags = 0;
                if (sp.Site.Mode == VisualMode.Minimal) flags |= 1;
                if (u < 0f || u > U) flags |= 8;
                td.Samples[k] = new TrackSample
                {
                    Pos = pos,
                    Right = math.normalizesafe(right.xz, new float2(1f, 0f)),
                    FloorRel = 0f,
                    VergeL = float.NaN,
                    VergeR = float.NaN,
                    HalfWidth = math.max(2f, sec.HalfWidth),
                    FlatHalf = math.clamp(sec.FlatHalfWidth, 1f, math.max(1f, sec.HalfWidth)),
                    Flags = flags,
                };
                td.EdgeOf[k] = (short)cur;
            }
            // sample range of every edge (EdgeOf is monotonic along the track)
            for (int i = 0; i < td.Edges.Count; i++) { var ei = td.Edges[i]; ei.K0 = int.MaxValue; ei.K1 = -1; td.Edges[i] = ei; }
            for (int k = 0; k < count; k++)
            {
                int e = td.EdgeOf[k];
                var ei = td.Edges[e];
                if (k < ei.K0) ei.K0 = k;
                if (k > ei.K1) ei.K1 = k;
                td.Edges[e] = ei;
            }
            for (int i = 0; i < td.Edges.Count; i++) { var ei = td.Edges[i]; if (ei.K1 < ei.K0) { ei.K0 = 0; ei.K1 = -1; } td.Edges[i] = ei; }
            td.AnyPending = false;
            td.Valid = true;
            return true;
        }

        // Recomputes floor / verge heights for one edge (edgeIndex) or all (-1).
        public static void RefreshY(EntityManager em, TerrainSystem terrain, TrackData td, int edgeIndex)
        {
            if (!td.Valid) return;
            MxPerf.Begin(MxT.A_TrackRefreshY);
            MxPerf.CountIf(edgeIndex < 0, MxC.TrackRefreshAll);
            for (int ei = 0; ei < td.Edges.Count; ei++)
            {
                if (edgeIndex >= 0 && ei != edgeIndex) continue;
                var info = td.Edges[ei];
                RefreshRange(em, terrain, td, ei, info.K0, info.K1, true);
                info = td.Edges[ei];
                info.Pending = false;
                td.Edges[ei] = info;
            }
            SmoothNodes(td);
            // stagger the periodic full refresh of different projects
            td.LastYRefresh = edgeIndex < 0 ? RRWClock.UpdateIndex - (td.ProjectId * 37u) % 120u : td.LastYRefresh;
            MxPerf.End(MxT.A_TrackRefreshY);
        }

        // Floor / verge heights of samples [k0, k1] of edge ei (per range, so refreshes can be time-sliced).
        static void RefreshRange(EntityManager em, TerrainSystem terrain, TrackData td, int ei, int k0, int k1, bool snapshot)
        {
            MxPerf.Count(MxC.TrackRefreshEdges);
            var info = td.Edges[ei];
            Entity e = info.Edge;
            bool have = em.Exists(e) && em.HasComponent<RoadWorksRuntime>(e) && em.HasComponent<RoadWorksGround>(e) && em.HasComponent<RoadWorksSite>(e);
            RoadWorksRuntime rt = default;
            RoadWorksGround gd = default;
            TerrainProfile planned = TerrainProfile.Vanilla;
            if (have)
            {
                rt = em.GetComponentData<RoadWorksRuntime>(e);
                gd = em.GetComponentData<RoadWorksGround>(e);
                var site = em.GetComponentData<RoadWorksSite>(e);
                try { planned = PhasePlan.Terrain(PlanInput.From(site, rt)); } catch { planned = TerrainProfile.Vanilla; }
                if (snapshot)
                {
                    info.StepUpdate = gd.m_StepUpdate;
                    info.Hidden = rt.Hidden;
                    info.GroundKind = (byte)gd.m_Kind;
                    SnapApplied(ref info, gd);
                }
            }
            info.LastRefresh = RRWClock.UpdateIndex;
            td.Edges[ei] = info;
            int terrainSamples = 0;
            k0 = math.max(0, k0);
            k1 = math.min(td.Count - 1, k1);
            for (int k = k0; k <= k1; k++)
            {
                if (td.EdgeOf[k] != ei) continue;
                var smp = td.Samples[k];
                float3 c = smp.Pos;
                float tc = EcsUtil.TerrainY(terrain, c);
                float floor = FloorRel(have, rt, gd, planned, c, tc, (smp.Flags & 8) != 0);
                float vo = smp.HalfWidth + RRWConst.kVergeOffset;
                float3 r3 = new float3(smp.Right.x, 0f, smp.Right.y);
                float yl = EcsUtil.TerrainY(terrain, c - r3 * vo);
                float yr = EcsUtil.TerrainY(terrain, c + r3 * vo);
                smp.FloorRel = math.clamp(floor, -RRWConst.kMaxDepth - 2f, 3f);
                smp.VergeL = float.IsNaN(yl) ? float.NaN : math.clamp(yl - c.y, -10f, 10f);
                smp.VergeR = float.IsNaN(yr) ? float.NaN : math.clamp(yr - c.y, -10f, 10f);
                td.Samples[k] = smp;
                terrainSamples += 3;
            }
            MxPerf.Count(MxC.TerrainSamples, terrainSamples);
        }

        // THE floor function of the track and of rrw.mx.check (one function for both): floor relative to the
        // centre-line point c (curve Y) given the natural ground tc there. Beyond the chain: natural ground; hidden: GroundMath.FloorWorldY
        // of the applied (else planned) profile; visible: the road surface.
        public static float FloorRel(bool have, in RoadWorksRuntime rt, in RoadWorksGround gd, in TerrainProfile planned, float3 c, float tc, bool beyond)
        {
            if (beyond) return float.IsNaN(tc) ? 0f : math.clamp(tc - c.y, -3f, 3f);
            if (have && rt.Hidden)
            {
                float fy = GroundMath.FloorWorldY(rt, gd, planned, c.y, tc);
                return float.IsNaN(fy) ? 0f : fy - c.y;
            }
            return 0f;
        }

        // Diagnosis of the track floor at chain u (rrw.mx.check worstFloorErr line): the edge, its applied vs planned
        // profile, the sample spacing, the distance to the edge ends / nodes, the track age, the step stamp the track saw vs the current
        // one, and the track samples bracketing u.
        public static string FloorDiag(EntityManager em, TrackData td, float u)
        {
            if (td == null || !td.Valid) return "no track";
            int ei = -1;
            for (int i = 0; i < td.Edges.Count; i++) if (u >= td.Edges[i].Lo - 1e-3f && u <= td.Edges[i].Hi + 1e-3f) { ei = i; break; }
            if (ei < 0) return "u outside the edges";
            var info = td.Edges[ei];
            var sb = new System.Text.StringBuilder();
            sb.Append("edge#").Append(ei).Append('=').Append(RRWLog.E(info.Edge)).Append(" [").Append(RRWLog.F(info.Lo)).Append(',').Append(RRWLog.F(info.Hi)).Append(']')
              .Append(" edgeDist=").Append(RRWLog.F(math.min(u - info.Lo, info.Hi - u)));
            float nd = float.MaxValue;
            if (ei > 0) nd = math.min(nd, u - info.Lo);
            if (ei < td.Edges.Count - 1) nd = math.min(nd, info.Hi - u);
            sb.Append(" nodeDist=").Append(nd == float.MaxValue ? "none" : RRWLog.F(nd)).Append(" ds=").Append(RRWLog.F(td.Ds));
            int k = math.clamp((int)math.floor((u - td.U0) / td.Ds), 0, td.Count - 2);
            sb.Append(" samples k").Append(k).Append('=').Append(RRWLog.F(td.Samples[k].FloorRel)).Append('/').Append(RRWLog.F(td.Samples[k + 1].FloorRel));
            sb.Append(" trackAge=").Append(unchecked((int)(RRWClock.UpdateIndex - info.LastRefresh))).Append(info.Pending ? " PENDING" : "");
            if (em.Exists(info.Edge) && em.HasComponent<RoadWorksGround>(info.Edge) && em.HasComponent<RoadWorksRuntime>(info.Edge) && em.HasComponent<RoadWorksSite>(info.Edge))
            {
                var g = em.GetComponentData<RoadWorksGround>(info.Edge);
                var rt = em.GetComponentData<RoadWorksRuntime>(info.Edge);
                TerrainProfile planned = TerrainProfile.Vanilla;
                try { planned = PhasePlan.Terrain(PlanInput.From(em.GetComponentData<RoadWorksSite>(info.Edge), rt)); } catch { }
                sb.Append(" stepSeen=").Append(info.StepUpdate == uint.MaxValue ? "never" : info.StepUpdate.ToString()).Append(" stepNow=").Append(g.m_StepUpdate)
                  .Append(g.m_StepUpdate != info.StepUpdate ? " STEP-NOT-SEEN" : "").Append(AppliedChanged(g, info) ? " APPLIED-CHANGED" : "")
                  .Append(" applied=").Append(g.m_Kind).Append("[").Append(RRWLog.F(g.m_FloorMaxRel)).Append(',').Append(RRWLog.F(g.m_FloorMinRel)).Append("] t=").Append(RRWLog.F(g.m_AppliedT))
                  .Append(" planned=").Append(planned.Kind).Append("[").Append(RRWLog.F(planned.MiddleFloor)).Append(',').Append(RRWLog.F(planned.MiddleCap)).Append(']')
                  .Append(" hidden=").Append(rt.Hidden).Append(" sinceStep=").Append(unchecked((int)(RRWClock.UpdateIndex - g.m_StepUpdate)));
            }
            return sb.ToString();
        }

        // Floor steps between edges of different depth (C1, D2): blend over the measured ramp half-width (~1.75 m) so a
        // machine crossing a node climbs a ramp instead of popping.
        static void SmoothNodes(TrackData td)
        {
            const float half = 1.75f;
            for (int ei = 0; ei + 1 < td.Edges.Count; ei++)
            {
                float un = td.Edges[ei].Hi;
                int kL = (int)math.floor((un - 2.5f - td.U0) / td.Ds);
                int kR = (int)math.ceil((un + 2.5f - td.U0) / td.Ds);
                if (kL < 0 || kR >= td.Count) continue;
                float fl = td.Samples[kL].FloorRel, fr = td.Samples[kR].FloorRel;
                if (math.abs(fl - fr) < 0.02f) continue;
                for (int k = kL + 1; k < kR; k++)
                {
                    float u = td.U0 + k * td.Ds;
                    var smp = td.Samples[k];
                    smp.FloorRel = math.lerp(fl, fr, MachineMotion.Smooth((u - (un - half)) / (2f * half)));
                    td.Samples[k] = smp;
                }
            }
        }

        // Floor step across the node at the start of the edge containing u (C1 truck wait rule).
        public static float StepBehind(TrackData td, float u, out float edgeLo)
        {
            edgeLo = 0f;
            if (td == null || !td.Valid) return 0f;
            for (int i = 0; i < td.Edges.Count; i++)
            {
                if (u >= td.Edges[i].Lo && u <= td.Edges[i].Hi + 1e-3f)
                {
                    edgeLo = td.Edges[i].Lo;
                    if (i == 0) return 0f;
                    return math.abs(Floor(td, edgeLo + 3f) - Floor(td, edgeLo - 3f));
                }
            }
            return 0f;
        }

        // Updates since the last Y refresh of the edge containing u (dev check), -1 when unknown.
        public static int YAge(TrackData td, float u)
        {
            if (td == null || !td.Valid) return -1;
            for (int i = 0; i < td.Edges.Count; i++)
                if (u >= td.Edges[i].Lo - 1e-3f && u <= td.Edges[i].Hi + 1e-3f)
                    return unchecked((int)(RRWClock.UpdateIndex - td.Edges[i].LastRefresh));
            return -1;
        }

        public static float Floor(TrackData td, float u)
        {
            if (td == null || !td.Valid || td.Count < 2) return 0f;
            int k = math.clamp((int)math.round((u - td.U0) / td.Ds), 0, td.Count - 1);
            return td.Samples[k].FloorRel;
        }

        // The works edge whose chain range holds u (nearest beyond the ends); Entity.Null without a track.
        public static Entity EdgeAt(TrackData td, float u)
        {
            if (td == null || !td.Valid || td.Edges.Count == 0) return Entity.Null;
            int best = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < td.Edges.Count; i++)
            {
                var e = td.Edges[i];
                float d = u < e.Lo ? e.Lo - u : u > e.Hi ? u - e.Hi : 0f;
                if (d < bd) { bd = d; best = i; if (d <= 0f) break; }
            }
            return td.Edges[best].Edge;
        }

        public static TrackSample At(TrackData td, float u)
        {
            if (td == null || !td.Valid || td.Count < 1) return default;
            int k = math.clamp((int)math.round((u - td.U0) / td.Ds), 0, td.Count - 1);
            return td.Samples[k];
        }

        // Cross-section of the edge under chain u (nearest edge beyond the chain ends). False without a valid track.
        public static bool SectionAt(TrackData td, float u, out EdgeSection sec, out bool reversed)
        {
            sec = default;
            reversed = false;
            if (td == null || !td.Valid || td.Edges.Count == 0) return false;
            int best = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < td.Edges.Count; i++)
            {
                var e = td.Edges[i];
                float d = u < e.Lo ? e.Lo - u : u > e.Hi ? u - e.Hi : 0f;
                if (d < bd) { bd = d; best = i; if (d <= 0f) break; }
            }
            sec = td.Edges[best].Section;
            reversed = td.Edges[best].Reversed;
            if (!(sec.HalfWidth > 0.5f)) return false;
            return true;
        }

        // Carriageway edges (CHAIN frame, lo < hi) at chain u. Falls back to +-(HalfWidth - 3) like EdgeSection.
        public static bool CarriageAt(TrackData td, float u, out float lo, out float hi)
        {
            if (SectionAt(td, u, out var sec, out bool rev))
            {
                RoadZoneMath.CarriageChain(sec, rev, out lo, out hi);
                if (hi - lo > 0.5f) return true;
            }
            var smp = At(td, u);
            float hw = smp.HalfWidth > 0f ? smp.HalfWidth : 6f;
            hi = math.max(1f, hw - 3f);
            lo = -hi;
            return false;
        }

        // Smallest distance from the centre line to a carriageway edge over the whole chain (K-turn fit).
        public static float CarriageHalfMin(TrackData td)
        {
            if (td == null || !td.Valid || td.Edges.Count == 0) return 0f;
            float m = float.MaxValue;
            for (int i = 0; i < td.Edges.Count; i++)
            {
                var sec = td.Edges[i].Section;
                m = math.min(m, math.min(-sec.CarriageLo, sec.CarriageHi));
            }
            return m == float.MaxValue ? 0f : math.max(0f, m);
        }

        // True when the chain range [a, b] crosses a mode-D (Minimal) edge.
        public static bool CrossesMinimal(TrackData td, float a, float b)
        {
            if (td == null || !td.Valid) return false;
            if (a > b) { float x = a; a = b; b = x; }
            for (int i = 0; i < td.Edges.Count; i++)
                if (td.Edges[i].Minimal && td.Edges[i].Hi > a && td.Edges[i].Lo < b) return true;
            return false;
        }
    }
}
