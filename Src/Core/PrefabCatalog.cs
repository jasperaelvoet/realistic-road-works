using System.Collections.Generic;
using Game.Prefabs;
using Unity.Entities;

namespace RealisticRoadWorks.V3
{
    // Names of every built-in asset v3 uses (all checked against a runtime dump of the game's prefabs) and a cached resolver.
    // Never use Prop_*, dJP-*, ReachStacker01 (Find-It / asset packs / DLC).
    public static class PrefabNames
    {
        // ---- vehicles (CarPrefab / CarTrailerPrefab) used as puppets
        public const string Excavator = "MiningExcavator01";            // 14.7 x 12.6 x 33.8 m unscaled; NEVER spawned directly (dust)
        public const string RoadExcavator = "RRW Road Excavator";        // runtime clone of MiningExcavator01: no DustcloudVFX, worklights x0.4,
                                                                         // "RRW Dust VFX" (else vanilla DustcloudSmallVFX) on bone Arm02Bucket,
                                                                         // 7 s pulse; no WorkVehicle
        public const string RoadExcavatorQuiet = "RRW Road Excavator Quiet"; // same without the small dust (DustEffects off)
        public const string DustSmallVfx = "DustcloudSmallVFX";          // vanilla source of "RRW Dust VFX" (the good small dust, verified in game)
        // Never used: DustcloudVFX (huge at scale 0.4), WaterVapor*VFX (steam: a factory plume, unusable in game), Smoke*VFX.
        public const string Loader = "FrontendLoader01";                // fallback only (no excavator clone); spawned WITHOUT its trailer.
                                                                         // The Loader role (grader) is the "RRW Road Excavator Quiet" clone
        public const string LoaderTrailer = "FrontendLoaderTrailer01";
        public const string DumpTruck = "CoalTruck01";                  // 2.9 x 3.6 x 9.0 m; DeliveryTruck cargo Ore/Stone/Coal
        public const string CrewTruck = "RoadMaintenanceVehicle01";     // 2.4 x 2.5 x 7.4 m; amber beacons, arrow board
        public const string Tractor = "Tractor01";                      // optional roller proxy (not used in 3.0)

