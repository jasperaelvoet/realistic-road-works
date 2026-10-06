using System;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.Common;
using Game.Net;
using Game.Serialization;
using Game.Tools;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using NetCarLane = Game.Net.CarLane;
using NetParkingLane = Game.Net.ParkingLane;
using NetPedestrianLane = Game.Net.PedestrianLane;
using NetTrackLane = Game.Net.TrackLane;
using SubLane = Game.Net.SubLane;

namespace RealisticRoadWorks.V3.Persistence
{
    // Save safety. Read-only lane counters that show whether any per-group
    // closure state reached a save or survived a load:
    //   Forbidden   CarLane.m_Flags & Forbidden (serialized; vanilla also sets it on some generated lanes, so compare with a
    //               vanilla control)
    //   Blocked     CarLane.m_BlockageEnd >= m_BlockageStart (serialized; CLOSED-B writes 0..255; vanilla: accidents)
    //   Sentinel    an access restriction that points at our runtime sentinel (RRWAccessZone; never saved)
    //   Dangling    an access restriction that points at an entity that does not exist (a sentinel ref that survived a load)
    //   ParkDisabled ParkingLane ParkingDisabled (vanilla recomputes it in ParkingLaneDataSystem; verified in game)
    // Persistence never writes lanes (Traffic owns the save guard); these counts only verify it.
    internal struct LaneScanCounts
    {
        public int Edges, CarLanes, Forbidden, Blocked, Sentinel, Dangling;
        public int PedLanes, PedSentinel, TrackLanes, TrackSentinel, ParkingLanes, ParkDisabled, ParkSentinel;
        public int RrwRefs => Sentinel + Dangling + PedSentinel + TrackSentinel + ParkSentinel;

        public string Text() =>
            "edges=" + Edges + " car=" + CarLanes + " forbidden=" + Forbidden + " blocked=" + Blocked + " sentinel=" + Sentinel + " dangling=" + Dangling
            + " ped=" + PedLanes + "(rrw " + PedSentinel + ") track=" + TrackLanes + "(rrw " + TrackSentinel + ")"
            + " parking=" + ParkingLanes + " parkDisabled=" + ParkDisabled + "(rrw " + ParkSentinel + ")";
    }

    internal static class LaneScan
    {
        // Snapshot taken in the first Modification1 update after a load (LegacyMigrationSystem, order 215): after the
        // Director's registry rebuild (210), BEFORE Traffic re-applies anything (TrafficRequests 220, LaneClosure ModificationEnd).
        public static bool LoadTaken;
        public static LaneScanCounts LoadWorks, LoadCity;
        public static uint LoadUpdate;
        public static int LoadSites, LoadModeA, LoadModeH, LoadModeD, LoadModeUnknown, LoadIncompatible;
        // Last pre-serialize audit (SaveLaneAuditSystem, after Traffic's save guard).
        public static bool AuditTaken;
        public static LaneScanCounts AuditWorks;
        public static int AuditClosedBEdges, AuditSoftEdges, AuditOpenEdges;
        public static string AuditWhen = "never";

        public static void ResetForLoad()
        {
            LoadTaken = false;
            LoadWorks = LoadCity = default;
            LoadUpdate = 0;
            LoadSites = LoadModeA = LoadModeH = LoadModeD = LoadModeUnknown = LoadIncompatible = 0;
            AuditTaken = false;
            AuditWorks = default;
            AuditClosedBEdges = AuditSoftEdges = AuditOpenEdges = 0;
            AuditWhen = "never";
        }

        // A saved VisualMode value this build accepts (2 = HalfWidth is valid; this version treats it like Minimal).
        public static bool ModeValid(VisualMode m) => m == VisualMode.FullDig || m == VisualMode.HalfWidth || m == VisualMode.Minimal;

        private static int RestrictionKind(EntityManager em, Entity r)
        {
            if (r == Entity.Null) return 0;
            if (!em.Exists(r)) return 2;                          // dangling
            return em.HasComponent<RRWAccessZone>(r) ? 1 : 0;     // our sentinel
        }

