using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;

namespace RealisticRoadWorks.V3
{
    // Options UI. Registered by Mod.OnLoad; read live by every module through RRWSettings.Current.
    [FileLocation(nameof(RealisticRoadWorks))]
    [SettingsUITabOrder(kTiming, kTraffic, kVisuals, kMaintenance)]
    [SettingsUIGroupOrder(kTimingGroup, kTrafficGroup, kVisualsGroup, kMaintenanceGroup)]
    public class RRWSetting : ModSetting
    {
        public const string kTiming = "Timing";
        public const string kTraffic = "Traffic";
        public const string kVisuals = "Visuals";
        public const string kMaintenance = "Maintenance";
        public const string kTimingGroup = "TimingGroup";
        public const string kTrafficGroup = "TrafficGroup";
        public const string kVisualsGroup = "VisualsGroup";
        public const string kMaintenanceGroup = "MaintenanceGroup";

        public RRWSetting(IMod mod) : base(mod) { SetDefaults(); }

        // ---------------- Timing
        [SettingsUISection(kTiming, kTimingGroup)]
        [SettingsUISlider(min = 1, max = 240, step = 1, unit = Game.UI.Unit.kInteger)]
        public int ConstructionHoursPerKm { get; set; }

        [SettingsUISection(kTiming, kTimingGroup)]
        [SettingsUISlider(min = 1, max = 48, step = 1, unit = Game.UI.Unit.kInteger)]
        public int ConstructionMinHours { get; set; }

        [SettingsUISection(kTiming, kTimingGroup)]
        [SettingsUISlider(min = 1, max = 240, step = 1, unit = Game.UI.Unit.kInteger)]
        public int DemolitionHoursPerKm { get; set; }

        [SettingsUISection(kTiming, kTimingGroup)]
        [SettingsUISlider(min = 1, max = 48, step = 1, unit = Game.UI.Unit.kInteger)]
        public int DemolitionMinHours { get; set; }

        [SettingsUIHidden]   // crews work 24/7 for now (WorkTime.Effective ignores the shift)
        [SettingsUISection(kTiming, kTimingGroup)]
        public ShiftWindow Shift { get; set; }

        [SettingsUISection(kTiming, kTimingGroup)]
        [SettingsUISlider(min = 0, max = 200, step = 5, unit = Game.UI.Unit.kPercentage)]
        public int RushCostPercent { get; set; }

        [SettingsUISection(kTiming, kTimingGroup)]
        public bool InstantCancelUnfinished { get; set; }

        // ---------------- Traffic
        [SettingsUISection(kTraffic, kTrafficGroup)]
        public ClosurePolicy Policy { get; set; }

        [SettingsUISection(kTraffic, kTrafficGroup)]
        [SettingsUISlider(min = 10, max = 60, step = 5, unit = Game.UI.Unit.kInteger)]
        public int WorkZoneSpeedKmh { get; set; }

        // User request: sidewalks open from the paving phase (and in simple sites), and one half of the road
        // opens at work-zone speed during markings, as soon as no machine is on it. Off = the road stays closed until done.
        [SettingsUISection(kTraffic, kTrafficGroup)]
        public bool StagedOpening { get; set; }

        // RESERVED for a later version - mode H for rebuilt streets with houses. Hidden until its techniques are verified in game;
        // the current version never reads it.
        [SettingsUIHidden]
        [SettingsUISection(kTraffic, kTrafficGroup)]
        public bool HalfWidthWorks { get; set; }

        [SettingsUISection(kTraffic, kTrafficGroup)]
        public bool RerouteInFlight { get; set; }

        [SettingsUISection(kTraffic, kTrafficGroup)]
        public bool RelocateParkedCars { get; set; }

        [SettingsUISection(kTraffic, kTrafficGroup)]
        public bool HardCloseAfterDrain { get; set; }

        [SettingsUISection(kTraffic, kTrafficGroup)]
        public bool ShowSlowZoneIcon { get; set; }

