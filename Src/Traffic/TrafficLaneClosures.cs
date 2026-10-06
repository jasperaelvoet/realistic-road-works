using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using BlockedLane = Game.Objects.BlockedLane;
using GeometryFlags = Game.Objects.GeometryFlags;
using Moving = Game.Objects.Moving;
using NetCarLane = Game.Net.CarLane;
using NetCarLaneFlags = Game.Net.CarLaneFlags;
using NetMasterLane = Game.Net.MasterLane;
using NetSlaveLane = Game.Net.SlaveLane;
using NetSubLane = Game.Net.SubLane;
using ObjTransform = Game.Objects.Transform;

// Lane closures of upgrade works: invisible "RRW Lane Closure" markers that close ONE slave car lane (a lane drop) while every
// other lane of the direction keeps carrying traffic (verified in game on a building-free 2+2 road: traffic merges before the
// first marker, nothing gets stuck, the marker is not rendered).
//
//  * Prefab: runtime clone of the vanilla MarkerObjectPrefab "Road Block" (its LaneBlock component makes the vanilla
//    LaneBlockSystem register the object on every non-master lane whose curve lies within r + laneWidth / 2 of it), with
//    PlaceableObject / Unlockable / UIObject stripped so it never shows in a toolbar. Its bounds are tailored in game to a cube
//    of half size r (kBlockerRadius), so the registration radius (bounds.max.x - bounds.min.y) / 2 is r.
//  * Placement rules (verified in game): the marker centre keeps r + kDropOpenLaneClear from the edge of every lane that stays
//    open (closer, it registers on that lane too and the master lane of the direction is blocked: the whole direction stops);
//    the first and last marker stand kUwBlockerNodeSetback from the nodes (closer, node lanes and crosswalks register).
//  * Entities: LivePath + RRWDerived (never written to a save). The lane data they cause (blockage, caution) IS serialized, so
//    the save guard writes the values the game would compute without them right before SerializerSystem and the live values
//    right after. A save loads without the mod with no residue (verified in game).
namespace RealisticRoadWorks.V3.Traffic
{
    // Tag on our lane closure markers (runtime only: the entity carries LivePath, so it is never written to a save).
    public struct TrafficLaneBlocker : IComponentData
    {
        public Entity m_Edge;
        public Entity m_Lane;     // the dropped lane it was placed on
    }

    // ------------------------------------------------------------------------------------------------ prefab
    public static class LaneClosurePrefab
    {
        public const string Name = PrefabNames.LaneClosure;
        public const string Type = "MarkerObjectPrefab";

        public static bool Done;                 // registration attempted (main menu poll, preload fallback or first use)
        public static bool Ok;                   // the clone exists
        public static string Report = "not registered yet";
        public static Entity Prefab = Entity.Null;
        public static float TailoredR = float.NaN;

        public static bool Register(PrefabSystem ps, string where)
        {
            if (ps.TryGetPrefab(new PrefabID(Type, Name), out PrefabBase existing) && existing != null)
            {
                Done = Ok = true;
                Report = "'" + Name + "' already registered (" + where + ")";
                return true;
            }
            if (!ps.TryGetPrefab(new PrefabID(Type, PrefabNames.RoadBlockMarker), out PrefabBase src) || src == null) return false;
            var c = src.Clone(Name);
            bool hadLaneBlock = c.Has<LaneBlock>();
            c.Remove<PlaceableObject>();
            c.Remove<Unlockable>();
            c.Remove<UIObject>();
            c.Remove<ObsoleteIdentifiers>();
            if (!hadLaneBlock) c.AddComponent<LaneBlock>();
            bool ok = ps.AddPrefab(c);
            Done = true;
            Ok = ok;
            Report = "'" + Name + "' from '" + PrefabNames.RoadBlockMarker + "' " + (ok ? "added" : "NOT added") + " in " + where +
                     " (laneBlock " + (hadLaneBlock ? "inherited" : "added") + ")";
            if (!ok) UnityEngine.Object.Destroy(c);
            RRWLog.Info("traffic: lane closure prefab " + Report);
            return ok;
        }

