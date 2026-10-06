namespace RealisticRoadWorks.V3
{
    // [RegisterSystem(..., Order = RRWOrder.X)] values. Mod.cs registers in ascending Order; systems registered
    // Before/After the same target run in REGISTRATION order (UpdateSystem sorts by add index), so these numbers
    // also fix the run order of our systems that share a target. Never reuse a number.
    public static class RRWOrder
    {
        // PrefabUpdate (main menu, before PrefabInitializeSystem)
        public const int SurfaceRegistry = 50;        // Surfaces: RRW surface clones
        public const int PropRegistry = 52;           // Props: effect clones (RRW Amber Light, RRW Dust VFX) + carriers (beacon barrier, dust heaps).
                                                      // Before MachineRegistry: the excavator clone references RRW Dust VFX.
        public const int MachineRegistry = 55;        // Machines: "RRW Road Excavator" vehicle clones (no big dust)

        // ToolUpdate / ApplyTool
        public const int BulldozeIntercept = 100;     // Tools: After BulldozeToolSystem
        public const int WorksTag = 110;              // Tools: Before ApplyNetSystem

        // Modification1 (all Before Game.Tools.GenerateAreasSystem, in this order)
        public const int Director = 210;              // Director (registry rebuild first: sets NextProjectId)
        public const int LegacyMigration = 215;       // Persistence: after the Director rebuild; its sites are picked up next frame
        public const int TrafficRequests = 220;       // Traffic
        public const int Props = 230;                 // Props
        public const int MachineDirector = 240;       // Machines
        public const int SurfaceAreas = 250;          // Surfaces (last: CreationDefinitions are consumed by GenerateAreasSystem right after)

        // Modification4 (UpdateAt)
        public const int SurfaceTag = 400;            // Surfaces: LivePath + RRWDerived on new RRW areas
        public const int PropOwner = 410;             // Props: late Owner on props created / re-owned this frame
        public const int MachineTag = 420;            // Machines: LivePath on every Created descendant of a puppet (every frame)
        public const int PreviewHide = 430;           // Director: hides tool-preview Temp copies of hidden works roads. Runs in
                                                      // Modification2B (Before Game.Objects.SubObjectSystem / Game.Net.NodeAlignSystem) and
                                                      // uses TempFlags.Hidden - never the Hidden component on a Temp
        public const int PreviewHideCatchUp = 435;    // Optional: Mod4B/Mod5 catch-up adding TempFlags.Hidden to Temp sub-lanes /
                                                      // sub-objects of hidden Temps that vanilla did not flag yet

        // ModificationEnd
        public const int LaneClosure = 500;           // Traffic: After LaneDataSystem
        public const int ParkingClosure = 510;        // Traffic: After ParkingLaneDataSystem
        public const int PathInvalidation = 520;      // Traffic: After LanesModifiedSystem
        public const int HeapFill = 530;              // Props: After QuantityUpdateSystem
        public const int Excavation = 540;            // Ground: Before TerrainSystem

        // PreCulling / Rendering
        public const int DevCamera = 590;             // Dev (DEVTOOLS only): rrw.cam follow, PreCulling Before Game.Rendering.CameraUpdateSystem
        public const int MachineMover = 600;          // Machines: Before PreCullingSystem
        public const int RollerRender = 605;          // Optional: Machines' roller GameObjects (pose, LOD, lights, cleanup),
                                                      // PreCulling Before Game.Rendering.PreCullingSystem, after MachineMover (may live in it)
        public const int SurfaceMaterial = 610;       // Surfaces: After AreaBatchSystem
        public const int MachineBones = 700;          // Machines: After ObjectInterpolateSystem

        // UI
        public const int InfoSection = 800;           // UI: UpdateAt UIUpdate
        public const int ClickSelect = 805;           // UI: ToolUpdate After DefaultToolSystem (click a works site -> select the works street)
        public const int Tooltips = 810;              // UI: UpdateAt UITooltip

        // Serialize (Before/After SerializerSystem)
        public const int SaveSanitize = 900;          // Persistence: Before SerializerSystem
        public const int TrafficSaveGuard = 905;      // Traffic: Before SerializerSystem
        public const int SaveLaneAudit = 907;         // Persistence: Before SerializerSystem, after TrafficSaveGuard (read-only audit)
        public const int SaveRestore = 910;           // Persistence: After SerializerSystem
        public const int TrafficSaveRestore = 915;    // Traffic: After SerializerSystem
    }
}