        private static void CountLane(EntityManager em, Entity lane, ref LaneScanCounts c)
        {
            if (em.HasComponent<NetCarLane>(lane))
            {
                var cl = em.GetComponentData<NetCarLane>(lane);
                c.CarLanes++;
                if ((cl.m_Flags & CarLaneFlags.Forbidden) != 0) c.Forbidden++;
                if (cl.m_BlockageEnd >= cl.m_BlockageStart) c.Blocked++;
                int k = RestrictionKind(em, cl.m_AccessRestriction);
                if (k == 1) c.Sentinel++; else if (k == 2) c.Dangling++;
            }
            if (em.HasComponent<NetPedestrianLane>(lane))
            {
                c.PedLanes++;
                if (RestrictionKind(em, em.GetComponentData<NetPedestrianLane>(lane).m_AccessRestriction) != 0) c.PedSentinel++;
            }
            if (em.HasComponent<NetTrackLane>(lane))
            {
                c.TrackLanes++;
                if (RestrictionKind(em, em.GetComponentData<NetTrackLane>(lane).m_AccessRestriction) != 0) c.TrackSentinel++;
            }
            if (em.HasComponent<NetParkingLane>(lane))
            {
                var pl = em.GetComponentData<NetParkingLane>(lane);
                c.ParkingLanes++;
                if ((pl.m_Flags & ParkingLaneFlags.ParkingDisabled) != 0) c.ParkDisabled++;
                if (RestrictionKind(em, pl.m_AccessRestriction) != 0) c.ParkSentinel++;
            }
        }