        // ---------------- Visuals
        [SettingsUISection(kVisuals, kVisualsGroup)]
        public QualityPreset Quality { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool TerrainExcavation { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool SurfaceTextures { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool TopsoilStrip { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool Machines { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool MachineAnimation { get; set; }

        // IK dig cycle (the bucket bites into the trench floor, swings to the truck bed and empties it; breaker chops
        // in D0; the grader's bucket skims the surface). Off = the simpler keyframe cycle (cheaper).
        [SettingsUISection(kVisuals, kVisualsGroup)]
        [SettingsUIDisableByCondition(typeof(RRWSetting), nameof(IsDetailedDiggingDisabled))]
        public bool DetailedDigging { get; set; }

        // Tandem road rollers compact the gravel base (C2), the fresh asphalt behind the paver (C3) and the restored
        // ground (D2). Off = no rollers (the crew works without them).
        [SettingsUISection(kVisuals, kVisualsGroup)]
        [SettingsUIDisableByCondition(typeof(RRWSetting), nameof(IsRollersDisabled))]
        public bool Rollers { get; set; }

        // Greyed out while the switch has no effect (DetailedDiggingOn needs BonesOn, RollersOn needs MachinesOn;
        // the debug layers are dev-only and not part of the condition).
        public bool IsDetailedDiggingDisabled() => Quality < QualityPreset.Medium || !Machines || !MachineAnimation;
        public bool IsRollersDisabled() => Quality < QualityPreset.Medium || !Machines || MaxMachinesPerSite <= 0;

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool DustEffects { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool Beacons { get; set; }

        // Yellow temporary lines on the half that is open to traffic. Off = cones and fences only.
        // (Surfaces' SurfacePalette.kTempMarkingOn stays as the kill switch.)
        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool TempMarkings { get; set; }

        // Flashing amber head at the open-direction entry. Hidden while its experimental switch is off (RRWGates.AmberHead).
        [SettingsUISection(kVisuals, kVisualsGroup)]
        [SettingsUIHideByCondition(typeof(RRWSetting), nameof(IsApproachSignalsHidden))]
        public bool ApproachSignals { get; set; }

        public bool IsApproachSignalsHidden() => !RRWGates.AmberHead;

        [SettingsUISection(kVisuals, kVisualsGroup)]
        [SettingsUISlider(min = 0, max = 6, step = 1, unit = Game.UI.Unit.kInteger)]
        public int MaxMachinesPerSite { get; set; }

        // Long roads are split into sections with a crew each, so every machine works at a realistic speed.
        // Fewer crews = each crew covers more road (machines work faster, never beyond their speed limits).
        [SettingsUISection(kVisuals, kVisualsGroup)]
        [SettingsUISlider(min = 1, max = RRWConst.kMaxCrewsPerProject, step = 1, unit = Game.UI.Unit.kInteger)]
        public int MaxCrewsPerProject { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool ShowIcons { get; set; }

        [SettingsUISection(kVisuals, kVisualsGroup)]
        public bool ShowTooltips { get; set; }

        // ---------------- Maintenance
        [SettingsUISection(kMaintenance, kMaintenanceGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool FinishAllWorksNow { set { WorksRequests.Enqueue(new WorksRequest { Type = WorksRequestType.FinishAll }); } }

        [SettingsUISection(kMaintenance, kMaintenanceGroup)]
        [SettingsUIButton]
        public bool RebuildVisuals { set { WorksRequests.Enqueue(new WorksRequest { Type = WorksRequestType.RebuildVisuals }); } }

        [SettingsUISection(kMaintenance, kMaintenanceGroup)]
        public bool VerboseLogging { get; set; }

        public override void SetDefaults()
        {
            ConstructionHoursPerKm = 48;
            ConstructionMinHours = 6;
            DemolitionHoursPerKm = 24;
            DemolitionMinHours = 3;
            Shift = ShiftWindow.AllDay;
            RushCostPercent = 50;
            InstantCancelUnfinished = true;
            Policy = ClosurePolicy.Realistic;
            WorkZoneSpeedKmh = 30;
            StagedOpening = true;
            HalfWidthWorks = true;
            RerouteInFlight = true;
            RelocateParkedCars = true;
            HardCloseAfterDrain = false;
            ShowSlowZoneIcon = false;
            Quality = QualityPreset.High;
            TerrainExcavation = true;
            SurfaceTextures = true;
            TopsoilStrip = true;
            Machines = true;
            MachineAnimation = true;
            DetailedDigging = true;
            Rollers = true;
            DustEffects = true;
            Beacons = true;
            TempMarkings = true;
            ApproachSignals = true;
            MaxMachinesPerSite = 6;
            MaxCrewsPerProject = RRWConst.kDefaultCrewsPerProject;
            ShowIcons = true;
            ShowTooltips = true;
            VerboseLogging = false;
        }

        // ---- effective switches (preset x toggles x dev layers). Modules use ONLY these.
        public bool TerrainOn => Quality >= QualityPreset.Medium && TerrainExcavation && RRWDebug.On(DebugLayers.Terrain);
        public bool SurfacesOn => Quality >= QualityPreset.Medium && SurfaceTextures && RRWDebug.On(DebugLayers.Surfaces);
        public bool TopsoilOn => SurfacesOn && TopsoilStrip;
        public bool MachinesOn => Quality >= QualityPreset.Medium && Machines && MaxMachinesPerSite > 0 && RRWDebug.On(DebugLayers.Machines);
        // Crews per project (Director: PhasePlan.CrewCount(view, CrewsMax)); MachineBudget is PER CREW.
        public int CrewsMax => System.Math.Max(1, System.Math.Min(RRWConst.kMaxCrewsPerProject, MaxCrewsPerProject));
        public int MachineBudget => Quality == QualityPreset.Medium ? System.Math.Min(2, MaxMachinesPerSite) : System.Math.Min(RRWConst.kMaxMachinesPerSite, MaxMachinesPerSite);
        public bool BonesOn => Quality >= QualityPreset.Medium && MachineAnimation && RRWDebug.On(DebugLayers.Bones);   // bones are cheap: on from Medium
        public bool DustOn => Quality >= QualityPreset.High && DustEffects;
        // IK dig cycle (needs bones); dust puffs at breakout / dump need it and DustOn.
        public bool DetailedDiggingOn => BonesOn && DetailedDigging;
        public bool DigDustOn => DetailedDiggingOn && DustOn;
        // Road rollers (managed GameObject units; they count against MachineBudget per crew, kMaxMachinesGlobal and
        // kMaxRollersGlobal). The Director latches ProjectRecord.Rollers from it; Machines also gates spawns on it live (off: rollers leave).
        public bool RollersOn => MachinesOn && Rollers;
        public bool BeaconsOn => Quality >= QualityPreset.Medium && Beacons;
        public bool PropsOn => RRWDebug.On(DebugLayers.Props);
        public bool IconsOn => ShowIcons && RRWDebug.On(DebugLayers.Icons);
        public float WorkZoneSpeed => WorkZoneSpeedKmh / 3.6f;
        public bool StagedOpeningOn => StagedOpening && RRWDebug.On(DebugLayers.Closure);   // Director's input to PhasePlan.Stage / OpenLanes
        public bool TempMarkingsOn => SurfacesOn && TempMarkings;                             // Surfaces draws yellow lines
        public bool ApproachSignalsOn => ApproachSignals && RRWGates.AmberHead && PropsOn;   // Props spawns the amber head
    }

    public static class RRWSettings
    {
        // Never null after Mod.OnLoad. Defaults object until then (e.g. unit tests / early systems).
        private static RRWSetting s_Current;
        public static RRWSetting Current { get => s_Current; set => s_Current = value; }

        public static void Register(IMod mod)
        {
            var s = new RRWSetting(mod);
            s.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new RRWLocaleEN(s));
            AssetDatabase.global.LoadSettings(nameof(RealisticRoadWorks), s, new RRWSetting(mod));
            s_Current = s;
        }

        public static void Unregister()
        {
            s_Current?.UnregisterInOptionsUI();
        }
    }

    // en-US texts for the options UI and every UI / tooltip / notification string (RRWText keys).
    public class RRWLocaleEN : IDictionarySource
    {
        private readonly RRWSetting m_S;
        public RRWLocaleEN(RRWSetting setting) { m_S = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var d = new Dictionary<string, string>
            {
                { m_S.GetSettingsLocaleID(), "Realistic Road Works" },
                { m_S.GetOptionTabLocaleID(RRWSetting.kTiming), "Timing" },
                { m_S.GetOptionTabLocaleID(RRWSetting.kTraffic), "Traffic" },
                { m_S.GetOptionTabLocaleID(RRWSetting.kVisuals), "Visuals" },
                { m_S.GetOptionTabLocaleID(RRWSetting.kMaintenance), "Maintenance" },
                { m_S.GetOptionGroupLocaleID(RRWSetting.kTimingGroup), "Work time" },
                { m_S.GetOptionGroupLocaleID(RRWSetting.kTrafficGroup), "Traffic during works" },
                { m_S.GetOptionGroupLocaleID(RRWSetting.kVisualsGroup), "Work sites" },
                { m_S.GetOptionGroupLocaleID(RRWSetting.kMaintenanceGroup), "Maintenance" },
            };
            void Opt(string name, string label, string desc)
            {
                d[m_S.GetOptionLabelLocaleID(name)] = label;
                d[m_S.GetOptionDescLocaleID(name)] = desc;
            }
            Opt(nameof(RRWSetting.ConstructionHoursPerKm), "Construction time (hours per km)", "In-game working hours crews need to build one kilometre of road. Crews work around the clock.");
            Opt(nameof(RRWSetting.ConstructionMinHours), "Minimum construction time (hours)", "Even a short road takes at least this many working hours.");
            Opt(nameof(RRWSetting.DemolitionHoursPerKm), "Demolition time (hours per km)", "Working hours needed to break up and remove one kilometre of road.");
            Opt(nameof(RRWSetting.DemolitionMinHours), "Minimum demolition time (hours)", "Even a short road takes at least this many working hours to remove.");
            Opt(nameof(RRWSetting.Shift), "Working hours", "When crews work. Outside the shift the site is quiet, machines rest and beacons keep blinking.");
            Opt(nameof(RRWSetting.RushCostPercent), "Rush cost (% of road price)", "Overtime crews work around the clock at 1.5x speed. The price scales with the road's cost and the work still to do.");
            Opt(nameof(RRWSetting.InstantCancelUnfinished), "Bulldozing a just-started road cancels it", "Below 5 % progress, bulldozing a road under construction removes it at once with a full refund.");
            Opt(nameof(RRWSetting.Policy), "Closure policy", "Realistic: new and demolished roads close, upgrades of roads that serve buildings stay open as a slow zone. Always slow zone: traffic keeps flowing at work-zone speed (simple site, no excavation). Visual only: no effect on traffic (simple site, no excavation). Roads with machines on them stay closed until the machines have left; buildings that appear along them wait for access. A change applies to new works.");
            Opt(nameof(RRWSetting.StagedOpening), "Open sidewalks and one lane early", "Sidewalks open as soon as the new road is paved, behind a fence. During the markings one direction opens on its own half at work-zone speed with yellow temporary lines while the crew paints the other half, then the halves swap so the crew can finish both sides. Traffic in the closed direction takes a detour; houses on the closed half stay reachable. No direction opens on dead ends, tram roads, one-way roads, narrow roads or with a bus stop in the works half.");
            Opt(nameof(RRWSetting.HalfWidthWorks), "Rebuild streets with houses half by half", "Streets with houses that are rebuilt stay open on one half or on part of the lanes while the crew works on the rest.");
            Opt(nameof(RRWSetting.TempMarkings), "Yellow temporary markings", "Yellow temporary lane lines on the half of the road that is open during the works. Off: cones and fences only.");
            Opt(nameof(RRWSetting.ApproachSignals), "Flashing amber light", "A flashing amber traffic light where the open direction enters a half-closed road.");
            Opt(nameof(RRWSetting.WorkZoneSpeedKmh), "Work-zone speed (km/h)", "Speed limit on roads that stay open during works.");
            Opt(nameof(RRWSetting.RerouteInFlight), "Reroute vehicles already on their way", "Vehicles whose route crosses a closed road look for a new route at once.");
            Opt(nameof(RRWSetting.RelocateParkedCars), "Move parked cars", "Cars parked on a closed road without buildings are moved to nearby parking. Roads that serve buildings keep their parking.");
            Opt(nameof(RRWSetting.HardCloseAfterDrain), "Block closed lanes completely", "After the last vehicle has left a closed road, block its lanes like an accident does. Experimental.");
            Opt(nameof(RRWSetting.ShowSlowZoneIcon), "Slow-zone marker", "Show a bottleneck marker over roads that stay open as a slow zone.");
            Opt(nameof(RRWSetting.Quality), "Work-site detail", "Low: barriers, cones and closure only. Medium: excavation, ground textures and two animated machines. High: everything.");
            Opt(nameof(RRWSetting.TerrainExcavation), "Excavation", "New roads are dug into the ground and filled back up; demolished roads leave a trench that is restored.");
            Opt(nameof(RRWSetting.SurfaceTextures), "Ground textures", "Dirt, gravel and fresh asphalt spread along the site as the works progress.");
            Opt(nameof(RRWSetting.TopsoilStrip), "Topsoil strip", "Stripped, lighter soil along the site from survey and clearing on; the verges green up again within about an hour after completion; the finished road looks like any other road at once.");
            Opt(nameof(RRWSetting.Machines), "Machines", "Excavators, dump trucks, loaders and crew trucks work on the site.");
            Opt(nameof(RRWSetting.MachineAnimation), "Machine animation", "Excavator arms dig, swing and dump.");
            Opt(nameof(RRWSetting.DetailedDigging), "Detailed digging", "Excavators really dig: the bucket bites into the trench floor, swings over and empties into the waiting truck, which fills up bucket by bucket, with small dust puffs. Breakers chop the old asphalt. Off: a simpler, lighter dig animation.");
            Opt(nameof(RRWSetting.Rollers), "Road rollers", "Tandem rollers compact the gravel base and the fresh asphalt behind the paver, driving back and forth at walking pace.");
            Opt(nameof(RRWSetting.DustEffects), "Dust", "Small dust puffs at the excavator bucket and on fresh spoil and rubble heaps.");
            Opt(nameof(RRWSetting.Beacons), "Warning lights", "Blinking amber lamps on the barrier lines and amber beacons on the crew vehicles.");
            Opt(nameof(RRWSetting.MaxMachinesPerSite), "Machines per crew", "Upper limit of machines in one crew. Road rollers count against this limit and are dropped first. Crews far from the camera have none.");
            Opt(nameof(RRWSetting.MaxCrewsPerProject), "Crews per road", "Long roads are split into sections, each with its own crew, so machines work at a realistic pace. Fewer crews make each crew cover more road (machines work faster, within their speed limits). Only crews near the camera show machines.");
            Opt(nameof(RRWSetting.ShowIcons), "Map markers", "Show the road works marker above sites.");
            Opt(nameof(RRWSetting.ShowTooltips), "Tool tooltips", "Show the expected work time while drawing or bulldozing roads.");
            Opt(nameof(RRWSetting.FinishAllWorksNow), "Finish all works now", "Completes every construction and demolition in this city immediately.");
            Opt(nameof(RRWSetting.RebuildVisuals), "Rebuild all work-site visuals", "Removes and recreates every barrier, machine, texture and trench. Use it if something looks wrong.");
            Opt(nameof(RRWSetting.VerboseLogging), "Detailed log", "Write more detail to Logs/RealisticRoadWorks.log.");
            d[m_S.GetOptionWarningLocaleID(nameof(RRWSetting.FinishAllWorksNow))] = "Finish all road works in this city now?";
            d[m_S.GetEnumValueLocaleID(ShiftWindow.Day)] = "Day (07:00-19:00)";
            d[m_S.GetEnumValueLocaleID(ShiftWindow.Extended)] = "Extended (06:00-22:00)";
            d[m_S.GetEnumValueLocaleID(ShiftWindow.AllDay)] = "Around the clock";
            d[m_S.GetEnumValueLocaleID(ClosurePolicy.Realistic)] = "Realistic";
            d[m_S.GetEnumValueLocaleID(ClosurePolicy.AlwaysSlowZone)] = "Always slow zone";
            d[m_S.GetEnumValueLocaleID(ClosurePolicy.VisualOnly)] = "Visual only";
            d[m_S.GetEnumValueLocaleID(QualityPreset.Low)] = "Low";
            d[m_S.GetEnumValueLocaleID(QualityPreset.Medium)] = "Medium";
            d[m_S.GetEnumValueLocaleID(QualityPreset.High)] = "High";
            foreach (var kv in RRWText.English) d[kv.Key] = kv.Value;
            return d;
        }

        public void Unload() { }
    }

    // Keys + English defaults for every in-game string outside the options page (UI module, tooltips).
    // C# formats with string.Format; the .mjs gets ready-made strings from the info section.
    public static class RRWText
    {
        public const string P = "RealisticRoadWorks.";
        public const string Title = P + "UI.Title";
        public const string KindConstruction = P + "UI.Kind.Construction";
        public const string KindDemolition = P + "UI.Kind.Demolition";
        public const string KindMixed = P + "UI.Kind.Mixed";
        public const string Phase = P + "UI.Phase";                       // "{0} · {1}%"
        public const string EtaWork = P + "UI.EtaWork";                   // "≈ {0} of work · done {1}" ({1} = FinishToday/Tomorrow/Days text; CS2 days are not weekdays)
        public const string CrewsOff = P + "UI.CrewsOff";                 // "Crews off until {0}"
        public const string Finishing = P + "UI.Finishing";
        public const string TrafficClosed = P + "UI.Traffic.Closed";
        public const string TrafficSlow = P + "UI.Traffic.Slow";           // "Slow zone {0} km/h"
        public const string TrafficOpen = P + "UI.Traffic.Open";
        public const string Rerouted = P + "UI.Traffic.Rerouted";         // "{0} vehicles rerouted · {1} parked cars moved"
        public const string Paid = P + "UI.Paid";                         // "Paid {0}"
        public const string Refund = P + "UI.Refund";                     // "Refund if cancelled {0}"
        public const string Rush = P + "UI.Rush";                         // "Rush works ({0})"
        public const string Rushed = P + "UI.Rushed";
        public const string CantAfford = P + "UI.CantAfford";
        public const string CancelBuild = P + "UI.CancelBuild";           // "Cancel construction (refund {0})"
        public const string CancelDemolition = P + "UI.CancelDemolition";
        public const string CannotCancel = P + "UI.CannotCancel";
        public const string Focus = P + "UI.Focus";
        public const string Segments = P + "UI.Segments";                 // "{0} of {1} segments"
        public const string TipDemolition = P + "Tooltip.Demolition";     // "Demolition ≈ {0} h · road closes"
        public const string TipConstruction = P + "Tooltip.Construction"; // "Construction ≈ {0} h"
        public const string TipWorks = P + "Tooltip.Works";               // "{0} · {1} left"
        public const string ClearingTraffic = P + "UI.ClearingTraffic";   // "Waiting for traffic to clear"
        public const string WaitingAccess = P + "UI.WaitingAccess";       // "{0} buildings waiting for road access"
        // finish estimate, cancel confirmation, completion
        public const string FinishToday = P + "UI.FinishToday";           // "{0}" (clock)
        public const string FinishTomorrow = P + "UI.FinishTomorrow";     // "tomorrow {0}"
        public const string FinishDays = P + "UI.FinishDays";             // "in {0} days, {1}"
        public const string FinishUnknown = P + "UI.FinishUnknown";       // "–"
        public const string ConfirmCancel = P + "UI.ConfirmCancel";       // "Click again to confirm"
        public const string CompleteText = P + "UI.CompleteText";         // "Works complete · opening the road"
        public const string TipDemolitionOpen = P + "Tooltip.DemolitionOpen"; // "Demolition ≈ {0} h" (policy keeps the road open)
        public const string TipCancelBuild = P + "Tooltip.CancelBuild";   // "Cancels construction · refund {0}"
        // traffic state, release and crew status
        public const string TrafficPedestrians = P + "UI.Traffic.Pedestrians"; // Closed + Sidewalks open
        public const string TrafficOneSide = P + "UI.Traffic.OneSide";         // "{0} km/h" Closed + a car half (and sidewalks) open
        public const string OpeningAfterMachines = P + "UI.OpeningAfterMachines"; // releasing, machines still on the carriageway
        public const string CrewsWorking = P + "UI.CrewsWorking";         // "{0} crews working" (ProjectRecord.Crews > 1)
        public const string CrewSection = P + "UI.CrewSection";           // "{0} crews · about {1} m each" (tooltip / dev)
        public const string WaitingVehicles = P + "UI.WaitingVehicles";       // "{0}" buildings reachable on foot, waiting for vehicles
        // staged traffic management
        public const string TrafficOneDirection = P + "UI.Traffic.OneDirection";   // "{0}" km/h: a car half applied open
        public const string TrafficSwitchingSides = P + "UI.Traffic.SwitchingSides"; // Switch Swap / Drain
        public const string TrafficCrewChangingSides = P + "UI.Traffic.CrewChangingSides"; // "{0}" km/h: Switch Vacate
        public const string TrafficPedestriansReason = P + "UI.Traffic.PedestriansReason"; // "{0}" = StageReason text: no car half
        public const string BandLine = P + "UI.BandLine";                       // RESERVED for a later version: "Band {0}/{1} · {2}"

        public static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            { Title, "Road works" },
            { KindConstruction, "Under construction" },
            { KindDemolition, "Being demolished" },
            { KindMixed, "Construction and demolition" },
            { Phase, "{0} · {1}%" },
            { EtaWork, "≈ {0} of work · done {1}" },
            { CrewsOff, "Crews off until {0}" },
            { Finishing, "Finishing" },
            { TrafficClosed, "Closed – through traffic diverted" },
            { TrafficSlow, "Slow zone {0} km/h – access kept" },
            { TrafficOpen, "Open to traffic" },
            { Rerouted, "{0} vehicles rerouted · {1} parked cars moved" },
            { Paid, "Paid {0}" },
            { Refund, "Refund if cancelled {0}" },
            { Rush, "Rush works ({0})" },
            { Rushed, "Crews on overtime" },
            { CantAfford, "Not enough money to rush" },
            { CancelBuild, "Cancel construction (refund {0})" },
            { CancelDemolition, "Call off demolition" },
            { CannotCancel, "Road bed already removed" },
            { Focus, "Show work front" },
            { Segments, "{0} of {1} segments" },
            { TipDemolition, "Demolition ≈ {0} h · road closes" },
            { TipConstruction, "Construction ≈ {0} h" },
            { TipWorks, "{0} · {1} left" },
            { ClearingTraffic, "Waiting for traffic to clear" },
            { WaitingAccess, "{0} buildings waiting for road access" },
            { FinishToday, "{0}" },
            { FinishTomorrow, "tomorrow {0}" },
            { FinishDays, "in {0} days, {1}" },
            { FinishUnknown, "–" },
            { ConfirmCancel, "Click again to confirm" },
            { CompleteText, "Works complete · opening the road" },
            { TipDemolitionOpen, "Demolition ≈ {0} h" },
            { TipCancelBuild, "Cancels construction · refund {0}" },
            { TrafficPedestrians, "Closed to vehicles – sidewalks open" },
            { TrafficOneSide, "One side open at {0} km/h" },
            { OpeningAfterMachines, "Works complete · opening once the machines have left" },
            { CrewsWorking, "{0} crews working" },
            { CrewSection, "{0} crews · about {1} m each" },
            { WaitingVehicles, "{0} buildings reachable on foot, waiting for vehicle access" },
            { TrafficOneDirection, "One direction open at {0} km/h, other direction detours" },
            { TrafficSwitchingSides, "Switching sides: waiting for vehicles to clear" },
            { TrafficCrewChangingSides, "One direction open at {0} km/h · crew clearing the other side before the switch" },
            { TrafficPedestriansReason, "Closed to vehicles – sidewalks open ({0})" },
            { BandLine, "Band {0}/{1} · {2}" },
            { PhasePlan.StageReasonKey(StageBlockReason.DeadEnd), "dead end" },
            { PhasePlan.StageReasonKey(StageBlockReason.Track), "tram tracks" },
            { PhasePlan.StageReasonKey(StageBlockReason.Stop), "bus stop" },
            { PhasePlan.StageReasonKey(StageBlockReason.Narrow), "road too narrow" },
            { PhasePlan.StageReasonKey(StageBlockReason.OneWay), "one-way road" },
            { PhasePlan.StageReasonKey(StageBlockReason.Detour), "no detour" },
            { PhasePlan.StageReasonKey(StageBlockReason.Parking), "parked cars" },
            { PhasePlan.StageReasonKey(StageBlockReason.LaneLayout), "no separate lanes per direction" },
            { PhasePlan.PhaseKey(WorksPhase.Survey), "Survey & clearing" },
            { PhasePlan.PhaseKey(WorksPhase.Excavation), "Excavation" },
            { PhasePlan.PhaseKey(WorksPhase.Foundation), "Foundation" },
            { PhasePlan.PhaseKey(WorksPhase.Paving), "Paving" },
            { PhasePlan.PhaseKey(WorksPhase.Finishing), "Markings & finishing" },
            { PhasePlan.PhaseKey(WorksPhase.BreakUp), "Breaking up" },
            { PhasePlan.PhaseKey(WorksPhase.Removal), "Removing road bed" },
            { PhasePlan.PhaseKey(WorksPhase.Restore), "Restoring ground" },
            { PhasePlan.PhaseKey(WorksPhase.Complete), "Complete" },
        };

        // Active-language text with English fallback (only en-US ships in 3.0).
        public static string Get(string key)
        {
            try
            {
                var lm = GameManager.instance != null ? GameManager.instance.localizationManager : null;
                if (lm != null && lm.activeDictionary != null && lm.activeDictionary.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            return English.TryGetValue(key, out var e) ? e : key;
        }

        public static string Format(string key, params object[] args)
        {
            try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, Get(key), args); }
            catch { return Get(key); }
        }
    }
}