        // In game: the prefab entity with a valid archetype and its bounds tailored to kBlockerRadius; Null + why while not ready
        // (a clone added in game is initialised at the next PrefabUpdate: callers retry next update).
        public static Entity Ensure(World world, EntityManager em, out string why, out bool missing)
        {
            why = null;
            missing = false;
            var ps = world.GetOrCreateSystemManaged<PrefabSystem>();
            if (!ps.TryGetPrefab(new PrefabID(Type, Name), out PrefabBase pb) || pb == null)
            {
                if (!Register(ps, "first use in game"))
                {
                    missing = true;
                    why = "source '" + PrefabNames.RoadBlockMarker + "' not found";
                    return Entity.Null;
                }
                why = "prefab registered just now (initialised at the next prefab update)";
                return Entity.Null;
            }
            if (!ps.TryGetEntity(pb, out Entity e) || !em.HasComponent<ObjectData>(e) || !em.GetComponentData<ObjectData>(e).m_Archetype.Valid)
            {
                why = "prefab not initialised yet";
                return Entity.Null;
            }
            Prefab = e;
            float r = RRWConst.kBlockerRadius;
            if (em.HasComponent<ObjectGeometryData>(e))
            {
                var g = em.GetComponentData<ObjectGeometryData>(e);
                if (TailoredR != r || g.m_Bounds.max.x != r || g.m_Bounds.min.y != -r)
                {
                    g.m_Bounds = new Bounds3(new float3(-r, -r, -r), new float3(r, r, r));
                    g.m_Size = new float3(2f * r, 2f * r, 2f * r);
                    g.m_Flags &= ~(GeometryFlags.Overridable | GeometryFlags.DeleteOverridden);
                    em.SetComponentData(e, g);
                    TailoredR = r;
                    RRWLog.Info("traffic: lane closure prefab bounds tailored to r=" + RRWLog.F(r) + " (registration radius " +
                                RRWLog.F((g.m_Bounds.max.x - g.m_Bounds.min.y) * 0.5f) + " m)");
                }
            }
            return e;
        }
    }