        // Lanes of every saved works edge (RoadWorksSite on an Edge, not Temp/Deleted): their SubLane buffers.
        // edgeOut (optional) receives the edges with any blocked / sentinel / dangling lane (max 16).
        public static LaneScanCounts Works(EntityManager em, List<Entity> edgeOut = null)
        {
            var c = new LaneScanCounts();
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadWorksSite>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<SubLane>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            var edges = q.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    c.Edges++;
                    int before = c.Blocked + c.RrwRefs;
                    var buf = em.GetBuffer<SubLane>(edges[i], true);
                    for (int k = 0; k < buf.Length; k++)
                    {
                        Entity lane = buf[k].m_SubLane;
                        if (lane == Entity.Null || !em.Exists(lane)) continue;
                        CountLane(em, lane, ref c);
                    }
                    if (edgeOut != null && edgeOut.Count < 16 && c.Blocked + c.RrwRefs > before) edgeOut.Add(edges[i]);
                }
            }
            finally
            {
                edges.Dispose();
                q.Dispose();
            }
            return c;
        }

        // Every lane of every road edge in the city (edge sub-lanes; node lanes are regenerated by vanilla). One-time on load
        // and on demand (rrw.save.scan): a mod-less save has no works edges left, so the city-wide numbers are the
        // ones to compare with a vanilla control. edgeOut: road edges with a blocked / sentinel / dangling lane (max 16).
        public static LaneScanCounts City(EntityManager em, List<Entity> edgeOut = null)
        {
            var c = new LaneScanCounts();
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<SubLane>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            var edges = q.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    c.Edges++;
                    int before = c.Blocked + c.RrwRefs;
                    var buf = em.GetBuffer<SubLane>(edges[i], true);
                    for (int k = 0; k < buf.Length; k++)
                    {
                        Entity lane = buf[k].m_SubLane;
                        if (lane == Entity.Null || !em.Exists(lane)) continue;
                        CountLane(em, lane, ref c);
                    }
                    if (edgeOut != null && edgeOut.Count < 16 && c.Blocked + c.RrwRefs > before) edgeOut.Add(edges[i]);
                }
            }
            finally
            {
                edges.Dispose();
                q.Dispose();
            }
            return c;
        }

        // Saved sites by VisualMode at load.
        public static void ModeCensus(EntityManager em, EntityQuery sites)
        {
            LoadSites = LoadModeA = LoadModeH = LoadModeD = LoadModeUnknown = LoadIncompatible = 0;
            if (sites.IsEmptyIgnoreFilter) return;
            var arr = sites.ToComponentDataArray<RoadWorksSite>(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    LoadSites++;
                    switch (arr[i].Mode)
                    {
                        case VisualMode.FullDig: LoadModeA++; break;
                        case VisualMode.HalfWidth: LoadModeH++; break;
                        case VisualMode.Minimal: LoadModeD++; break;
                        default: LoadModeUnknown++; break;
                    }
                    if (arr[i].Has(SiteFlags.Incompatible)) LoadIncompatible++;
                }
            }
            finally { arr.Dispose(); }
        }

        // First Modification1 update after a load (called by LegacyMigrationSystem). Logs one Info line, Warn when an RRW
        // restriction (sentinel / dangling ref) survived the load on any road lane.
        public static void TakeLoadSnapshot(EntityManager em, EntityQuery sites)
        {
            var works = new List<Entity>();
            var city = new List<Entity>();
            LoadWorks = Works(em, works);
#if DEVTOOLS
            LoadCity = City(em, city);   // every road lane: dev builds only (to compare a mod-less save with a vanilla control)
#endif
            ModeCensus(em, sites);
            LoadUpdate = RRWClock.UpdateIndex;
            LoadTaken = true;
            string msg = "persistence: load lane snapshot (before Traffic re-applies) works " + LoadWorks.Text() + " | city " + (LoadCity.Edges > 0 ? LoadCity.Text() : "(dev builds only)")
                         + " | sites=" + LoadSites + " modeA=" + LoadModeA + " modeH=" + LoadModeH + "(Wave 2 save, run as mode D)"
                         + " modeD=" + LoadModeD + " modeUnknown=" + LoadModeUnknown + " incompatible=" + LoadIncompatible;
            if (LoadCity.RrwRefs > 0 || LoadWorks.RrwRefs > 0)
                RRWLog.Warn(msg + " -- RRW restriction refs survived the save (E10 FAIL?): edges " + Edges(city.Count > 0 ? city : works));
            else RRWLog.Info(msg + (LoadCity.Blocked + LoadWorks.Blocked > 0 ? " (blocked edges " + Edges(city.Count > 0 ? city : works) + ")" : ""));
            if (LoadModeUnknown > 0)
                RRWLog.Warn("persistence: " + LoadModeUnknown + " saved sites carry an unknown VisualMode (not 0/2/3); modules run them as mode D");
        }

        public static string Edges(List<Entity> l)
        {
            if (l == null || l.Count == 0) return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(RRWLog.E(l[i])); }
            return sb.ToString();
        }
    }

    // Serialize, Before SerializerSystem, AFTER Traffic's save guard (RRWOrder.TrafficSaveGuard 905): counts what the works
    // edges' lanes really carry into the save. Read-only; one Info line per save, Warn when a
    // sentinel reference or a CLOSED-B blockage would be written. Systems sharing the Before target run in registration
    // (Order) order, so this runs between the Traffic guard (905) and SaveRestore (910, After).
    [RegisterSystem(SystemUpdatePhase.Serialize, Before = typeof(SerializerSystem), Order = SaveLaneAuditSystem.kOrder)]
    public partial class SaveLaneAuditSystem : GameSystemBase
    {
        public const int kOrder = RRWOrder.SaveLaneAudit;   // after the Traffic save guard (905), before SaveRestore (910)
        private readonly RRWGuard m_Guard = new RRWGuard("persistence SaveLaneAudit");
        private readonly List<Entity> m_Edges = new List<Entity>();

        protected override void OnUpdate()
        {
            if (m_Guard.Faulted) return;
            long t0 = RRWPerf.Start();
            try
            {
                EntityManager.CompleteAllTrackedJobs();
                m_Edges.Clear();
                var c = LaneScan.Works(EntityManager, m_Edges);
                int closedB = 0, soft = 0, open = 0;
                foreach (var er in SiteRegistry.Edges.Values)
                {
                    if (er.ClosedBApplied != RoadZones.None) closedB++;
                    if (er.SoftApplied != RoadZones.None) soft++;
                    if (er.OpenLanesApplied != RoadZones.None) open++;
                }
                LaneScan.AuditWorks = c;
                LaneScan.AuditClosedBEdges = closedB;
                LaneScan.AuditSoftEdges = soft;
                LaneScan.AuditOpenEdges = open;
                LaneScan.AuditTaken = true;
                LaneScan.AuditWhen = "update " + RRWClock.UpdateIndex + " sim " + RRWClock.SimFrame;
                string msg = "persistence: save lane audit (after the Traffic save guard) works " + c.Text()
                             + " | registry edges closedB=" + closedB + " soft=" + soft + " halfOpen=" + open + " gates soft=" + RRWGates.Soft + " closedb=" + RRWGates.ClosedB;
                // A sentinel / dangling ref must never be written. Blockage on a works edge is a CLOSED-B the guard missed when
                // Traffic reports CLOSED-B groups (else it is a vanilla accident, logged only).
                if (c.RrwRefs > 0 || (c.Blocked > 0 && closedB > 0))
                    RRWLog.Warn(msg + " -- closure state would be SAVED on edges " + LaneScan.Edges(m_Edges) + " (Traffic save guard, E10)");
                else RRWLog.Info(msg);
                m_Guard.Ok();
            }
            catch (Exception e) { m_Guard.Fail(e); }
            finally { RRWPerf.Stop(PerfSlot.Persistence, t0); }
        }
    }
}
