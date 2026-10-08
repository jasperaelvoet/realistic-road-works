using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // Tuning constants shared by all modules. Change here, never duplicate.
    public static class RRWConst
    {
        // ---- time
        public const uint kFramesPerDay = 262144;                 // Game.Simulation.TimeSystem.kTicksPerDay
        public const float kFramesPerHour = kFramesPerDay / 24f;  // 10922.67
        public const float kInstantCancelProgress = 0.05f;        // cancelling a construction below this = delete + full refund (paid by us)
        public const float kRushRateMultiplier = 1.5f;
        public const uint kModelWindow = 256;                     // ProgressModel re-anchor window (sim frames)
        public const uint kClearTrafficTimeoutFrames = 600;       // demolition D0 waits at most this long for vehicles to leave

        // ---- phase boundaries (p)
        public const float kC1 = 0.08f, kC2 = 0.35f, kC3 = 0.58f, kC4 = 0.86f;
        public const float kD1 = 0.35f, kD2 = 0.75f;

        // ---- terrain (metres relative to the profile grade = curve Y + per-edge grade offset)
        public const float kProfileGradeBias = -0.21f;   // FALLBACK grade offset only (measured in game on Small Road Asymmetric);
                                                         // the real one is NetCompositionData.m_SurfaceHeight.min (EdgeSection.GradeOffset)
        public const float kSubgradeDepthSmall = 0.75f;  // composition width <= kLargeRoadWidth
        public const float kSubgradeDepthLarge = 1.0f;
        public const float kLargeRoadWidth = 20f;
        public const float kBaseTopY = -0.125f;          // end of C2 / start of D1 / reveal-hold floor (reveal depth ~ -0.125, verified in game)
        public const float kRevealBedY = kBaseTopY;      // floor Ground holds under a just-revealed road
        public const int kRevealHoldUpdates = 45;        // >= 30 visible frames (verified in game) before the clip may return; counted in RRWClock.UpdateIndex
        public const float kDemolitionDepth = 0.5f;      // D1 digs the base out to -0.5
        public const float kMaxDepth = 1.25f;            // decal box is terrain +-1.5 m: depth + |grade offset| must stay < 1.5
        public const float kNaturalMargin = 0.5f;        // MORPH bracket margin around sampled natural ground
        public const float kUnsampledBracket = 20f;      // MORPH bracket when natural ground is unknown; decays with (1-t)^4 (TerrainProfile.Morph)
        public const float kDeepClip = 62f;              // clip slab depth below grade (verified in game: -61/-60 works)
        public const int kTerrainTriggerMinUpdates = 15; // global: at most one re-render trigger frame per 15 updates (~4 Hz)
        public const int kNaturalSampleDelayUpdates = 30;// new road: sample CPU heights this many updates after NATURAL was applied
        public const float kNaturalSampleStep = 4f;      // metres between natural-ground samples
        public const float kC1DepthEase0 = 0.15f, kC1DepthEase1 = 0.85f; // C1 per-edge depth = smoothstep(ease0, ease1, g)
        public const int kReplacePreRollUpdates = 10;    // Replaced roads stay visible this long while the gravel cover appears

        // ---- surfaces
        public const float kFrontQuantum = 2f;           // decal fronts move in 2 m steps
        public const float kMinSpan = 1.5f;              // edge-local pieces are never shorter than this (snapped/widened, never dropped)
        public const float kSpanOverlap = 1.0f;          // hand-over overlap where two layers meet at a front (>= max smooth + 0.4)
        public const float kNodeOverlap = 0.75f;         // Roads-layer polygons extend this far past an interior chain node
        public const int kAreaMinUpdatesBetweenWrites = 15; // <= 4 Hz per project
        public const int kAreaRewritesPerUpdateGlobal = 8;  // global cap (round-robin; phase-change / rebuild bypasses exempt)
        public const float kAreaNodeStep = 4f;           // max polygon node spacing along the road (adaptive on curves, EdgeArc.Strip)
        public const float kAreaMinNodeStep = 1f;
        public const float kAreaMaxSagitta = 0.02f;      // adaptive step keeps the chord error <= 2 cm
        public const float kAreaMinNodeGap = 0.3f;       // drop polygon vertices closer than this
        public const float kTopsoilMargin = 2f;          // per side, beyond composition width / 2
        public const float kSubgradeMargin = 1f;
        public const float kBaseCourseMargin = 2f;
        public const float kAsphaltMargin = 0.35f;       // beyond the outer carriage-interval edges (asphalt smooth 0.25 stays opaque over lines)
        public const float kCarriageMergeGap = 0.5f;     // carriage intervals closer than this merge (a median stays a gap)
        public const float kAreaBoxHalf = 1.5f;          // decal box half height (heightOffset 1.5, verified in game)
        public const float kAreaResnapDepthDelta = 0.5f; // Surfaces rewrites an area's nodes when the floor moved this much since its last write
        public const int kRenderQueueRaise = 100;        // 2000 -> 2100 for the "Cover" layers (verified in game)
        // Surface palette. Only sources verified to render in game are primary: Ore material untouched (dirt / topsoil),
        // GravelWorldspace swap (base course), RoadEUWorldspace swap tint .45 (fresh asphalt). Tints multiply the source.
        public static readonly float3 kSubgradeTint = new float3(0.85f, 0.75f, 0.65f); // "Subgrade Src" recipe verified in game (brown dirt)
        // Topsoil: the subgrade dirt hue, less saturated / darker than the old (1.0, .92, .70) yellow
        // sand, a touch lighter than the subgrade; 35 % alpha so the grass shows through. Read by Surfaces' SurfacePalette.
        public static readonly float3 kTopsoilTint = new float3(0.90f, 0.78f, 0.69f);   // freshly turned soil, "grass stripped to soil"
        public const float kTopsoilAlpha = 0.35f;        // topsoil lets the grass show through (alpha path verified in game)
        public const bool kTopsoilUseAgriculture = false;// opt-in variant: build "RRW Topsoil" from Agriculture Surface 01 (visually unverified; may read as a crop field)
        public static readonly float3 kAgricultureTopsoilTint = new float3(1f, 1f, 1f);
        public const float kAsphaltTint = 0.45f;         // fresh-asphalt tint on the RoadEUWorldspace swap (verified in game)

        // ---- scars / curing (in-game hours after completion; alpha a100 -> a50 -> a20 -> deleted)
        // There is NO curing layer any more - a finished road looks vanilla the moment it opens
        // (PhasePlan.BecomesScar no longer keeps FreshAsphalt). The kCure* values are unused, kept for reference only.
        public const float kCureA50Hours = 6f, kCureA20Hours = 12f, kCureEndHours = 18f;   // UNUSED (no curing layer)
        // Soil scars (construction verges, demolition scar): only the End hours drive the look (Surfaces
        // SurfacePalette: hold full strength for ScarHoldFraction of the life, then fade in small steps to 0 at End).
        // The soil A50/A20 hours are no longer used for the look (kept for reference).
        // Construction verge scars (topsoil + subgrade) fade out within ~1 in-game hour and are deleted at End.
        public const float kVergeA50Hours = 0.25f, kVergeA20Hours = 0.6f, kVergeEndHours = 1.0f; // brown verge after construction
        public const float kScarA50Hours = 8f, kScarA20Hours = 11f, kScarEndHours = 12f;    // demolition topsoil scar

        // ---- geometry / modes
        public const float kShortEdge = 8f;              // projects shorter than this are mode D
        public const float kWorksMinLaneSpeed = 30f / 3.6f;
        public const float kSplitMatchTolerance = 1f;    // split / preview matching: 3 curve points within 1 m of a works edge
        public const float kCombineMatchTolerance = 1.5f;

        // ---- machines
        public const float kExcavatorScale = 0.4f;       // verified in game
        public const int kMaxMachinesPerSite = 6;
        public const int kMaxMachinesGlobal = 40;
        public const float kMachineSpawnRadius = 600f;
        public const float kMachineDespawnRadius = 800f;   // players watch from 150-600 m: a puppet is despawned only beyond this
                                                           // distance or after kMachineCulledUpdates with CullingInfo.m_CullingIndex == 0
        public const int kMachineCulledUpdates = 60;     // "out of sight" = culled (m_CullingIndex == 0) this many consecutive updates
        public const float kPostSiteParkSeconds = 120f;  // UNUSED (post-site puppets no longer park: see kPostSiteMaxSimFrames)
        // ---- machine report / release contract
        // The leave / release caps run on GAME time (sim frames / the machine clock, which stops while paused
        // and follows the game speed), never on rendered frames (RRWClock.UpdateIndex keeps counting while paused and
        // depends on the frame rate: machines vanished mid-drive at 144 fps and while paused).
        // Raised from 3600 (60 game s): a leaver drives to the CONNECTED chain end and is normally removed there
        // once out of the camera's view (culled / beyond kMachineDespawnRadius); the removal at this cap, even if visible, is a
        // last resort only. The release gate (kCompletionMachineWaitSimFrames) and the C4 Vacate cap follow it.
        public const int kPostSiteMaxSimFrames = 7200;   // a leaving puppet (DriveOut, post-site, released project) is despawned when
                                                         // out of sight, and at the latest this many sim frames (120 game seconds) after it
                                                         // began leaving - even if visible. No machine is ever left parked on a road.
        public const double kPostSiteMaxSeconds = kPostSiteMaxSimFrames / 60.0;   // the same cap in machine seconds (Machines' clock)
        public const int kMachinesReportStaleUpdates = 30;   // ProjectRecord.MachinesReportUpdate older than this = no trustworthy report
        public const int kCompletionMachineWaitSimFrames = kPostSiteMaxSimFrames + 600; // Director's safety cap: after this many sim frames
                                                         // of release the road opens even without a clear report (logs Warn once)
        public const int kCompletionMachineWaitUpdatesHard = 36000; // last-resort cap in rendered updates (~10 min at 60 fps, paused
                                                         // included) in case the sim clock never advances
        // A puppet that has not moved for this long while its plan says it should move (not a Hold / Parked /
        // intended wait) is STUCK: Machines resolves it (priority yield / back-out / re-plan / leave) and counts it in
        // ProjectRecord.MachinesStuck (rrw.check / rrw.mx.check must show 0 stuck puppets older than this).
        public const float kMachineStuckSeconds = 20f;   // machine seconds
        public const float kZoneCentreTolerance = 0.25f; // m: a box/lane within this of the half split (EdgeSection.DirSplit) counts for BOTH halves
        public const float kZoneFootprintSlack = 1f;     // m: a box reaching beyond [TrimU0 - this, TrimU1 + this] is Outside
        // Staged opening (user request): the C4 crew works in the chain-RIGHT half, the chain-LEFT half opens first (C4a).
        // Halves are DIRECTION halves (EdgeSection.DirSplit); with the side swap (kC4SwapOn) C4b reverses the roles.
        public const RoadZones kStagedOpenHalf = RoadZones.LeftHalf;
        public const RoadZones kStagedWorksHalf = RoadZones.RightHalf;
        public const float kMachineMinChain = 30f;       // trimmed chains shorter than this get the crew truck only
        public const float kSharedNodeSlotShift = 25f;   // slots within this of a node shared with another project move inward
        public const float kCrossProjectRadius = 60f;    // overlap planner considers other projects' boxes within this distance
        public const float kTruckSpeed = 5f;             // m/s cruise inside the site (MachineLimits caps it at 5.5)
        public const float kTruckReverseSpeed = 2f;      // = MachineLimits dump truck VRev
        public const float kTruckAccel = 1.5f;           // = MachineLimits dump truck Accel
        public const float kMaxReverseLeg = 35f;         // no reverse leg is longer than this (trucks turn instead)
        public const float kTurnMinFlatWidth = 9f;       // three-point turn needs this flat floor width
        public const float kMachineGap = 3f;             // minimum free gap between two machines' bounding boxes along the chain
        public const float kVergeOffset = 3.5f;          // verge lateral = HalfWidth + this
        public const float kDigCycleSeconds = 10f;       // step-and-dig cycle (sim seconds); first half digs in place, second half moves
        public const float kFinisherLead = 1.7f;         // finisher pivot = F + this (body covers the 2 m decal quantum)
        public const float kExcavatorBack = 3.5f;        // excavator pivot = F - this

        // ---- crew sections. A project is split into equal chain sections [B_i, B_i+1] of [0, U]; each
        // section has its own crew and its own front F_i = B_i + (B_i+1 - B_i) * s (s = the unit sweep of the phase, shared by
        // all sections). Interior boundaries lie on the kFrontQuantum grid. The count is latched per phase by the Director
        // (ProjectRecord.Crews, PhasePlan.CrewCountFor) and never changes mid-phase except on hard resets.
        public const int kMaxCrewsPerProject = 6;        // hard cap (SpanSet capacity and the cancel-crew flag bits rely on it, <= 8)
        public const int kDefaultCrewsPerProject = 4;    // RRWSetting.MaxCrewsPerProject default
        public const float kMinSectionLength = 60f;      // never a section shorter than this (trimmed chain / crews)
        public const float kMaxSectionLength = 400f;     // a longer road gets more crews even when one crew would be fast enough
        public const int kMaxActiveCrewsGlobal = 8;      // crews with spawned puppets at once (Machines LOD, nearest crews first)
        public const float kCrewReturnSpeedFactor = 0.5f;// return-aware follow anchors move back at most this x the role's VFwd
        // A return-aware follow-back is usually reversed (no room to turn on a works lane), so it never moves
        // back faster than this fraction of the role's VRev (= 0.9 x Machines' 0.92 planning margin; crew truck 1.656 m/s).
        // PhasePlan.ReturnAware (the Core anchor) and Machines' Ret leg cap both use it, so the two agree exactly.
        public const float kCrewReturnRevFactor = 0.828f;
        public const float kCrewReturnMarginSeconds = 10f;// ... and arrive this many machine seconds before the phase / pick-up ends
        // Against clones popping in at hand-overs: a puppet that misses the budget still takes a free slot of its
        // role when that slot's anchor is in view (<= kMachineSpawnRadius from the camera) and it reaches it within this many machine
        // seconds at its limits (it arrives late, the front-relative lag model covers it). Otherwise it leaves and the role spawns
        // at its anchor as before (out of view, or too far).
        public const float kHandOverLateSeconds = 90f;
        // Against crews meeting at section boundaries: in C3 the crew truck of a crew > 0 comes home this far
        // beyond StartSlotIn(0) (B_i + 12 m): crew i-1's paver finishes its section at B_i + kFinisherLead with its body reaching
        // ~B_i + 5 m, so B_i + 6 left no gap.
        public const float kC3InteriorHomeShift = 6f;
        public const float kHandOverSlackSeconds = 10f;  // phase hand-over: a live puppet keeps a role whose new anchor it reaches within
                                                         // the front's lead-in + this; otherwise it leaves and the role spawns at its anchor
        // ---- realistic speeds: machine seconds (machine clock = sim clock). See MachineLimits.
        public const float kSpeedCheckTolerance = 0.05f; // rrw.mx.check: observed speed may exceed a limit by 5 % (+0.05 m/s) before it counts
        public const float kAccelCheckTolerance = 0.10f; // ... acceleration by 10 % (+0.1 m/s^2)
        public const float kEmergencyDecel = 3.0f;       // guard brake (BrakeSplice) only; planned legs never brake harder than Accel

        // ---- procedural tandem road roller, ported from the roller prototype (verified in game; GameObject + MeshRenderer +
        // runtime HDRP/Lit, layer 0, shadows on, ~2 k tris LOD0). Dimensions of the built model (HAMM HD+ 110 / BOMAG BW 154 class):
        public const float kRollerLength = 4.72f;        // m, bumper to bumper (box for the report, planner and guard)
        public const float kRollerWidth = 1.90f;         // m, over the drum hub motors
        public const float kRollerHeight = 3.08f;        // m, at the beacon
        public const float kRollerWheelbase = 3.30f;     // m, drum axle to drum axle (articulation joint in the middle)
        public const float kRollerDrumRadius = 0.60f;    // drums 1.2 m x 1.68 m
        public const float kRollerDrumWidth = 1.68f;
        public const float kRollerMaxArticulationDeg = 38f; // joint stop (path + steer clamped)
        public const float kRollerLod0Distance = 250f;   // camera distance: LOD0 (~2 k tris) below, LOD1 (~0.3 k) up to kRollerLod1Distance,
        public const float kRollerLod1Distance = 2000f;  // renderers disabled beyond (defaults verified in game; may be tuned)
        public const float kRollerVibAmplitude = 0.012f; // m, drum shimmer while a vibrating pass drives (frames 0.2 x); off when stopped / reversing
        public const float kRollerBeaconNits = 3000f;    // emissive levels (verified in game at dusk / night): beacon double flash ~0.8 Hz,
        public const float kRollerLampNits = 300f;       // head + roof work lamps,
        public const float kRollerTailNits = 150f;       // tail lamps
        public const int kRollerBeaconPhases = 4;        // shared beacon materials (phase offsets 0, 1/4, 1/2, 3/4); a roller picks one by seed
        public const int kMaxRollersPerCrew = 2;         // C3: breakdown + finish roller; C2 / D2: one
        public const int kMaxRollersGlobal = 12;         // on top of the per-crew MachineBudget and the global kMaxMachinesGlobal (rollers count in both)
        public const float kRollerTwoMinSection = 100f;  // a crew section (SecHi - SecLo) shorter than this gets one C3 roller only
        public const float kRollerMinPass = 8f;          // a compaction window shorter than this is "not open": no roller there (Crew slot None)
        public const float kRollerEndClear = 5f;         // roller pivot >= SecLo + this and <= SecHi - this (box ends ~2.6 m from the boundary)
        public const float kRollerC2Back = 22f;          // C2 / D2 window top = F_i - this: grader shuttle rear (F - 6 - 6.75) + 6 m + roller half
        public const float kRollerC2PassLength = 16f;    // C2 / D2 pass length
        public const float kRollerC2EndClear = 36f;      // C2 / D2 window top <= SecHi - this: clear of the end-side truck base slots (EndSlotIn 1/2)
        public const float kRollerC2HandOffF = 0.97f;    // C2 f >= this: the roller of crew i < last parks at its hand-off spot (relayed to crew
                                                         // i + 1's C3 breakdown roller); the last crew's roller drives out
        public const float kRollerHandOffBack = 6.5f;    // hand-off spot = B_(i+1) - this (crew i's section end, clear of its C3 truck bases)
        public const float kRollerC3Back0 = 10.5f;       // C3 breakdown window top = F_i - this: front 6.1 m behind the paver's screed (F - 2)
        public const float kRollerC3Pass0 = 14f;
        public const float kRollerC3Back1 = 33f;         // C3 finish roller window top = F_i - this (3.7 m behind the breakdown window's rear)
        public const float kRollerC3Pass1 = 15f;
        public const float kRollerCrewTruckGap = 8.5f;   // C3 window bottom >= crew truck anchor + this (truck half 3.7 + roller half 2.36 + 2.4)
        public const float kC3CrewTruckBackOneRoller = 44f;  // C3 crew truck follows at F - this with one roller (was 32 without rollers)
        public const float kC3CrewTruckBackTwoRollers = 62f; // ... with two rollers
        public const float kRollerC3WorksHalfF = 0.92f;  // C3 f >= this: passes stay in the C4a works half (kStagedWorksHalf), so the rollers
                                                         // leave at the C3 -> C4 hand-over without blocking the half that opens first
        public const float kRollerPassShift = 0.8f;      // lateral shift per outbound pass (m; drum 1.68 m: overlapping passes), bouncing
        public const float kRollerReversePause = 1.5f;   // machine seconds stopped at each reversal (vibration off)
        public const float kRollerSpeedBase = 1.0f;      // m/s pass speed, C2 base course (vibration on)
        public const float kRollerSpeedBreakdown = 1.2f; // C3 breakdown roller (vibration on)
        public const float kRollerSpeedFinish = 1.5f;    // C3 finish roller (static)
        public const float kRollerSpeedBackfill = 1.0f;  // D2 (vibration on)
        public const bool kRollerInRestore = true;       // D2 backfill compaction (optional duty; false = no D2 roller slot)
        public const float kRollerSpawnDeferSeconds = 60f; // (unused: a roller whose anchor is in view drives in from
                                                         // an out-of-view spot behind it or waits - it never spawns at the anchor in view)

        // ---- IK dig cycle (verified in the dig-animation prototype on the 0.4 MiningExcavator01 clone). DigCycle.cs holds the
        // pure schedule. The period is FIXED (planner, truck hops and bones agree on one grid); a fitted swing / return shorter than
        // nominal leaves an operator hold at the ready pose; a longer one is compressed down to kDigSwingMin.
        public const float kDigCyclePeriod = 20f;        // machine seconds (Detailed digging; the simple keyframe cycle keeps kDigCycleSeconds)
        public const float kDigLowerSeconds = 2.0f;      // ready -> boom down / stick out over the far bite (bucket in the air)
        public const float kDigPenetrateSeconds = 1.0f;  // teeth bite at the far end of the floor
        public const float kDigDragSeconds = 3.0f;       // drag towards the machine along the floor (Break: the chop strikes)
        public const float kDigCurlSeconds = 1.3f;       // curl: the bucket fills and rolls back (breakout = start of this key)
        public const float kDigLiftSeconds = 1.6f;       // lift clear of the trench
        public const float kDigDumpSeconds = 1.6f;       // open over the bed / spoil, about the bucket pin
        public const float kDigShakeSeconds = 0.7f;      // shake it empty (attitude +-12 deg at 3 Hz)
        public const float kDigSwingNominal = 4.4f;      // swing out / back (s) when the fit gives none: ~100 deg at kDigSlewRateAvg
        public const float kDigSwingMin = 2.6f;          // never faster than this per swing (slew peak <= ~36 deg/s)
        public const float kDigSlewRateAvg = 23f;        // deg/s average swing (smoothstep peak 1.5 x = 34.5, verified in game)
        public const float kDigDumpAt = 0.45f;           // the Dump event: this fraction into the dump key (bucket half open over the bed)
        public const float kDigGroundMargin = 0.3f;      // s after the curl ends during which the root still stands still (bucket leaving the floor)
        public const float kDigTipClear = 0.02f;         // tip above the floor guard while dragging (gap 0.02 m, verified in game)
        public const float kDigBite = 0.15f;             // experimental: the drag runs this far BELOW the visible floor (teeth in the soil); 0 = the
                                                         // verified scrape at floor + kDigTipClear. Machines may override it at runtime (rrw.mx.dig bite=)
        public const float kDigDumpClear = 0.30f;        // lowest bucket point above the truck box at the dump poses (0.31 verified in game)
        public const float kDigFitClear = 0.40f;         // clearance the swing / return fit aims for
        public const float kDigTruckClearMin = 0.15f;    // never closer to the truck box than this (rrw.mx.check)
        public const int kBreakStrikes = 5;              // D0 breaker: chop strikes in the drag key, then scoop / lift / swing / dump the pieces
        public const float kBreakLift = 0.4f;            // tip height above the road surface between strikes (m)
        public const int kBucketsPerLoad = 4;            // a truck is full after 4 Dump events (25 % each); it leaves after the 4th
        public const float kTruckCapacity = 20000f;      // CoalTruck01 DeliveryTruck amount at 100 % (verified in game)
        public const float kTruckLeaveAfterShake = 1.0f; // the full truck starts moving this long after the 4th dump's shake ends
        public const float kGraderBucketReach = 4.5f;    // grader (excavator clone) bucket tip ahead of the swing axis while spreading / scraping
        public const float kGraderSkim = 0.03f;          // tip above the floor on working (forward) strokes
        public const float kGraderLift = 0.6f;           // tip above the floor on repositioning (reverse) strokes
        public const float kGraderAttitude = 60f;        // bucket attitude while skimming (deg from straight down, + = curled: back of the bucket flat)
        public const float kGraderBlendSeconds = 1.0f;   // skim <-> lift transition (machine s)
        // Dust puffs ("RRW Dust Puff" emitter props, vanilla DustcloudSmallVFX, tan puff verified in game): at Breakout and Dump, DustOn only.
        public const float kDustPuffAnimSeconds = 4.0f;  // carrier effect animation (one puff)
        public const float kDustPuffLifeSeconds = 3.6f;  // machine seconds until the emitter is deleted (0.9 x anim; deleting kills the particles)
        public const int kDustPuffMaxLive = 24;          // global budget of live puff emitters (oldest are never cut short: new puffs are skipped)
        public const int kDustPuffMaxPerProject = 6;
        public const float kDustPuffMinInterval = 2.0f;  // machine s between two puffs of one excavator
        public const float kDustPuffMaxDistance = 600f;  // no puffs for excavators farther than this from the camera (or culled)

        // ---- fixes for bugs seen in game
        public const float kDeadEndParkRemoveSeconds = 15f; // a leaver standing at a chain end that is NOT a connected exit (dead end), or any
                                                         // leaver while a C4 Vacate waits for its half, is removed once culled or after this
                                                         // many machine seconds standing there, whichever is first (never the full leave cap)
        public const float kFloorErrorMax = 0.10f;       // rrw.mx.check: machine origin vs the floor under its centre (off node ramps)

        // ---- props
        public const int kPropBudgetPerSite = 250;      // was 150. Thinning order heaps -> edge cones -> divider cones
                                                         // (to kDividerFarSpacing) -> never fences, divider barriers, closed-end lines or signs;
                                                         // only the GLOBAL budget drops far projects' fences first
        public const int kPropBudgetGlobal = 3000;
        public const float kPropFarDistance = 600f;      // projects farther than this keep only barriers + edge cones
        public const float kSurveyConeSpacing = 20f;
        public const float kMinimalConeSpacing = 10f;
        public const float kSpoilHeapSpacing = 6f;
        public const float kDumpHeapSpacing = 12f;
        public const float kRubbleSpacing = 25f;
        public const float kStoneWindrowSpacing = 6f;
        public const float kWindrowFill = 170f;
        public const float kBarrierClearance = 0.6f;     // barrier depth; machine anchors keep this + 1.5 m from the barrier line
        // User request: "proper fencing", staged opening
        public const float kFenceSpacing = 4f;           // FenceIndustrialPiece* panels are ~4 m long (design brief; name unverified, fallback barriers)
        // Kerb fences stand ON the sidewalk, this far outside the carriageway edge (panel ~edge+0.08 ..
        // edge+0.43), so machines working at kCarriageClearance (0.1 m) inside the edge never clip them on 12 m roads
        // (inside the carriageway they were hit by the C3 grader and the whole C4 crew). Pedestrians lose ~0.45 m of
        // sidewalk. A side without a sidewalk band gets no kerb fence (nobody walks there).
        public const float kKerbFenceOutset = 0.25f;
        // Trench fence: HalfWidth + this (hidden phases, edges with buildings only). NEGATIVE = inside the old road footprint
        // (on the top of the trench batter, outside the flat floor the machines use): at HalfWidth + 1 the panels stood
        // inside the building lots and cut through facades with no front setback.
        public const float kFootprintFenceOffset = -0.4f;
        public const float kCentreConeSpacing = 6f;      // was 8. Divider cones (DividerStyle.Cones) at DirSplitC + kDividerInset into the works half
        public const float kTempMarkingWidth = 0.25f;    // yellow temporary line width (m) (dev selector RRWGates.TempLineWidth)
        public const float kTempMarkingEdgeInset = 0.3f; // legacy EdgeSection.TempLine(i, reversed) only; the band-kind form uses kTempLineEdgeInset
        public static readonly float3 kTempMarkingTint = new float3(1.0f, 0.80f, 0.08f); // works yellow (BE/NL/DE temporary markings)

        // ---- staged traffic management
        // Techniques not yet verified in game have a runtime dev selector in RRWGates (Gates.cs) whose initial value is the constant below.
        public const bool kC4SwapOn = true;              // C4a -> C4b side swap (false if the in-game check fails) -> RRWGates.C4Swap
        public const float kC4SwapF = 0.45f;             // C4 phase fraction of the side swap (p held there until the switch completes)
        public const float kC4aPaintF0 = 0.03f, kC4aPaintF1 = 0.40f;  // C4a painter front U*Sweep(f, F0, F1) (swap on)
        public const float kC4bPaintF0 = 0.47f, kC4bPaintF1 = 0.86f;  // C4b painter front (swap on)
        public const float kC4TeardownF = 0.90f;         // C4 teardown start (barrier pick-up; tape-pulled yellow lines when swap is off)
        public const float kC4TapeEndF = 0.98f;          // tape-pulled front reaches Trim1 (swap off)
        public const float kStageSwitchTolF = 1e-4f;     // f tolerance when p is held exactly at a switch point
        public const int kStageMinUpdates = 256;         // each group flips at most once per this many updates per edge (Director)
        public const int kDrainTimeoutFrames = (int)kClearTrafficTimeoutFrames; // SOFT drain timeout (sim frames): Ready anyway, Warn once
        public const int kDrainReadyCapFrames = 4 * kDrainTimeoutFrames; // a timed-out group stays out of WorkZonesReady
                                                         // until a clean re-scan, at most this many sim frames after it began draining
        public const int kDrainCheckUpdates = 16;        // Traffic: Moving-vehicle scan interval for SOFT / just-closed groups (2 clean scans = drained)
        public const int kZonesReportStaleUpdates = 30;  // EdgeRecord.ZonesReportUpdate older than this = no trustworthy drain report
        public const SoftMode kSoftMode = SoftMode.Sentinel;   // experimental switch -> RRWGates.Soft
        public const bool kClosedBOn = false;            // experimental: CLOSED-B on works halves next to buildings; false = sentinel (CLOSED-S) -> RRWGates.ClosedB
        public const float kMinWorksHalfWidth = 2.9f;    // a works half (drive lanes + parking of that half) narrower than this: no car half
        public const float kPedCloseF = 0.90f;           // D0: house-side sidewalks go SOFT from this fraction; p held at kD1 until drained
        public const bool kD0SidewalksOn = false;        // experimental -> RRWGates.D0Sidewalks (false = D0 closes every sidewalk). Off until
                                                         // verified in game (a failed pedestrian drain leaves people on a hidden road)
        public const float kDividerFarSpacing = 12f;     // divider cones beyond kPropFarDistance (no lamps)
        public const float kDividerInset = 0.30f;        // divider cones stand this far from DirSplitC INTO the works half
        public const float kDividerEndGap = 4f;          // first/last divider cone this far inside the closed-end lines (chain u from Trim0/Trim1)
        public const float kFenceEndGap = 1.0f;          // kerb fence runs over [Trim0 + this, Trim1 - this]
        public const float kKerbFenceInset = 0.30f;      // kerb fence on the works band's outer edge, this far INSIDE the carriageway edge
                                                         // (RRWGates.FenceLateral = Inset; Sidewalk = kKerbFenceOutset on the sidewalk)
        public const FenceLateral kFenceLateral = FenceLateral.Sidewalk; // experimental -> RRWGates.FenceLateral. The sidewalk
                                                         // position (verified in game) until the inset is verified on a Small Road C4 (inset: RMV boxes clip it on
                                                         // ~3 m works halves, and it stands in parking strips that keep parked cars)
        public const float kFenceMachineClearance = 0.15f; // Machines keep boxes this far from a standing kerb fence panel (inset 0.3 + panel
                                                         // half depth + this ~= 0.5 m inside a fenced carriageway edge)
        public const float kFenceShortRadius = 12f;      // curve radius below which only Short panels are used
        public const float kFencePitchStretchMax = 0.3f; // max stretch per joint before a Short end panel is used
        public const float kFenceStepMaxDY = 0.5f;       // |dY| along one panel above this -> two Short panels
        public const float kAccessGapWidth = 4.0f;       // vehicle gap in a works-side fence at each building access while the half is CLOSED-B
        public const float kSignInsetBehindLine = 1.0f;  // signs / amber head stand this far inside the closed-end barrier line
        public const float kSignDividerOffset = 0.6f;    // ... and this far from the divider line, in the works half
        public const bool kSignYawFlip = false;          // experimental: plate faces -Z when true -> RRWGates.SignYawFlip
        public const bool kSignalYawFlip = false;        // experimental: amber head turned 180 deg when true -> RRWGates.SignalYawFlip
        public const bool kSignsOn = true;               // experimental -> RRWGates.Signs (looks only: default on)
        public const int kKerbFenceVariant = 0;          // experimental -> RRWGates.KerbFenceVariant (index into PrefabNames.KerbFenceVariants; 0 = Low02/LowShort02, -1 = barrier line)
        public const int kTempLineSource = 1;            // experimental -> RRWGates.TempLineSource (0 = Y1 Concrete, 1 = Y2 Sand, 2 = Y3 Pavement)
        public const bool kAmberHeadOn = false;          // experimental -> RRWGates.AmberHead (off until verified in game: TrafficLightObject collision + state unverified)
        public const float kTempMarkingRoundness = 0.01f;// RenderedArea roundness of "RRW Temp Marking" (was the 0.5 default: lines ~0.53 m wide)
        public const int kTempMarkingQueueRaise = 200;   // TempMarking queue 2000 + this = 2200 (HDRP decal range <= 2500)
        public const int kTempMarkingPriority = -87;     // the planned MarkingBlackout layer takes -88 at 2100
        public const float kTempLineDividerOffset = 0.25f; // divider-side yellow line centre = DirSplitC -+ (this + w/2) into the open half
        public const float kTempLineEdgeInset = 0.15f;   // outer yellow line centre this far inside the open half's outer DRIVE lane edge
        public const float kTempDash = 3f, kTempGap = 6f;// dashed yellow lane lines between same-direction lanes (EU; NA draws none)
        public const float kCentreStripHalf = 0.30f;     // C4 half bands: the centre-line strip DirSplitC -+ this belongs to the half painted first
        public const float kHalfBandOverlap = 0.10f;     // the open half's band reaches this far over the strip edge (no seam)
        public const int kStagedLampCap = 12;            // RRW Cone Lamps per site (optional, experimental switch)
        public const int kStagedLampSites = 10;          // ... on the nearest N staged sites only
        // ---- reserved for a later version (unused in 3.0)
        public const float kNoticeHours = 2f;
        public const int kFailBudget = 10;               // failed paths per in-game hour on a mode-H project before the band degrades
        public const float kParkingClearHours = 4f;
        public const float kModeHMultPerBand = 0.175f, kModeHMultMax = 1.7f;
        public const float kTaperPerKmh = 0.5f, kTaperMin = 10f, kTaperMax = 30f, kTaperConeSpacing = 2.5f;
        public const float kBlockerSpacing = 20f, kBlockerNodeClear = 3f, kBlockerVehicleClear = 10f, kBlockerTurnClear = 30f, kBlockerRadius = 0.5f;
        public const float kBandRosterW1 = 3.2f, kBandRosterW2 = 6.6f;
        public const float kMiniExcavatorScale = 0.25f;
        public const float kBlackoutExtraWidth = 0.30f, kCentreBlackoutWidth = 0.45f;

        // ---- upgrade works (mode H: partial works when a road is replaced by another type)
        public const float kUwTol = 0.4f;                // re-marking runs narrower than this are measurement noise (lane widths +-0.2 m)
        public const float kUwLaneTol = 0.3f;            // a lane line that moved less than this is the same line
        public const float kUwShiftTol = 0.3f;           // new curve = old curve shifted sideways: every sample within this of the mean shift
        public const float kUwCalibMaxResidual = 0.3f;   // composition calibration error above this: layout unreadable (full rebuild)
        public const float kUwBandMinWidth = 1.0f;       // build / rebuild / remove runs narrower than this become re-marking (or nothing)
        public const int kUwMaxBands = 4;                // per edge (saved slots)
        public const int kUwMaxWindows = 8;              // per project (saved slots)
        public const int kUwMaxChainBands = 8;           // chain bands of one project (more: the chain is split)
        public const float kUwSetupP = 0.03f;            // setup share of p, fixed at creation (0 when created rushed)
        public const float kUwTeardownP = 0.03f;         // teardown share at the end of p
        public const float kUwKindBuild = 0.92f;         // duration weight per metre of band width, relative to a full rebuild
        public const float kUwKindBuildRemark = 0.78f;   // build band followed by a re-marking window (no finishing of its own)
        public const float kUwKindRebuild = 0.80f;
        public const float kUwKindRemove = 0.65f;        // times the demolition / construction hours ratio
        public const float kUwKindRemark = 0.14f;
        public const float kUwStagedOverhead = 0.15f;    // extra time when traffic keeps running beside the works
        public const float kUwSwitchHours = 0.25f;       // per window switch
        public const float kUwMinHours = 2.0f, kUwMinHoursRemark = 1.0f;
        public const float kUwBandMachineClear = 0.25f;  // machines keep this far from an open lane edge
        public const float kUwFenceClear = 0.30f;        // ... and this far from a fence side
        public const float kUwDropInset = 0.2f;          // a car lane is dropped when its centre lies inside [Lo + inset, Hi - inset] of a band
        public const bool kUwShuttleHeadsOn = true;      // portable signal heads at the ends of a one-lane section (TrafficLightCar01,
                                                         // owned by the works edge: unowned, its meshes get no draw layer)
        public const float kUwLaneTaperFrom = 4f;        // temporary lanes: metres from each edge end with no shift ...
        public const float kUwLaneTaperTo = 24f;         // ... and with the full shift (the yellow lines stop here)
        public const float kUwShuttleMaxLength = 300f;   // one-lane alternating operation only over sections up to this long
        public const float kUwShuttleGreenSec = 25f;     // green per direction (game seconds at normal speed)
        public const float kUwShuttleClearMinSec = 4f;   // all-red at least this long ...
        public const float kUwShuttleClearMaxSec = 60f;  // ... and until the section is empty, at most this long
        public const float kUwBlockerNodeSetback = 30f;  // lane blockers and bands stop this far before each node
        public const float kUwDrivewayKeepOut = 3f;      // +- along the road around a building access: no standing machine
        public const float kUwSlewLimitDeg = 15f;        // excavator slew next to traffic (front dump only)
        public const int kUwTooltipMaxEdges = 64;
        // classifier details
        public const float kUwMergeGap = 0.5f;           // runs / entries of the same kind closer than this merge
        public const float kUwBoundaryTol = 0.15f;       // half width of the re-marking strip around a lane line that appears / disappears
        public const float kUwStripGap = 0.25f;          // gaps between same-kind same-direction strips smaller than this are closed
        public const float kUwMiddleTol = 0.25f;         // a run within the median zone +- this is a Middle band
        public const float kUwEndTol = 1.0f;             // new curve end points may lie this far along the old arc from the old ends
        public const float kUwLengthTol = 0.02f;         // relative length change allowed beyond the offset-curve length
        public const float kUwUnionGap = 0.5f;           // edge bands of one chain cluster into one chain band within this
        public const float kUwMinWindowWeight = 0.05f;   // smallest window weight (m x factor) so a window never has zero length
        // crews of upgrade works (machine-safe width of a band decides the roster)
        public const float kUwExcavatorMinWidth = 6.6f;  // full excavator with a truck beside it
        public const float kUwMiniExcavatorMinWidth = 3.9f; // mini excavator with the truck in line behind it (below: truck + crew truck)
        public const float kUwLoaderMinWidth = 3.2f;     // loader / grader spreads the base course
        public const float kUwRollerMinWidth = 2.6f;     // rollers compact the base and the fresh asphalt
        public const float kUwFrontLoadAhead = 11f;      // mode H front loading: truck centre this far ahead of the excavator pivot (the
                                                         // tail beyond the scrape; Machines refines it from the arm reach and box lengths)
        public const float kUwFrontLoadAheadSmall = 6f;  // same for the mini excavator
        public const float kUwMachineLatInset = 1.6f;    // a machine centre stays this far inside the machine-safe range (half a box + clearance)
        public const float kUwDividerSpacing = 6f;       // divider cones along the inner edge of a band's closed part
        public const float kUwVergeFenceGap = 4f;        // gap in the fence on the new road edge at each building access
        // traffic-primitive switches for upgrade works (RRWGates holds the runtime copies). Lane drops with blockers and the
        // apply-frame covers are on; the soft visual drop and the footprint hold stay off. Next to buildings a lane drop is never
        // used (cars reach the dropped lane from parking and driveways): PhasePlan.WindowPrimitive enforces that.
        public const bool kUwDropOn = true;              // lane drops with Traffic-owned blockers (only on edges without buildings)
        public const bool kUwVisualDropOn = false;       // soft visual drop (Forbidden + caution) on band lanes: more cars used them, off
        public const bool kUwPreCoverOn = true;          // bands covered from the apply frame
        public const bool kUwNewParkingOffOn = true;     // EMPTY new parking lanes inside build bands disabled until their band opens
        public const bool kUwFootprintHoldOn = false;    // old footprint held flat after a narrowing
        public const bool kUwOldAsphaltOn = false;       // "RRW Old Asphalt" on the removed strip (else Base Course Cover until it is broken up)

        // ---- effect clones (verified in game)
        public const float kBeaconLux = 0.5f;            // RRW Amber Light intensity (vanilla CarAmberWarningSource is 10 lux: far too bright on a prop)
        public static readonly float3 kBeaconColor = new float3(1f, 0.5f, 0.05f);
        public const float kBeaconPeriodSeconds = 1f;    // square blink, 1 s period
        public const float kBeaconDuty = 0.5f;
        public const uint kBeaconPeriodFrames = 60;      // phase = PseudoRandomSeed(seed).GetRandom(kLightState).NextUInt(60) (EffectTransformSystem)
        public const int kBeaconChaseStepFrames = 6;     // "chase" along a closed-end barrier line
        public const float kBeaconHeight = 1.0f;         // light offset above the barrier pivot
        public const float kDustPulseSeconds = 7f;       // RRW Dust VFX emitter pulse (small realistic puff, verified in game)
        public const int kVfxMinMaxCount = 128;          // VFX clone instance cap
        public const int kHeapDustEvery = 4;             // every n-th spoil heap / windrow / dump heap slot is a dust-emitting heap (0 = none)

        // ---- performance / housekeeping
        public const int kSelfHealInterval = 16;         // updates between self-heal passes (props, areas, machines)
        public const int kWearCheckInterval = 16;        // Director compares wear with LaneCondition and rewrites differences
        public const int kDerivedGcInterval = 64;        // updates between orphan-derived sweeps (Director)
        public const int kDerivedGcGracePasses = 2;      // an orphan is deleted only on the 2nd consecutive sweep
        public const int kOrphanCloneMaxUpdates = 60;    // rrw.check: no live non-site edge references a clone longer than this
    }
}