    // Registers the clone in the main menu (pattern of the other RRW clones); fallback before a city loads, and lazily on first use.
    [RegisterSystem(SystemUpdatePhase.PrefabUpdate, Before = typeof(PrefabInitializeSystem), Order = RRWOrder.LaneClosureRegistry)]
    public partial class LaneClosurePrefabSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private int m_Wait, m_Polls;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate()
        {
            if (LaneClosurePrefab.Done) return;
            if (--m_Wait > 0) return;
            m_Wait = 30;
            try
            {
                m_Polls++;
                if (LaneClosurePrefab.Register(m_PrefabSystem, "prefab update poll " + m_Polls)) return;
                bool inGame = GameManager.instance != null && GameManager.instance.gameMode.IsGameOrEditor();
                if (m_Polls >= 120 || inGame)
                {
                    LaneClosurePrefab.Done = true;
                    RRWLog.Warn("traffic: '" + PrefabNames.RoadBlockMarker + "' not found after " + m_Polls + " polls: no lane drops (upgrade works fall back to the next traffic rung)");
                }
            }
            catch (Exception e)
            {
                LaneClosurePrefab.Done = true;
                RRWLog.ErrorOnce("traffic lane closure prefab", e);
            }
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (LaneClosurePrefab.Done || !mode.IsGameOrEditor()) return;
            try { LaneClosurePrefab.Register(m_PrefabSystem, "OnGamePreload(" + purpose + ")"); }
            catch (Exception e) { LaneClosurePrefab.Done = true; RRWLog.ErrorOnce("traffic lane closure prefab (preload)", e); }
        }
    }

    // ------------------------------------------------------------------------------------------------ lane data replica
    // Blockage / caution of one car lane as bytes (CarLane fields).
    public struct LaneVals
    {
        public byte BS, BE, CS, CE;

        public static LaneVals Of(in NetCarLane c) => new LaneVals { BS = c.m_BlockageStart, BE = c.m_BlockageEnd, CS = c.m_CautionStart, CE = c.m_CautionEnd };
        public bool Blocked => BS <= BE;
        public bool Caution => CS <= CE;
        public bool SameBlockage(in LaneVals o) => BS == o.BS && BE == o.BE;
        public bool SameCaution(in LaneVals o) => CS == o.CS && CE == o.CE;
        public bool Same(in LaneVals o) => SameBlockage(o) && SameCaution(o);
        public string Text => "block=" + (Blocked ? BS + ".." + BE : "-") + " caution=" + (Caution ? CS + ".." + CE : "-");
    }

    // One sub lane of an owner (edge or node) in SubLane buffer order: what the game's lane data system reads.
    public struct ReplicaLane
    {
        public bool Car, Master, Slave, HasBuffer, Invert;
        public Bounds1 Stat;            // union of the non-moving lane objects' curve ranges (empty = (1, 0))
        public int MinIndex, MaxIndex;  // master: its slaves in the owner's SubLane buffer
        public SlaveLaneFlags SlaveFlags;
        public int Group;               // CarLane.m_CarriagewayGroup
    }

    // Pure replica of the game's blockage / caution computation (verified against the live values in game):
    //  * single / slave car lane: blockage = the stationary range; caution = blockage unless the slave is both a starting and an
    //    ending lane;
    //  * master: blockage = intersection of the stationary ranges of its slaves that have a lane object buffer, no caution of its own;
    //  * then every car lane gets the union of the caution of all car lanes of the owner in the same carriageway group, mirrored
    //    (255 - x) between lanes of opposite curve direction.
    public static class LaneDataReplica
    {
        public static void Bytes(Bounds1 b, out byte s, out byte e)
        {
            if (b.min <= b.max)
            {
                s = (byte)math.max(0, (int)math.floor(b.min * 255f));
                e = (byte)math.min(255, (int)math.ceil(b.max * 255f));
            }
            else { s = 255; e = 0; }
        }

        public static LaneVals[] Compute(ReplicaLane[] lanes)
        {
            int n = lanes.Length;
            var own = new LaneVals[n];
            for (int i = 0; i < n; i++)
            {
                var l = lanes[i];
                var v = new LaneVals { BS = 255, BE = 0, CS = 255, CE = 0 };
                if (!l.Car) { own[i] = v; continue; }
                if (l.Master)
                {
                    var b = new Bounds1(1f, 0f);
                    bool first = true;
                    int max = math.min(l.MaxIndex, n - 1);
                    for (int j = l.MinIndex; j <= max; j++)
                    {
                        if (j < 0 || !lanes[j].HasBuffer) continue;
                        if (first) { b = lanes[j].Stat; first = false; }
                        else b &= lanes[j].Stat;
                    }
                    Bytes(b, out v.BS, out v.BE);
                }
                else
                {
                    Bytes(l.Stat, out v.BS, out v.BE);
                    var both = SlaveLaneFlags.StartingLane | SlaveLaneFlags.EndingLane;
                    if (v.Blocked && (!l.Slave || (l.SlaveFlags & both) != both)) { v.CS = v.BS; v.CE = v.BE; }
                }
                own[i] = v;
            }
            var r = new LaneVals[n];
            for (int i = 0; i < n; i++)
            {
                var x = own[i];
                if (lanes[i].Car)
                {
                    int cs = x.CS, ce = x.CE;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i || !lanes[j].Car || !own[j].Caution || lanes[j].Group != lanes[i].Group) continue;
                        if (lanes[j].Invert != lanes[i].Invert) { cs = math.min(cs, 255 - own[j].CE); ce = math.max(ce, 255 - own[j].CS); }
                        else { cs = math.min(cs, own[j].CS); ce = math.max(ce, own[j].CE); }
                    }
                    x.CS = (byte)cs;
                    x.CE = (byte)ce;
                }
                r[i] = x;
            }
            return r;
        }

        private static Bounds1 Stationary(EntityManager em, Entity lane, bool excludeOurs, out bool hasBuffer)
        {
            var r = new Bounds1(1f, 0f);
            hasBuffer = em.HasBuffer<LaneObject>(lane);
            if (!hasBuffer) return r;
            var buf = em.GetBuffer<LaneObject>(lane, true);
            for (int i = 0; i < buf.Length; i++)
            {
                var o = buf[i].m_LaneObject;
                if (em.Exists(o) && em.HasComponent<Moving>(o)) continue;
                if (excludeOurs && em.Exists(o) && em.HasComponent<TrafficLaneBlocker>(o)) continue;
                r |= MathUtils.Bounds(buf[i].m_CurvePosition.x, buf[i].m_CurvePosition.y);
            }
            return r;
        }

        // Expected blockage / caution of every car lane of `owner` as the game would compute it now (optionally without our
        // lane closure markers).
        public static void Expected(EntityManager em, Entity owner, bool excludeOurs, Dictionary<Entity, LaneVals> output)
        {
            if (!TrafficUtil.Alive(em, owner) || !em.HasBuffer<NetSubLane>(owner)) return;
            var buf = em.GetBuffer<NetSubLane>(owner, true);
            int n = buf.Length;
            var lanes = new Entity[n];
            var input = new ReplicaLane[n];
            for (int i = 0; i < n; i++) lanes[i] = buf[i].m_SubLane;
            for (int i = 0; i < n; i++)
            {
                var lane = lanes[i];
                var li = new ReplicaLane();
                if (TrafficUtil.Alive(em, lane))
                {
                    li.Stat = Stationary(em, lane, excludeOurs, out li.HasBuffer);
                    if (em.HasComponent<NetCarLane>(lane))
                    {
                        var c = em.GetComponentData<NetCarLane>(lane);
                        li.Car = true;
                        li.Group = c.m_CarriagewayGroup;
                        li.Invert = (c.m_Flags & NetCarLaneFlags.Invert) != 0;
                        if (em.HasComponent<NetMasterLane>(lane))
                        {
                            var m = em.GetComponentData<NetMasterLane>(lane);
                            li.Master = true;
                            li.MinIndex = m.m_MinIndex;
                            li.MaxIndex = m.m_MaxIndex;
                        }
                        if (em.HasComponent<NetSlaveLane>(lane))
                        {
                            li.Slave = true;
                            li.SlaveFlags = em.GetComponentData<NetSlaveLane>(lane).m_Flags;
                        }
                    }
                }
                input[i] = li;
            }
            var vals = Compute(input);
            for (int i = 0; i < n; i++) if (input[i].Car) output[lanes[i]] = vals[i];
        }

        public static void Write(EntityManager em, Entity lane, in LaneVals v)
        {
            if (!TrafficUtil.Alive(em, lane) || !em.HasComponent<NetCarLane>(lane)) return;
            var c = em.GetComponentData<NetCarLane>(lane);
            c.m_BlockageStart = v.BS; c.m_BlockageEnd = v.BE;
            c.m_CautionStart = v.CS; c.m_CautionEnd = v.CE;
            em.SetComponentData(lane, c);
        }
    }

    // ------------------------------------------------------------------------------------------------ marker entities
    public static class LaneClosureMarkers
    {
        private static EntityQuery s_Query;
        private static World s_QueryWorld;

        public static bool IsOurs(EntityManager em, Entity o) => o != Entity.Null && em.Exists(o) && em.HasComponent<TrafficLaneBlocker>(o);

        // Mod1 only (structural change). Created + Updated: the vanilla LaneBlockSystem registers it on its lanes in this frame.
        public static Entity Create(EntityManager em, Entity prefab, float3 pos, quaternion rot, Entity edge, Entity lane, uint projectId)
        {
            var arch = em.GetComponentData<ObjectData>(prefab).m_Archetype;
            Entity e = em.CreateEntity(arch);
            em.SetComponentData(e, new PrefabRef(prefab));
            em.SetComponentData(e, new ObjTransform(pos, rot));
            if (em.HasComponent<PseudoRandomSeed>(e)) em.SetComponentData(e, new PseudoRandomSeed((ushort)(e.Index & 0xffff)));
            if (!em.HasComponent<Created>(e)) em.AddComponent<Created>(e);
            if (!em.HasComponent<Updated>(e)) em.AddComponent<Updated>(e);
            if (!em.HasBuffer<BlockedLane>(e)) em.AddBuffer<BlockedLane>(e);
            em.AddComponentData(e, new TrafficLaneBlocker { m_Edge = edge, m_Lane = lane });
            EcsUtil.TagDerived(em, e, edge, projectId, DerivedGroup.LaneBlocker);
            return e;
        }

        // Deleted: LaneBlockSystem unregisters it in this frame; the lanes it was registered on are refreshed one update later.
        public static void Delete(EntityManager em, Entity e) => EcsUtil.MarkDeleted(em, e);

        public static bool Alive(EntityManager em, Entity e) => e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e);

        // Lanes a marker is registered on now (appended; none while LaneBlockSystem has not run yet).
        public static void RegisteredLanes(EntityManager em, Entity marker, List<Entity> output)
        {
            if (!em.Exists(marker) || !em.HasBuffer<BlockedLane>(marker)) return;
            var buf = em.GetBuffer<BlockedLane>(marker, true);
            for (int i = 0; i < buf.Length; i++) output.Add(buf[i].m_Lane);
        }

        // Our markers in a lane's object buffer.
        public static int CountOnLane(EntityManager em, Entity lane)
        {
            if (!TrafficUtil.Alive(em, lane) || !em.HasBuffer<LaneObject>(lane)) return 0;
            var buf = em.GetBuffer<LaneObject>(lane, true);
            int n = 0;
            for (int i = 0; i < buf.Length; i++) if (IsOurs(em, buf[i].m_LaneObject)) n++;
            return n;
        }

        // Every marker entity of ours in the world (stale ones after a load must never exist: LivePath).
        public static EntityQuery Query(EntityManager em)
        {
            if (s_QueryWorld != em.World)
            {
                s_QueryWorld = em.World;
                s_Query = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<TrafficLaneBlocker>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>() },
                });
            }
            return s_Query;
        }

        // ---- save guard (Serialize phase, inside the Traffic save guard / restore systems)

        private static readonly Dictionary<Entity, LaneVals> s_SaveLive = new Dictionary<Entity, LaneVals>();
        private static readonly HashSet<Entity> s_Owners = new HashSet<Entity>();
        private static readonly HashSet<Entity> s_Registered = new HashSet<Entity>();
        private static readonly List<Entity> s_Tmp = new List<Entity>(8);
        private static readonly Dictionary<Entity, LaneVals> s_Without = new Dictionary<Entity, LaneVals>();
        private static readonly Dictionary<Entity, LaneVals> s_With = new Dictionary<Entity, LaneVals>();

        public static bool SavePending => s_SaveLive.Count > 0;

        // Writes the blockage / caution the game would compute WITHOUT our markers onto every car lane they influence (the lanes
        // they are registered on, and lanes of the same owners whose values differ with and without them). Remembers the live
        // values for RestoreAfterSave. Runs AFTER the closure snapshot write, so the closure values never reach the save either.
        public static int NeutraliseForSave(EntityManager em, out int owners, out int markers, out int withoutLivePath)
        {
            s_SaveLive.Clear();
            s_Owners.Clear();
            s_Registered.Clear();
            owners = markers = withoutLivePath = 0;
            var q = Query(em);
            if (q.IsEmptyIgnoreFilter) return 0;
            var arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var m = arr[i];
                    markers++;
                    if (!em.HasComponent<Game.Routes.LivePath>(m)) withoutLivePath++;
                    var tag = em.GetComponentData<TrafficLaneBlocker>(m);
                    if (TrafficUtil.Alive(em, tag.m_Edge)) s_Owners.Add(tag.m_Edge);
                    s_Tmp.Clear();
                    RegisteredLanes(em, m, s_Tmp);
                    for (int k = 0; k < s_Tmp.Count; k++)
                    {
                        var lane = s_Tmp[k];
                        s_Registered.Add(lane);
                        if (em.Exists(lane) && em.HasComponent<Owner>(lane)) s_Owners.Add(em.GetComponentData<Owner>(lane).m_Owner);
                    }
                }
            }
            finally { arr.Dispose(); }
            int n = 0;
            foreach (var owner in s_Owners)
            {
                s_Without.Clear();
                s_With.Clear();
                LaneDataReplica.Expected(em, owner, true, s_Without);
                LaneDataReplica.Expected(em, owner, false, s_With);
                owners++;
                foreach (var kv in s_Without)
                {
                    var live = LaneVals.Of(em.GetComponentData<NetCarLane>(kv.Key));
                    if (live.Same(kv.Value)) continue;
                    bool ours = s_Registered.Contains(kv.Key) || (s_With.TryGetValue(kv.Key, out var w) && !w.Same(kv.Value));
                    if (!ours) continue;
                    s_SaveLive[kv.Key] = live;
                    LaneDataReplica.Write(em, kv.Key, kv.Value);
                    n++;
                }
            }
            return n;
        }

        // Puts the live values back right after SerializerSystem (before the closure re-apply). Returns lanes written.
        public static int RestoreAfterSave(EntityManager em)
        {
            int n = 0;
            foreach (var kv in s_SaveLive)
            {
                LaneDataReplica.Write(em, kv.Key, kv.Value);
                n++;
            }
            s_SaveLive.Clear();
            return n;
        }

        public static void ClearSaveState() => s_SaveLive.Clear();

        public static string Describe(EntityManager em)
        {
            var q = Query(em);
            int n = q.IsEmptyIgnoreFilter ? 0 : q.CalculateEntityCount();
            var sb = new StringBuilder();
            sb.Append("markers=").Append(n).Append(" prefab=").Append(LaneClosurePrefab.Ok ? "ok" : "missing")
              .Append(" r=").Append(RRWLog.F(LaneClosurePrefab.TailoredR));
            return sb.ToString();
        }
    }
}