        // ---- static props (StaticObjectPrefab)
        public static readonly string[] SafetyBarriers = { "SafetyBarrier01", "SafetyBarrier02", "SafetyBarrier03" };     // ~1.8 m wide
        public static readonly string[] ConcreteBarriers = { "ConcreteBarrier01", "ConcreteBarrier02", "ConcreteBarrier03" }; // 1.22 m jersey
        public static readonly string[] Cones = { "SafetyCone01", "SafetyCone02", "SafetyCone04" };
        public const string TallCone = "SafetyCone03";
        public static readonly string[] WarningLights = { "WarningLight01", "WarningLight02" };   // steady red, no blink (verified in game). NOT used in 3.0:
                                                                                                 // closure lamps are the blinking "RRW Barrier Beacon" clones
        public const string Barrel = "Barrel01";                        // sparks carrier (not used in 3.0)
        public const string OreHeap = "CoalTruck01_OrePile01";          // QuantityObject 2.46 x 0.54 x 5.84 m (brown)
        public const string StoneHeap = "CoalTruck01_StonePile01";      // grey
        public const string CoalHeap = "CoalTruck01_CoalPile01";        // black
        public static readonly string[] Rubble = { "RubblePileMixed02", "RubblePileMixed04", "RubblePileConcrete02", "RubblePileConcrete03" }; // 7-10 m: rotate long side along the road
        public static readonly string[] CrewProps = { "RoadMaintenanceVehicle01_Props01", "RoadMaintenanceVehicle01_Props02", "RoadMaintenanceVehicle01_Props03" };
        // Fencing / staged opening. Names and sizes checked in a runtime dump of the vanilla prefabs (
        // StaticObjectPrefab, spawnable; long axis = local Z); how they render as unowned props is NOT verified in game: resolve with
        // PrefabCatalog.Static and fall back to SafetyBarriers (fences) or skip (signals) when Null; log once. The "...Random01"
        // variants are PlaceholderObjects: never spawn those directly.
        public static readonly string[] SiteFences = { "FenceIndustrialPieceHigh01", "FenceIndustrialPieceHigh02" };          // 4.0 x 2.3 m mesh panels: trench perimeter
        public static readonly string[] SiteFencesShort = { "FenceIndustrialPieceHighShort01", "FenceIndustrialPieceHighShort02" }; // 2.0 m: end fill
        // Kerb fence panel pairs (panel, short end panel; experimental switch). Dump sizes (x, y, z = long axis):
        // Low02 0.075 x 1.83 x 4.0 (expected pick: a temporary mesh site fence), LowShort02 2.018; Low01 0.35 x 1.2 x 4.04 (maybe a
        // walled base: dropped if in-game testing confirms that); High01/02 0.06-0.10 x 2.3 x 4.0. Selector RRWGates.KerbFenceVariant (index;
        // -1 = SafetyBarrier line). Never the "...Random01" PlaceholderObjects, never NetFencePrefab or asset-pack fences.
        public static readonly string[][] KerbFenceVariants =
        {
            new[] { "FenceIndustrialPieceLow02", "FenceIndustrialPieceLowShort02" },    // 0 (default)
            new[] { "FenceIndustrialPieceLow01", "FenceIndustrialPieceLowShort01" },    // 1 (the earlier choice)
            new[] { "FenceIndustrialPieceHigh01", "FenceIndustrialPieceHighShort01" },  // 2
            new[] { "FenceIndustrialPieceHigh02", "FenceIndustrialPieceHighShort02" },  // 3
        };
        public static readonly string[] KerbFences = { "FenceIndustrialPieceLow02" };                                         // default (expected pick); was Low01
        public static readonly string[] KerbFencesShort = { "FenceIndustrialPieceLowShort02" };                               // 2.018 m: end fill / R < 12 m
        public static string KerbFence(int variant, bool shortPanel) =>
            variant < 0 || variant >= KerbFenceVariants.Length ? null : KerbFenceVariants[variant][shortPanel ? 1 : 0];
        // Flashing-amber head (TrafficLightObject, 3.5 m pole; experimental switch RRWGates.AmberHead). EU / NA by theme.
        public static readonly string[] PortableSignals = { "EU_TrafficLightCar01", "NA_TrafficLightCar01" };
        // Signs (StaticObjectPrefab, TrafficSignObject + ThemeObject, ~2.2 m pole; names checked in the dump).
        public const string EuOneway = "EU_OnewaySign01", NaOneway = "NA_OnewaySign01";
        public const string EuNoEntry = "EU_DoNotEnter01", NaNoEntry = "NA_DoNotEnter01";
        public static readonly int[] EuSpeedPlates = { 30, 40, 50, 60, 80 };              // EU_Speedlimit<n> (km/h)
        public static readonly int[] NaSpeedPlates = { 25, 30, 35, 45, 55, 65, 70 };      // NA_Speedlimit<n> (mph)
        public static string Oneway(bool na) => na ? NaOneway : EuOneway;
        public static string NoEntry(bool na) => na ? NaNoEntry : EuNoEntry;
        public static string Signal(bool na) => na ? PortableSignals[1] : PortableSignals[0];
        // Speed plate for a work-zone speed: the largest plate <= the speed (EU km/h, NA mph); below the smallest -> the smallest.
        public static string SpeedPlate(float speedKmh, bool na)
        {
            int[] plates = na ? NaSpeedPlates : EuSpeedPlates;
            float v = na ? speedKmh / 1.609344f : speedKmh;
            int pick = plates[0];
            for (int i = 0; i < plates.Length; i++) if (plates[i] <= v + 0.01f) pick = plates[i];
            return (na ? "NA_Speedlimit" : "EU_Speedlimit") + pick;
        }
        // RESERVED for later
        public const string RoadBlockMarker = "Road Block";                 // MarkerObjectPrefab -> "RRW Lane Closure" clone (Traffic, LaneClosure)
        public const string LaneClosure = "RRW Lane Closure";               // the product's lane blocker clone (RRWOrder.LaneClosureRegistry)
        public const string LaneBlock = "RRW Lane Block";                   // the lane-drop experiment's clone (kept for it)
        public const string RoadDirt = "RRW Road Dirt";
        public const string MarkingBlackout = "RRW Marking Blackout";
        public const string OldAsphaltCover = "RRW Old Asphalt Cover";      // upgrade works: the old road carrying traffic (over the new markings)
        public const string GroundCover = "RRW Ground Cover";                // upgrade works: a strip that is not dug yet (grass over the new road)
        public const string OldAsphalt = "RRW Old Asphalt";                 // upgrade works: removed strip before break-up (Terrain only)
        public const string ConeLamp = "RRW Cone Lamp";                     // SafetyCone03 + RRW Amber Light 0.3 lux (optional, experimental switch)

        // ---- vanilla surfaces the RRW clones are built from (SurfacePrefab)
        public const string SrcAgriculture = "Agriculture Surface 01";  // dev-only experiment (RRW Topsoil Agri)
        public const string SrcOre = "Ore Surface 01";
        public const string SrcGrass = "Grass Surface 01";
        public const string SrcSand = "Sand Surface 01";
        public const string SrcConcrete = "Concrete Surface 01";
        public const string SrcPavement = "Pavement Surface 01";       // Y3 source of the TempMarking variants (experimental switch)

