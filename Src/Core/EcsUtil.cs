using System.Collections.Generic;
using Colossal.Mathematics;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetSubLane = Game.Net.SubLane;
using NetParkingLane = Game.Net.ParkingLane;
using NetTrackLane = Game.Net.TrackLane;
using NetElevation = Game.Net.Elevation;
using NetMasterLane = Game.Net.MasterLane;

namespace RealisticRoadWorks.V3
{
    // Main-thread EntityManager helpers shared by the modules. No structural changes outside Mod1/Mod4/Serialize
    // callers' own phases (the caller is responsible for calling these in a phase where that is allowed).
    public static class EcsUtil
    {
        public static bool Alive(EntityManager em, Entity e) => e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e);

        public static bool ValidComposition(EntityManager em, Entity c) => c != Entity.Null && em.Exists(c) && em.HasComponent<NetCompositionData>(c);

        // ---- visibility (verified in game: Hidden + BatchesUpdated; LaneHidden/SubObjectHidden propagate at Mod5)

        // Returns true if it changed anything. Director rule: call with hide=true every frame while
        // hidden, but with hide=false ONLY on the true->false transition frame (vanilla tool previews hide originals).
        public static bool SetHidden(EntityManager em, Entity e, bool hide)
        {
            if (!Alive(em, e)) return false;
            bool hidden = em.HasComponent<Hidden>(e);
            if (hidden == hide) return false;
            if (hide) em.AddComponent<Hidden>(e); else em.RemoveComponent<Hidden>(e);
            if (!em.HasComponent<BatchesUpdated>(e)) em.AddComponent<BatchesUpdated>(e);
            return true;
        }

        // ---- network topology

