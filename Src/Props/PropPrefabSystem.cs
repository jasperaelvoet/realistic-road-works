using System;
using System.Collections.Generic;
using Game;
using Game.Prefabs;
using Game.Prefabs.Effects;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Props
{
    // Runtime effect clones + their prop carriers (a port of the effects prototype, verified in game).
    // Registered ONCE per process as soon as the vanilla sources exist (main menu), i.e. before any city deserializes:
    // VFXSystem builds its effect table only in PreDeserialize, so VFX clones registered while a city runs would render
    // through the wrong VFX index. Light clones are rescanned every frame and are always safe.
    // Never modifies a vanilla prefab: every edit is made on a Clone().
    internal static class PropClones
    {
        public static EffectPrefab AmberLight;      // "RRW Amber Light"
        public static EffectPrefab DustVfx;         // "RRW Dust VFX"
        public static PrefabBase BarrierBeacon;     // "RRW Barrier Beacon"
        public static PrefabBase DustHeapOre;       // "RRW Dust Heap Ore"
        public static PrefabBase DustHeapStone;     // "RRW Dust Heap Stone"
        public static PrefabBase ConeLamp;          // "RRW Cone Lamp" = SafetyCone03 + RRW Amber Light (divider lamps, RRWGates.ConeLamps)
        // Fallback carriers referencing the VANILLA DustcloudSmallVFX directly (no VFX clone, so they work whenever they are
        // registered). PropSystem uses them instead of the clone carriers when the clone's VFX slot fails the runtime check
        // (DustVfx, after every load) or when the dev command rrw.props.dust selects them.
        public static EffectPrefab VanillaDust;     // vanilla "DustcloudSmallVFX" (source of the clone)
        public static PrefabBase DustHeapOreVanilla;    // "RRW Dust Heap Ore Vanilla"
        public static PrefabBase DustHeapStoneVanilla;  // "RRW Dust Heap Stone Vanilla"
        public const string kDustHeapOreVanilla = "RRW Dust Heap Ore Vanilla";
        public const string kDustHeapStoneVanilla = "RRW Dust Heap Stone Vanilla";
        public static string VanillaDustInfo = "?";  // vanilla source: conditions, maxCount, asset (registration report)
        // "RRW Dust Puff", the one-shot dust puff emitter Machines spawns at breakout / dump
        // (verified in game in the dig-animation prototype): CoalTruck01_OrePile01 + the VANILLA DustcloudSmallVFX @ (0, 0.2, 0), one puff per
        // kDustPuffAnimSeconds. Vanilla effect => valid whenever it is registered (no VFX clone index). Its prefab entity gets
        // GeometryFlags.Overridable cleared (ObjectInitializeSystem sets it on Created prefabs; an unowned instance on a road would
        // otherwise be Overridden = not rendered, no effect): RRWPrefabRegistry.DustPuffOk only once that is done (PuffFixup).
        public static PrefabBase DustPuff;
        public static string PuffState = "not registered";
        public static int PuffOverridableCleared;      // times the flag was found set and cleared (1 expected per session)
        public static uint PuffAnimFrames;             // EffectAnimation[0].m_DurationFrames of the prefab (240 = 4 s expected)

        public static bool Done;
        public static int Attempts;
        public static string RegisteredIn = "never";
        public static bool VfxSkipped;
        public static int SourcesFound;
        private static readonly List<string> s_Report = new List<string>();
        public static IReadOnlyList<string> Report => s_Report;

        private const string kEffect = "EffectPrefab";
        private const string kStatic = "StaticObjectPrefab";
        private static readonly string[][] s_Wanted =
        {
            new[] { kEffect, PrefabNames.SrcAmberLight },
            new[] { kEffect, PrefabNames.DustSmallVfx },
            new[] { kStatic, "SafetyBarrier01" },
            new[] { kStatic, PrefabNames.OreHeap },
            new[] { kStatic, PrefabNames.StoneHeap },
            new[] { kStatic, PrefabNames.TallCone },   // cone lamp carrier source (SafetyCone03)
        };

        // Resolves a clone's prefab entity (Entity.Null when the clone does not exist).
        public static Entity Resolve(PrefabSystem ps, PrefabBase clone)
        {
            if (clone == null || ps == null) return Entity.Null;
            try { return ps.TryGetEntity(clone, out var e) ? e : Entity.Null; }
            catch { return Entity.Null; }
        }

        // Returns true when finished (successfully or not). force = register whatever is available now.
        // allowVfx = false: a city already runs, the VFX clones (and the dust heaps carrying them) are skipped.
        public static bool TryRegister(World world, PrefabSystem ps, EntityQuery prefabQuery, bool force, bool allowVfx, string phase)
        {
            var found = new Dictionary<string, PrefabBase>();
            for (int i = 0; i < s_Wanted.Length; i++)
            {
                try
                {
                    if (ps.TryGetPrefab(new PrefabID(s_Wanted[i][0], s_Wanted[i][1]), out PrefabBase p) && p != null)
                        found[s_Wanted[i][0] + ":" + s_Wanted[i][1]] = p;
                }
                catch { }
            }
            if (found.Count < s_Wanted.Length && force) ScanInto(world.EntityManager, ps, prefabQuery, found);
            if (found.Count < s_Wanted.Length && !force) return false;

            string mode = "?";
            try { if (GameManager.instance != null) mode = GameManager.instance.gameMode.ToString(); } catch { }
            RegisteredIn = phase + " gameMode=" + mode + " attempt=" + Attempts;
            SourcesFound = found.Count;
            s_Report.Clear();

            PrefabBase Src(string type, string name) => found.TryGetValue(type + ":" + name, out PrefabBase p) ? p : null;

            // 1. Effect clones first (EffectSource.LateInitialize resolves them through PrefabSystem.GetEntity).
            AmberLight = MakeLight(ps, Src(kEffect, PrefabNames.SrcAmberLight), PrefabNames.AmberLight);
            if (!allowVfx)
            {
                VfxSkipped = true;
                s_Report.Add("VFX clones SKIPPED: registration reached only while a city runs (" + mode + "); restart the game for dust");
            }
            else DustVfx = MakeVfx(ps, Src(kEffect, PrefabNames.DustSmallVfx), PrefabNames.DustVfx);

            // 2. Object clones carrying the effects.
            BarrierBeacon = MakeCarrier(ps, Src(kStatic, "SafetyBarrier01"), PrefabNames.BarrierBeacon, AmberLight,
                new float3(0f, RRWConst.kBeaconHeight, 0f), RRWConst.kBeaconPeriodSeconds, Square(RRWConst.kBeaconDuty));
            DustHeapOre = MakeCarrier(ps, Src(kStatic, PrefabNames.OreHeap), PrefabNames.DustHeapOre, DustVfx,
                new float3(0f, 0.4f, 0f), RRWConst.kDustPulseSeconds, Pulse());
            DustHeapStone = MakeCarrier(ps, Src(kStatic, PrefabNames.StoneHeap), PrefabNames.DustHeapStone, DustVfx,
                new float3(0f, 0.4f, 0f), RRWConst.kDustPulseSeconds, Pulse());
            // Divider cone lamp (experimental switch RRWGates.ConeLamps): the barrier beacon's light on a tall cone (light just
            // above the cone tip, 1.08 m), same square blink; Props gives the lamps a sequential phase along the divider.
            ConeLamp = MakeCarrier(ps, Src(kStatic, PrefabNames.TallCone), PrefabNames.ConeLamp, AmberLight,
                new float3(0f, 1.1f, 0f), RRWConst.kBeaconPeriodSeconds, Square(RRWConst.kBeaconDuty));
            // vanilla-effect twins (same offset and pulse): the fallback when the clone does not render like the vanilla dust
            VanillaDust = Src(kEffect, PrefabNames.DustSmallVfx) as EffectPrefab;
            VanillaDustInfo = Describe(VanillaDust);
            DustHeapOreVanilla = MakeCarrier(ps, Src(kStatic, PrefabNames.OreHeap), kDustHeapOreVanilla, VanillaDust,
                new float3(0f, 0.4f, 0f), RRWConst.kDustPulseSeconds, Pulse());
            DustHeapStoneVanilla = MakeCarrier(ps, Src(kStatic, PrefabNames.StoneHeap), kDustHeapStoneVanilla, VanillaDust,
                new float3(0f, 0.4f, 0f), RRWConst.kDustPulseSeconds, Pulse());
            // the dig cycle's dust puff (vanilla effect, one puff per animation period; PuffFixup finishes it)
            DustPuff = MakeCarrier(ps, Src(kStatic, PrefabNames.OreHeap), PrefabNames.DustPuff, VanillaDust,
                new float3(0f, 0.2f, 0f), RRWConst.kDustPuffAnimSeconds, PuffCurve());
            PuffState = DustPuff != null ? "registered, prefab entity pending" : "MISSING (source " + (Src(kStatic, PrefabNames.OreHeap) != null ? "ok" : "CoalTruck01_OrePile01 missing")
                        + ", vanilla dust " + (VanillaDust != null ? "ok" : "missing") + ")";
            RRWPrefabRegistry.DustPuffOk = false;
            RRWPrefabRegistry.DustPuffDone = true;

            Done = true;
            bool ok = AmberLight != null && DustVfx != null;
            RRWPrefabRegistry.EffectsOk = ok;
            RRWPrefabRegistry.EffectsDone = true;
            string names = Names();
            if (ok && BarrierBeacon != null && DustHeapOre != null && DustHeapStone != null)
                RRWLog.Info("props: effect clones ok (" + names + ") in " + RegisteredIn + " sources=" + found.Count + "/" + s_Wanted.Length);
            else
                RRWLog.Warn("props: effect clones INCOMPLETE (" + names + ") in " + RegisteredIn + " sources=" + found.Count + "/" + s_Wanted.Length +
                            (VfxSkipped ? " VFX-SKIPPED" : "") + "; missing clones degrade to plain barriers / plain heaps");
            foreach (var line in s_Report) RRWLog.Verbose("props: clone " + line);
            RRWLog.Info("props: vanilla dust source " + VanillaDustInfo + "; clone " + Describe(DustVfx) + "; vanilla-dust carriers=" +
                        (DustHeapOreVanilla != null) + "/" + (DustHeapStoneVanilla != null));
            if (DustPuff != null)
                RRWLog.Info("props: dust puff '" + PrefabNames.DustPuff + "' registered in " + RegisteredIn + " (CoalTruck01_OrePile01 + vanilla " + VanillaDustInfo
                            + " @ (0, 0.2, 0), one puff per " + RRWLog.F(RRWConst.kDustPuffAnimSeconds) + " s); usable once its prefab entity is initialised");
            else
                RRWLog.Warn("props: dust puff '" + PrefabNames.DustPuff + "' " + PuffState + ": the dig cycle runs without dust puffs");
            return true;
        }

        // Finishes "RRW Dust Puff" once ObjectInitializeSystem has initialised its prefab entity (ObjectData archetype
        // valid, ObjectGeometryData present): clears GeometryFlags.Overridable on the PREFAB entity (never on a vanilla prefab) and
        // publishes RRWPrefabRegistry.DustPuffOk. Polled by PropPrefabSystem every 30 frames for the whole session (2 component reads):
        // ObjectInitializeSystem sets the flag again only on Created prefabs, which a later poll would clear again.
        public static void PuffFixup(EntityManager em, PrefabSystem ps)
        {
            if (DustPuff == null) { RRWPrefabRegistry.DustPuffOk = false; return; }
            Entity e = Resolve(ps, DustPuff);
            bool ok = false;
            string state;
            if (e == Entity.Null || !em.Exists(e)) state = "registered, prefab entity pending";
            else if (!em.HasComponent<ObjectData>(e) || !em.GetComponentData<ObjectData>(e).m_Archetype.Valid) state = "prefab " + RRWLog.E(e) + " not initialised yet (ObjectData archetype)";
            else if (!em.HasComponent<ObjectGeometryData>(e)) state = "prefab " + RRWLog.E(e) + " has no ObjectGeometryData yet";
            else
            {
                var g = em.GetComponentData<ObjectGeometryData>(e);
                if ((g.m_Flags & Game.Objects.GeometryFlags.Overridable) != 0)
                {
                    g.m_Flags &= ~Game.Objects.GeometryFlags.Overridable;
                    em.SetComponentData(e, g);
                    PuffOverridableCleared++;
                    RRWLog.Verbose("props: dust puff prefab " + RRWLog.E(e) + " Overridable cleared (#" + PuffOverridableCleared + ")");
                }
                PuffAnimFrames = em.HasBuffer<Game.Prefabs.EffectAnimation>(e) && em.GetBuffer<Game.Prefabs.EffectAnimation>(e, true).Length > 0
                    ? em.GetBuffer<Game.Prefabs.EffectAnimation>(e, true)[0].m_DurationFrames : 0u;
                bool effect = em.HasBuffer<Game.Prefabs.Effect>(e) && em.GetBuffer<Game.Prefabs.Effect>(e, true).Length > 0;
                ok = effect;
                state = ok ? "ok prefab " + RRWLog.E(e) + " flags=" + g.m_Flags + " animFrames=" + PuffAnimFrames
                           : "prefab " + RRWLog.E(e) + " has no Effect buffer yet";
            }
            if (ok != RRWPrefabRegistry.DustPuffOk || state != PuffState)
            {
                if (ok && !RRWPrefabRegistry.DustPuffOk) RRWLog.Info("props: dust puff usable: " + state + " (Overridable cleared " + PuffOverridableCleared + "x)");
                else if (!ok && RRWPrefabRegistry.DustPuffOk) RRWLog.Warn("props: dust puff no longer usable: " + state);
            }
            PuffState = state;
            RRWPrefabRegistry.DustPuffOk = ok;
        }

        // One puff per animation period: 0 -> 1 in 8 %, hold to 30 %, fade out by 55 %, then 0 (verified in game).
        public static UnityEngine.AnimationCurve PuffCurve() => new UnityEngine.AnimationCurve(
            new UnityEngine.Keyframe(0f, 0f), new UnityEngine.Keyframe(0.08f, 1f), new UnityEngine.Keyframe(0.30f, 1f),
            new UnityEngine.Keyframe(0.55f, 0f), new UnityEngine.Keyframe(1f, 0f));

        public static string Names()
        {
            var parts = new List<string>(5);
            if (AmberLight != null) parts.Add(PrefabNames.AmberLight);
            if (DustVfx != null) parts.Add(PrefabNames.DustVfx);
            if (BarrierBeacon != null) parts.Add(PrefabNames.BarrierBeacon);
            if (DustHeapOre != null) parts.Add(PrefabNames.DustHeapOre);
            if (DustHeapStone != null) parts.Add(PrefabNames.DustHeapStone);
            if (ConeLamp != null) parts.Add(PrefabNames.ConeLamp);
            if (DustHeapOreVanilla != null) parts.Add(kDustHeapOreVanilla);
            if (DustHeapStoneVanilla != null) parts.Add(kDustHeapStoneVanilla);
            if (DustPuff != null) parts.Add(PrefabNames.DustPuff);
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        // Fallback source lookup: scan every PrefabData entity once.
        private static void ScanInto(EntityManager em, PrefabSystem ps, EntityQuery q, Dictionary<string, PrefabBase> found)
        {
            var want = new HashSet<string>();
            for (int i = 0; i < s_Wanted.Length; i++) want.Add(s_Wanted[i][0] + ":" + s_Wanted[i][1]);
            var arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length && found.Count < want.Count; i++)
                {
                    var pd = em.GetComponentData<PrefabData>(arr[i]);
                    if (!ps.TryGetPrefab(pd, out PrefabBase p) || p == null) continue;
                    string key = p.GetType().Name + ":" + p.name;
                    if (want.Contains(key) && !found.ContainsKey(key)) found[key] = p;
                }
            }
            finally { arr.Dispose(); }
        }

        private static T Strip<T>(T c) where T : PrefabBase
        {
            c.Remove<ObsoleteIdentifiers>();
            c.Remove<SpawnableObject>();
            c.Remove<UIObject>();
            c.Remove<Unlockable>();
            c.Remove<EditorAssetCategoryOverride>();
            return c;
        }

        private static bool Add(PrefabSystem ps, PrefabBase clone, string what)
        {
            if (ps.AddPrefab(clone))
            {
                s_Report.Add(clone.name + " added (" + what + ")");
                return true;
            }
            s_Report.Add(clone.name + " AddPrefab returned false (" + what + ")");
            try { UnityEngine.Object.Destroy(clone); } catch { }
            return false;
        }

        // RRW Amber Light: CarAmberWarningSource clone, conditions cleared, amber, kBeaconLux written to both intensity
        // fields (verified in game: the vanilla 10 lux makes huge orange blobs on a prop).
        private static EffectPrefab MakeLight(PrefabSystem ps, PrefabBase src, string name)
        {
            if (!(src is EffectPrefab ep)) { s_Report.Add(name + " source missing"); return null; }
            try
            {
                var c = Strip((EffectPrefab)ep.Clone(name));
                c.m_Conditions = default(EffectCondition);
                string light = "no LightEffect";
                if (c.TryGet(out LightEffect le))
                {
                    le.m_UseColorTemperature = false;
                    le.m_Color = new UnityEngine.Color(RRWConst.kBeaconColor.x, RRWConst.kBeaconColor.y, RRWConst.kBeaconColor.z, 1f);
                    le.m_Intensity = RRWConst.kBeaconLux;
                    if (le.m_LightIntensity != null) le.m_LightIntensity.m_Intensity = RRWConst.kBeaconLux;
                    light = "type=" + le.m_Type + " range=" + RRWLog.F(le.m_Range) + " intensity=" + RRWLog.F(RRWConst.kBeaconLux) + " unit=" + le.m_LightUnit;
                }
                return Add(ps, c, "from " + src.name + ", conditions cleared, " + light) ? c : null;
            }
            catch (Exception e) { s_Report.Add(name + " failed: " + e.Message); return null; }
        }

        // Effect prefab summary for the logs: conditions, VFX max count and asset (clone vs vanilla must be identical but for
        // the cleared conditions and the raised max count).
        public static string Describe(EffectPrefab ep)
        {
            if (ep == null) return "missing";
            try
            {
                var c = ep.m_Conditions;
                string vfx = "no VFX";
                if (ep.TryGet(out VFX v))
                    vfx = "maxCount=" + v.m_MaxCount + " asset=" + (v.m_Effect != null ? v.m_Effect.name : "null");
                return ep.name + " [req=" + c.m_RequiredFlags + " forb=" + c.m_ForbiddenFlags + " int=" + c.m_IntensityFlags + " " + vfx + "]";
            }
            catch (Exception e) { return ep.name + " [describe failed: " + e.Message + "]"; }
        }

        // RRW Dust VFX: DustcloudSmallVFX clone, conditions cleared, m_MaxCount >= kVfxMinMaxCount.
        private static EffectPrefab MakeVfx(PrefabSystem ps, PrefabBase src, string name)
        {
            if (!(src is EffectPrefab ep)) { s_Report.Add(name + " source missing"); return null; }
            try
            {
                var c = Strip((EffectPrefab)ep.Clone(name));
                c.m_Conditions = default(EffectCondition);
                string info = "no VFX component";
                if (c.TryGet(out VFX v))
                {
                    int orig = v.m_MaxCount;
                    v.m_MaxCount = Math.Max(v.m_MaxCount, RRWConst.kVfxMinMaxCount);
                    info = "maxCount " + orig + "->" + v.m_MaxCount;
                }
                return Add(ps, c, "from " + src.name + ", conditions cleared, " + info) ? c : null;
            }
            catch (Exception e) { s_Report.Add(name + " failed: " + e.Message); return null; }
        }

        // A stripped clone of a static prop carrying one effect with one animation curve.
        private static PrefabBase MakeCarrier(PrefabSystem ps, PrefabBase src, string name, EffectPrefab effect, float3 offset, float duration, UnityEngine.AnimationCurve curve)
        {
            if (src == null) { s_Report.Add(name + " carrier source missing"); return null; }
            if (effect == null) { s_Report.Add(name + " skipped: effect clone missing"); return null; }
            try
            {
                var c = Strip(src.Clone(name));
                var es = c.AddOrGetComponent<EffectSource>();
                es.m_Effects = new List<EffectSource.EffectSettings>
                {
                    new EffectSource.EffectSettings
                    {
                        m_Effect = effect,
                        m_PositionOffset = offset,
                        m_Rotation = quaternion.identity,
                        m_Scale = new float3(1f, 1f, 1f),
                        m_Intensity = 1f,
                        m_ParentMesh = 0,
                        m_AnimationIndex = 0,
                    },
                };
                es.m_AnimationCurves = new List<EffectSource.AnimationProperties>
                {
                    new EffectSource.AnimationProperties { m_Duration = duration, m_Curve = curve },
                };
                return Add(ps, c, src.name + " + " + effect.name + " @" + RRWLog.F3(offset) + " anim " + RRWLog.F(duration) + "s") ? c : null;
            }
            catch (Exception e) { s_Report.Add(name + " failed: " + e.Message); return null; }
        }

        // Curves are sampled into 31 points over [0,1] (AnimationCurve1) and evaluated at ((frame + phase) % dur) / dur.
        public static UnityEngine.AnimationCurve Square(float duty) => new UnityEngine.AnimationCurve(
            new UnityEngine.Keyframe(0f, 1f), new UnityEngine.Keyframe(math.max(0.01f, duty - 0.03f), 1f), new UnityEngine.Keyframe(duty, 0f),
            new UnityEngine.Keyframe(0.97f, 0f), new UnityEngine.Keyframe(1f, 1f));

        public static UnityEngine.AnimationCurve Pulse() => new UnityEngine.AnimationCurve(
            new UnityEngine.Keyframe(0f, 0f), new UnityEngine.Keyframe(0.15f, 1f), new UnityEngine.Keyframe(0.45f, 1f),
            new UnityEngine.Keyframe(0.65f, 0f), new UnityEngine.Keyframe(1f, 0f));
    }

    // One-shot clone registration at PrefabUpdate before PrefabInitializeSystem (main menu; no game-mode gate),
    // polled every 30 frames until every source exists; then registers whatever exists once the prefab count has been
    // stable for 10 polls or a city runs. OnGamePreload is the fallback (before the city deserializes, so VFX still get
    // their own index). Sets RRWPrefabRegistry.EffectsDone on EVERY exit path so Machines never waits in vain.
    [RegisterSystem(SystemUpdatePhase.PrefabUpdate, Before = typeof(PrefabInitializeSystem), Order = RRWOrder.PropRegistry)]
    public partial class PropPrefabSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_PrefabQuery;
        private int m_Wait;
        private int m_LastCount = -1;
        private int m_Stable;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_PrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
        }

        protected override void OnUpdate()
        {
            if (--m_Wait > 0) return;
            m_Wait = 30;
            if (PropClones.Done)
            {
                // finish / keep "RRW Dust Puff" usable (Overridable cleared once its prefab entity exists)
                try { PropClones.PuffFixup(EntityManager, m_PrefabSystem); }
                catch (Exception e)
                {
                    RRWPrefabRegistry.DustPuffOk = false;
                    PropClones.PuffState = "fixup failed: " + e.Message;
                    RRWLog.ErrorOnce("props: dust puff fixup", e);
                }
                return;
            }
            try
            {
                int count = m_PrefabQuery.CalculateEntityCount();
                bool inGame = GameManager.instance != null && GameManager.instance.gameMode.IsGameOrEditor();
                if (count != m_LastCount) { m_LastCount = count; m_Stable = 0; }
                else m_Stable++;
                bool force = inGame || m_Stable >= 10;
                PropClones.Attempts++;
                PropClones.TryRegister(World, m_PrefabSystem, m_PrefabQuery, force, allowVfx: !inGame, phase: "PrefabUpdate");
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (PropClones.Done || !mode.IsGameOrEditor()) return;
            try
            {
                PropClones.Attempts++;
                PropClones.TryRegister(World, m_PrefabSystem, m_PrefabQuery, force: true, allowVfx: true, phase: "OnGamePreload(" + purpose + "," + mode + ")");
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (!PropClones.Done || !mode.IsGameOrEditor()) return;
            try { PropClones.PuffFixup(EntityManager, m_PrefabSystem); }   // usable from the first game update after a load
            catch (Exception e) { RRWPrefabRegistry.DustPuffOk = false; RRWLog.ErrorOnce("props: dust puff fixup", e); }
        }

        private static void Fail(Exception e)
        {
            PropClones.Done = true;
            RRWPrefabRegistry.EffectsOk = false;
            RRWPrefabRegistry.EffectsDone = true;
            RRWPrefabRegistry.DustPuffOk = false;      // Machines never waits for the puff prefab in vain
            RRWPrefabRegistry.DustPuffDone = true;
            PropClones.PuffState = "registration failed: " + e.Message;
            RRWLog.ErrorOnce("props: effect clone registration", e);
            RRWLog.Warn("props: effect clones disabled (plain barriers, plain heaps)");
        }
    }
}