        // ---- RRW surface clones ("RRW " prefix: the bulldoze intercept drops deletes of these).
        public const string RrwPrefix = "RRW ";
        public const string TopsoilStrip = "RRW Topsoil";               // Ore untouched, RRWConst.kTopsoilTint (soil brown), alpha 35 % + fade clones;
                                                                         // from Agriculture Surface 01 only if RRWConst.kTopsoilUseAgriculture
        public const string TopsoilAgriDev = "RRW Topsoil Agri";        // dev-only (Agriculture Surface 01, rrw.surf.agri), never in product layers
        public const string Subgrade = "RRW Subgrade";                  // Ore untouched, tint (.85,.75,.65) - the "Src" recipe verified in game; no texture swap
        public const string BaseCourseCover = "RRW Base Course Cover";
        public const string FreshAsphalt = "RRW Fresh Asphalt";          // normal queue (under markings)
        public const string FreshAsphaltCover = "RRW Fresh Asphalt Cover";// raised queue (over markings)
        public const string TempMarking = "RRW Temp Marking";            // yellow temporary lines, tint RRWConst.kTempMarkingTint on a
                                                                         // light, uniform source; roundness 0.01, queue 2200, prio -87.
        // Selector RRWGates.TempLineSource: source variants Surfaces registers next to the default (Y1 = TempMarking,
        // Concrete); Y2 = Sand tint (1.15, .95, .20), Y3 = Pavement tint (1.10, .85, .12).
        public static readonly string[] TempMarkingVariants = { "RRW Temp Marking", "RRW Temp Marking Y2", "RRW Temp Marking Y3" };

        public static string Surface(SurfaceLayer l)
        {
            switch (l)
            {
                case SurfaceLayer.TopsoilStrip: return TopsoilStrip;
                case SurfaceLayer.Subgrade: return Subgrade;
                case SurfaceLayer.BaseCourseCover: return BaseCourseCover;
                case SurfaceLayer.FreshAsphalt: return FreshAsphalt;
                case SurfaceLayer.TempMarking: return TempMarking;
                case SurfaceLayer.MarkingBlackout: return MarkingBlackout;
                case SurfaceLayer.RoadDirt: return RoadDirt;
                case SurfaceLayer.OldAsphalt: return OldAsphalt;
                case SurfaceLayer.OldAsphaltCover: return OldAsphaltCover;
                case SurfaceLayer.GroundCover: return GroundCover;
                default: return FreshAsphaltCover;
            }
        }

        // Fade variants for scars (alpha variants, verified in game): "<name> a50", "<name> a20".
        // Registered for TopsoilStrip, Subgrade and FreshAsphalt (FreshAsphalt no longer becomes a scar; its variants
        // stay registered so old dev commands keep working, but nothing in the product uses them).
        public static string Alpha(string name, int percent) => percent >= 100 ? name : name + " a" + percent;
        public static bool HasAlphaVariants(SurfaceLayer l) => l == SurfaceLayer.TopsoilStrip || l == SurfaceLayer.Subgrade || l == SurfaceLayer.FreshAsphalt;

        // ---- effect clones + their carriers (registered by Props' PropPrefabSystem at PrefabUpdate in the main menu,
        //      verified in game; VFX clones must exist before a city deserializes).
        public const string SrcAmberLight = "CarAmberWarningSource";    // EffectPrefab (spot, range 30, 10 lux, volumetric)
        public const string AmberLight = "RRW Amber Light";             // EffectPrefab clone: conditions cleared, amber, RRWConst.kBeaconLux
        public const string DustVfx = "RRW Dust VFX";                   // EffectPrefab clone of DustcloudSmallVFX: conditions cleared, maxCount >= 128
        public const string BarrierBeacon = "RRW Barrier Beacon";       // SafetyBarrier01 + AmberLight @ (0,1,0), 1 s square blink, duty .5
        public const string DustHeapOre = "RRW Dust Heap Ore";          // CoalTruck01_OrePile01 + DustVfx @ (0,.4,0), 7 s pulse (emitter carrier verified in game)
        public const string DustHeapStone = "RRW Dust Heap Stone";      // CoalTruck01_StonePile01 + DustVfx, 7 s pulse (same carrier pattern)
        // Short-lived dust PUFF emitter (verified in the dig-animation prototype): CoalTruck01_OrePile01 carrier + the VANILLA
        // DustcloudSmallVFX @ (0, 0.2, 0), one-shot puff animation over RRWConst.kDustPuffAnimSeconds. Props registers it (main menu);
        // Machines spawns one per Breakout / Dump event (Hidden carrier, LivePath, DerivedGroup.DustPuff) and deletes it after
        // kDustPuffLifeSeconds. Vanilla source => registering while a city runs is valid too (no VFX clone index involved).
        public const string DustPuff = "RRW Dust Puff";
        public const string TEffect = "EffectPrefab";