        public static void ConnectedEdges(EntityManager em, Entity node, List<Entity> output)
        {
            output.Clear();
            if (!Alive(em, node) || !em.HasBuffer<ConnectedEdge>(node)) return;
            var buf = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < buf.Length; i++)
                if (Alive(em, buf[i].m_Edge)) output.Add(buf[i].m_Edge);
        }

        public static void SubLanes(EntityManager em, Entity edge, List<Entity> output)
        {
            output.Clear();
            if (!Alive(em, edge) || !em.HasBuffer<NetSubLane>(edge)) return;
            var buf = em.GetBuffer<NetSubLane>(edge, true);
            for (int i = 0; i < buf.Length; i++) output.Add(buf[i].m_SubLane);
        }

        // PathfindUpdated on the edge's lanes: LaneDataSystem/ParkingLaneDataSystem recompute them this frame (if called
        // at Mod1) and the Traffic closure systems re-apply right after. Never Updated (that regenerates lanes).
        public static int RefreshLanes(EntityManager em, Entity edge, bool carOnly)
        {
            if (!Alive(em, edge) || !em.HasBuffer<NetSubLane>(edge)) return 0;
            var lanes = em.GetBuffer<NetSubLane>(edge, true).ToNativeArray(Unity.Collections.Allocator.Temp);
            int n = 0;
            for (int i = 0; i < lanes.Length; i++)
            {
                var l = lanes[i].m_SubLane;
                if (!em.Exists(l) || em.HasComponent<Deleted>(l)) continue;
                if (carOnly && !em.HasComponent<NetCarLane>(l)) continue;
                if (!em.HasComponent<PathfindUpdated>(l)) { em.AddComponent<PathfindUpdated>(l); n++; }
            }
            lanes.Dispose();
            return n;
        }

        // ---- eligibility for mode A (port of the terrain prototype's eligibility rules, verified in game)

        public static bool DigEligible(EntityManager em, Entity edge, out string why)
        {
            why = "ok";
            if (!Alive(em, edge) || !em.HasComponent<PrefabRef>(edge)) { why = "no-prefab"; return false; }
            Entity prefab = em.GetComponentData<PrefabRef>(edge).m_Prefab;
            if (!em.HasComponent<NetGeometryData>(prefab)) { why = "no-geometry"; return false; }
            var ngd = em.GetComponentData<NetGeometryData>(prefab);
            if ((ngd.m_MergeLayers & Layer.Waterway) != Layer.None) { why = "waterway"; return false; }
            bool clip = (ngd.m_Flags & Game.Net.GeometryFlags.ClipTerrain) != 0;
            bool flat = (ngd.m_Flags & Game.Net.GeometryFlags.FlattenTerrain) != 0;
            if (!clip && !flat) { why = "no-terrain-interaction"; return false; }
            Entity comp = em.HasComponent<Composition>(edge) ? em.GetComponentData<Composition>(edge).m_Edge : Entity.Null;
            if (ValidComposition(em, comp))
            {
                var cd = em.GetComponentData<NetCompositionData>(comp);
                if ((cd.m_State & CompositionState.HasSurface) == 0) { why = "no-surface"; return false; }
                if ((cd.m_Flags.m_General & CompositionFlags.General.Elevated) != 0) { why = "elevated"; return false; }
                if ((cd.m_Flags.m_General & CompositionFlags.General.Tunnel) != 0) { why = "tunnel"; return false; }
                if ((cd.m_Flags.m_Left & CompositionFlags.Side.Lowered) != 0 || (cd.m_Flags.m_Right & CompositionFlags.Side.Lowered) != 0) { why = "lowered"; return false; }
                return true;
            }
            // Composition not selected yet (a temp created in the apply frame: GenerateEdgesSystem points Composition at
            // the PREFAB until CompositionSelectSystem runs at Mod3). Decide from the edge's own components instead.
            if (em.HasComponent<NetElevation>(edge))
            {
                float2 el = em.GetComponentData<NetElevation>(edge).m_Elevation;
                if (math.any(el > 0.5f)) { why = "elevated(prefab-path)"; return false; }
                if (math.any(el < -0.5f)) { why = "tunnel-or-lowered(prefab-path)"; return false; }
            }
            if (em.HasComponent<Upgraded>(edge))
            {
                var uf = em.GetComponentData<Upgraded>(edge).m_Flags;
                if ((uf.m_Left & CompositionFlags.Side.Lowered) != 0 || (uf.m_Right & CompositionFlags.Side.Lowered) != 0) { why = "lowered(prefab-path)"; return false; }
                if ((uf.m_General & (CompositionFlags.General.Elevated | CompositionFlags.General.Tunnel)) != 0) { why = "elevated(prefab-path)"; return false; }
            }
            why = "ok(prefab-path)";
            return true;
        }

        public static float CompositionWidth(EntityManager em, Entity edge)
        {
            if (em.HasComponent<Composition>(edge))
            {
                var c = em.GetComponentData<Composition>(edge).m_Edge;
                if (ValidComposition(em, c)) return em.GetComponentData<NetCompositionData>(c).m_Width;
            }
            if (em.HasComponent<PrefabRef>(edge))
            {
                var prefab = em.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (em.HasComponent<NetGeometryData>(prefab)) return em.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth;
            }
            return 8f;
        }

        // Cross-section: half width, flat floor half width (TerrainSystem middleSize), grade offset
        // (NetCompositionData.m_SurfaceHeight.min; clones carry the same data), node trims from EdgeGeometry, and the lane-derived
        // part via LaneSection.Apply: carriage intervals = union of the car, parking and on-road track lanes ALONG the edge (each
        // +-width/2, sampled at t = .1 .. .9 of every lane, merged across gaps < kCarriageMergeGap: a median stays a gap); DirSplit /
        // DriveLo / DriveHi / lane lines / Verdict from the through lanes. Every lane is measured from its own geometry against the
        // edge frame at its own projected point (EcsUtil.ProbeLanes / LaneSection.Probe), so curved and reversed edges measure like
        // straight ones; connectors across the road (auxiliary lanes), two-way, bicycle-only and master lanes never veto the split.
        public static EdgeSection MeasureSection(EntityManager em, Entity edge, EdgeArc arc)
        {
            var sec = new EdgeSection();
            sec.CompositionWidth = CompositionWidth(em, edge);
            sec.HalfWidth = sec.CompositionWidth * 0.5f;
            sec.FlatHalfWidth = EdgeSection.FlatHalfWidthOf(sec.CompositionWidth);
            sec.GradeOffset = RRWConst.kProfileGradeBias;
            if (em.HasComponent<Composition>(edge))
            {
                var c = em.GetComponentData<Composition>(edge).m_Edge;
                if (ValidComposition(em, c))
                {
                    float sh = em.GetComponentData<NetCompositionData>(c).m_SurfaceHeight.min;
                    if (!float.IsNaN(sh) && math.abs(sh) < 5f) sec.GradeOffset = sh;
                }
            }
            if (em.HasComponent<EdgeGeometry>(edge))
            {
                var g = em.GetComponentData<EdgeGeometry>(edge);
                float a = math.max(arc.Project(g.m_Start.m_Left.a), arc.Project(g.m_Start.m_Right.a));
                float d = math.min(arc.Project(g.m_End.m_Left.d), arc.Project(g.m_End.m_Right.d));
                sec.TrimStart = math.clamp(a, 0f, arc.Length * 0.5f);
                sec.TrimEnd = math.clamp(arc.Length - d, 0f, arc.Length * 0.5f);
            }
            var probes = s_Probes;
            ProbeLanes(em, edge, arc, probes, null);
            LaneSection.Apply(ref sec, probes);
            return sec;
        }

        // The car, parking and track lanes of an edge as LaneProbes (EDGE frame of arc). Deleted lanes are skipped (an edge whose
        // lanes are being regenerated); lanesOut (optional) receives the lane entity of each probe, in the same order.
        public static void ProbeLanes(EntityManager em, Entity edge, EdgeArc arc, List<LaneProbe> output, List<Entity> lanesOut)
        {
            output.Clear();
            lanesOut?.Clear();
            if (arc == null || !em.HasBuffer<NetSubLane>(edge)) return;
            var buf = em.GetBuffer<NetSubLane>(edge, true);
            for (int i = 0; i < buf.Length; i++)
            {
                Entity lane = buf[i].m_SubLane;
                if (!LaneBitsOf(em, lane, out LaneBits bits, out float width)) continue;
                var lc = LaneShiftRegistry.MeasureCurve(em, lane);
                output.Add(LaneSection.Probe(arc, lc, width * 0.5f, bits));
                lanesOut?.Add(lane);
            }
        }

        // LaneBits + width of a car / parking / track lane (false for other lanes, lanes without a curve and Deleted lanes).
        public static bool LaneBitsOf(EntityManager em, Entity lane, out LaneBits bits, out float width)
        {
            bits = LaneBits.None;
            width = 3f;
            if (lane == Entity.Null || !em.Exists(lane) || em.HasComponent<Deleted>(lane) || !em.HasComponent<Curve>(lane)) return false;
            bool car = em.HasComponent<NetCarLane>(lane);
            bool park = em.HasComponent<NetParkingLane>(lane);
            bool track = em.HasComponent<NetTrackLane>(lane);
            if (!car && !park && !track) return false;
            if (car) bits |= LaneBits.Car;
            if (park) bits |= LaneBits.Parking;
            if (track) bits |= LaneBits.Track;
            if (em.HasComponent<NetMasterLane>(lane)) bits |= LaneBits.Master;
            if (car && (em.GetComponentData<NetCarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Twoway) != 0) bits |= LaneBits.Twoway;
            if (track && (em.GetComponentData<NetTrackLane>(lane).m_Flags & Game.Net.TrackLaneFlags.Twoway) != 0) bits |= LaneBits.Twoway;
            if (em.HasComponent<Game.Net.EdgeLane>(lane))
            {
                float2 dl = em.GetComponentData<Game.Net.EdgeLane>(lane).m_EdgeDelta;
                if (math.abs(dl.x - dl.y) < 1e-4f) bits |= LaneBits.Point;
            }
            width = park ? 2.5f : 3f;
            if (em.HasComponent<PrefabRef>(lane))
            {
                var lp = em.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (em.HasComponent<NetLaneData>(lp)) width = em.GetComponentData<NetLaneData>(lp).m_Width;
                if (car && em.HasComponent<CarLaneData>(lp) &&
                    (em.GetComponentData<CarLaneData>(lp).m_RoadTypes & Game.Net.RoadTypes.Car) == 0) bits |= LaneBits.BikeOnly;
            }
            return true;
        }

        // A lane measured against an edge frame (EDGE frame of arc: EdgeRecord.Arc), exactly as the section measurement does
        // (LaneSection.Probe from the lane's own curve): bits, half width, centre, extent and direction. False for a lane that is
        // not a car / parking / track lane, has no curve or is Deleted. Traffic, Machines, Surfaces and the dev checks resolve
        // lanes with this (or LaneCentre), so they agree on which band a lane lies in.
        public static bool ProbeLane(EntityManager em, Entity lane, EdgeArc arc, out LaneProbe probe)
        {
            probe = default;
            if (arc == null || !LaneBitsOf(em, lane, out LaneBits bits, out float width)) return false;
            probe = LaneSection.Probe(arc, LaneShiftRegistry.MeasureCurve(em, lane), width * 0.5f, bits);
            return true;
        }

        // EDGE-frame lateral centre of any lane with a curve (median of its interior samples, LaneSection.Probe). False for a
        // missing or Deleted lane, or a lane without a curve.
        public static bool LaneCentre(EntityManager em, Entity lane, EdgeArc arc, out float centre)
        {
            centre = 0f;
            if (arc == null || !Alive(em, lane) || !em.HasComponent<Curve>(lane)) return false;
            centre = LaneSection.Probe(arc, LaneShiftRegistry.MeasureCurve(em, lane), 0f, LaneBits.None).Centre;
            return true;
        }

        // Signature of an edge's car / parking / track lanes (entity, version, end points quantised to 0.1 m). The Director re-measures
        // the section when it changes although curve, width and end corners stayed the same (lanes regenerated after the first
        // measurement, a lane set that was incomplete in the creation frame). 0 = no such lane.
        public static uint LaneSignature(EntityManager em, Entity edge)
        {
            if (!em.HasBuffer<NetSubLane>(edge)) return 0u;
            var buf = em.GetBuffer<NetSubLane>(edge, true);
            uint h = 0u;
            for (int i = 0; i < buf.Length; i++)
            {
                Entity lane = buf[i].m_SubLane;
                if (!em.Exists(lane) || em.HasComponent<Deleted>(lane) || !em.HasComponent<Curve>(lane)) continue;
                if (!em.HasComponent<NetCarLane>(lane) && !em.HasComponent<NetParkingLane>(lane) && !em.HasComponent<NetTrackLane>(lane)) continue;
                var b = LaneShiftRegistry.MeasureCurve(em, lane);
                uint k = math.hash(new int4((int3)math.round(b.a * 10f), lane.Index)) * 31u + math.hash(new int4((int3)math.round(b.d * 10f), lane.Version));
                h = h * 16777619u ^ k;
                if (h == 0u) h = 1u;
            }
            return h;
        }

        private static readonly List<LaneProbe> s_Probes = new List<LaneProbe>(24);

        public static RoadClassInfo RoadClass(EntityManager em, Entity edge)
        {
            var rc = new RoadClassInfo { CompositionWidth = CompositionWidth(em, edge) };
            if (em.HasBuffer<NetSubLane>(edge))
            {
                var buf = em.GetBuffer<NetSubLane>(edge, true);
                for (int i = 0; i < buf.Length; i++)
                    if (em.HasComponent<NetCarLane>(buf[i].m_SubLane) && !em.HasComponent<SlaveLane>(buf[i].m_SubLane)) rc.CarLanes++;
            }
            if (em.HasComponent<PrefabRef>(edge))
            {
                var prefab = em.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (em.HasComponent<RoadData>(prefab)) rc.Highway = (em.GetComponentData<RoadData>(prefab).m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
            }
            if (em.HasComponent<Composition>(edge))
            {
                var c = em.GetComponentData<Composition>(edge).m_Edge;
                if (ValidComposition(em, c))
                {
                    var g = em.GetComponentData<NetCompositionData>(c).m_Flags.m_General;
                    rc.Elevated = (g & CompositionFlags.General.Elevated) != 0;
                    rc.Tunnel = (g & CompositionFlags.General.Tunnel) != 0;
                }
            }
            return rc;
        }

        // ---- terrain

        // CPU terrain height (coarse baseLod cascade readback, ~3.5 m texels). NaN when not available.
        public static float TerrainY(TerrainSystem ts, float3 p)
        {
            if (ts == null) return float.NaN;
            var hd = ts.GetHeightData();
            if (!hd.isCreated || hd.resolution.x <= 2) return float.NaN;
            return TerrainUtils.SampleHeight(ref hd, p);
        }

        // ---- derived entities

        // Every entity a module creates for a site gets LivePath (never saved; verified in game for areas/puppets/props) + RRWDerived.
        public static void TagDerived(EntityManager em, Entity e, Entity site, DerivedGroup group) => TagDerived(em, e, site, 0u, group);

        public static void TagDerived(EntityManager em, Entity e, Entity site, uint projectId, DerivedGroup group)
        {
            if (!em.Exists(e)) return;
            if (!em.HasComponent<LivePath>(e)) em.AddComponent<LivePath>(e);
            var d = new RRWDerived { m_Site = site, m_ProjectId = projectId, m_Group = (byte)group };
            if (em.HasComponent<RRWDerived>(e)) em.SetComponentData(e, d); else em.AddComponentData(e, d);
        }

        public static void MarkDeleted(EntityManager em, Entity e)
        {
            if (em.Exists(e) && !em.HasComponent<Deleted>(e)) em.AddComponent<Deleted>(e);
        }

        // ---- money (PlayerMoney on the city entity)

        public static bool CanAfford(EntityManager em, Entity city, int amount)
        {
            if (amount <= 0) return true;
            if (city == Entity.Null || !em.HasComponent<PlayerMoney>(city)) return false;
            var m = em.GetComponentData<PlayerMoney>(city);
            return m.m_Unlimited || m.money >= amount;
        }

        public static bool TryPay(EntityManager em, Entity city, int amount)
        {
            if (amount <= 0) return true;
            if (!CanAfford(em, city, amount)) return false;
            var m = em.GetComponentData<PlayerMoney>(city);
            m.Subtract(amount);
            em.SetComponentData(city, m);
            return true;
        }

        public static void Credit(EntityManager em, Entity city, int amount)
        {
            if (amount <= 0 || city == Entity.Null || !em.HasComponent<PlayerMoney>(city)) return;
            var m = em.GetComponentData<PlayerMoney>(city);
            m.Add(amount);
            em.SetComponentData(city, m);
        }

        // ---- names

        public static string PrefabName(PrefabSystem ps, EntityManager em, Entity e)
        {
            try
            {
                if (em.HasComponent<PrefabRef>(e)) e = em.GetComponentData<PrefabRef>(e).m_Prefab;
                return ps.GetPrefabName(e);
            }
            catch { return "?"; }
        }

        public static string E(Entity e) => e.Index + ":" + e.Version;
    }
}