        // ---- texture prefixes (TextureAsset names; verified in game)
        public const string TexGravelBase = "GravelWorldspace_BaseColor";
        public const string TexGravelNormal = "GravelWorldspace_Normal";
        public const string TexGravelMask = "GravelWorldspace_MaskMap";
        public const string TexAsphaltBase = "RoadEUWorldspace_BaseColor";
        public const string TexAsphaltNormal = "RoadEUWorldspace_Normal";
        public const string TexAsphaltMask = "RoadEUWorldspace_MaskMap";

        // ---- notification icons (NotificationIconPrefab)
        public const string IconClosed = "Road Maintenance Vehicle";
        public const string IconSlowZone = "Traffic Bottleneck Notification";

        // PrefabID type names
        public const string TStatic = nameof(StaticObjectPrefab);
        public const string TCar = nameof(CarPrefab);
        public const string TTrailer = nameof(CarTrailerPrefab);
        public const string TSurface = nameof(SurfacePrefab);
        public const string TIcon = nameof(NotificationIconPrefab);
    }

    // Resolves prefab entities by (type, name) once per process and caches them (prefab entities are stable for the
    // session; cache is cleared on preload anyway). Missing assets are logged once and return Entity.Null.
    public static class PrefabCatalog
    {
        private static readonly Dictionary<string, Entity> s_Cache = new Dictionary<string, Entity>();

        public static Entity Get(PrefabSystem ps, string type, string name)
        {
            string key = type + "/" + name;
            if (s_Cache.TryGetValue(key, out var e)) return e;
            e = Entity.Null;
            try
            {
                if (ps.TryGetPrefab(new PrefabID(type, name), out PrefabBase p) && ps.TryGetEntity(p, out Entity pe)) e = pe;
            }
            catch { e = Entity.Null; }
            if (e == Entity.Null) RRWLog.Once("missing-prefab-" + key, "built-in asset not found: " + type + " " + name);
            s_Cache[key] = e;
            return e;
        }

        public static Entity Static(PrefabSystem ps, string name) => Get(ps, PrefabNames.TStatic, name);
        public static Entity Car(PrefabSystem ps, string name) => Get(ps, PrefabNames.TCar, name);
        public static Entity Icon(PrefabSystem ps, string name) => Get(ps, PrefabNames.TIcon, name);

        // Resolves a list, skipping missing ones.
        public static List<Entity> Statics(PrefabSystem ps, string[] names)
        {
            var list = new List<Entity>(names.Length);
            foreach (var n in names) { var e = Static(ps, n); if (e != Entity.Null) list.Add(e); }
            return list;
        }

        public static void Clear() => s_Cache.Clear();
    }

    // Main-menu prefab registries (PrefabUpdate) report here, so a later registry can wait for an earlier one's clones
    // without referencing another module (Machines' excavator clone uses Props' "RRW Dust VFX"). Process-wide; never reset.
    public static class RRWPrefabRegistry
    {
        public static bool SurfacesDone;   // Surfaces: SurfaceRegistrySystem finished (ok or failed)
        public static bool EffectsDone;    // Props: PropPrefabSystem finished (ok or failed)
        public static bool EffectsOk;      // ... and "RRW Dust VFX" + "RRW Amber Light" exist
        // Props' per-load runtime check of the "RRW Dust VFX" clone's VFX slot: 0 = not checked yet
        // (new load), 1 = verified, -1 = invalid (drawn through another graph's slot / missing). Single writer: Props.
        // Readers (Machines' dusty excavator clone) must not show the clone while it is -1.
        public static int DustCloneState;
        // Props (single writer) publishes whether the dust carriers use the VANILLA DustcloudSmallVFX (true) or the
        // "RRW Dust VFX" clone. Default true in EVERY build (the clone renders black; DevTools builds used to default to Auto).
        // Machines' dusty excavator follows it: while true it never shows the clone's dust on the bucket.
        public static bool DustVanilla = true;
        // Props (single writer) finished registering "RRW Dust Puff" (Done) and it exists (Ok). Machines spawns no puffs
        // while !DustPuffOk (the dig cycle runs without dust).
        public static bool DustPuffDone;
        public static bool DustPuffOk;
        public static bool MachinesDone;   // Machines: MachinePrefabSystem finished
        public static bool ExcavatorOk;    // ... and "RRW Road Excavator" exists (else the FrontendLoader01 fallback)
    }
}
