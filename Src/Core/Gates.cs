namespace RealisticRoadWorks.V3
{
    // Runtime selectors (experimental switches) for every technique of the staged traffic management that is not verified
    // in game yet, so each option can be tried and compared on the PRODUCT build.
    //
    // Contract:
    //  * Runtime only, never saved, process-wide (not reset on load). Initial values = the RRWConst defaults: the safe /
    //    fallback option wherever gameplay could break (CLOSED-S, sentinel SOFT, no amber head, no D0 house-side sidewalks), the
    //    nicer visual option where only looks are at stake (fence panel Low02, signs on, cones divider); the kerb fence stays on
    //    the sidewalk (verified in game against machine clipping) until the inset variant is verified.
    //  * SINGLE WRITER: the dev command `rrw.gate <name> <value>` (Src/Dev, DEVTOOLS builds) through Set*(), which bumps
    //    Revision. Release builds never change them (the defaults ARE the shipped behaviour; to ship a verified option,
    //    change its RRWConst default).
    //  * Readers: every module, every update (cheap static reads). A module that lays out / registers something from a switch
    //    compares Revision with its own copy and re-lays out (Props respawn in one frame, Surfaces rewrite,
    //    Traffic re-applies with its refresh rules). A change never makes a module throw.
    public static class RRWGates
    {
        public static int Revision;                       // ++ on every change (modules re-layout when it differs from their copy)

        // ---- traffic
        public static bool ClosedB = RRWConst.kClosedBOn;      // CLOSED-B (blockage after drain) instead of CLOSED-S on car groups of
                                                               // visible edges with buildings (Traffic). false = sentinel (the original approach)
        public static SoftMode Soft = RRWConst.kSoftMode;      // how Traffic drains a closing group (the release keeps Sentinel)
        public static bool C4Swap = RRWConst.kC4SwapOn;        // C4a -> C4b side swap (Director: StageContext.SwapOn)
        public static bool D0Sidewalks = RRWConst.kD0SidewalksOn; // demolition D0 house-side sidewalks open until kPedCloseF, then drained
        public static bool DeadEndRule = true;                 // A/B switch: false reproduces the old trap (car half opens on a dead end).
                                                               // Dev only; the other CarHalfAllowed rules have no switch (they only restrict)

        // ---- props
        public static int KerbFenceVariant = RRWConst.kKerbFenceVariant;                // index into PrefabNames.KerbFenceVariants (0 = Low02 + LowShort02);
                                                               // -1 = SafetyBarrier line fallback (the original Props fallback)
        public static FenceLateral FenceLateral = RRWConst.kFenceLateral; // inset (0.3 m inside the carriageway) vs sidewalk outset
                                                               // (default Sidewalk, verified in game; inset once verified)
        public static DividerStyle Divider = DividerStyle.Cones; // divider between open and works half (Barriers = a later look, dev only)
        public static bool Signs = RRWConst.kSignsOn;          // one-way / no-entry / speed plates at the closed ends
        public static bool SignYawFlip = RRWConst.kSignYawFlip;   // plate faces -Z
        public static bool AmberHead = RRWConst.kAmberHeadOn;  // flashing amber TrafficLightCar01 at the open-direction entry
        public static bool SignalYawFlip = RRWConst.kSignalYawFlip; // amber head turned 180 deg
        public static bool ConeLamps = false;                  // optional: every 4th divider cone an "RRW Cone Lamp"

        // ---- machines
        public static ArrowBoardMode ArrowBoard = ArrowBoardMode.Auto; // painter arrow board (SignalAnimation1/2)

        // ---- surfaces (yellow temporary lines)
        public static int TempLineSource = RRWConst.kTempLineSource;                  // 0 = Y1 Concrete (tint kTempMarkingTint), 1 = Y2 Sand (1.15,.95,.20),
                                                               // 2 = Y3 Pavement (1.10,.85,.12); Surfaces registers all three clones
        public static float TempLineWidth = RRWConst.kTempMarkingWidth;
        public static float TempLineRoundness = RRWConst.kTempMarkingRoundness;
        public static int TempLineQueueRaise = RRWConst.kTempMarkingQueueRaise;
        public static bool TempLineLodBias = false;            // m_LodBias = +1 on the clone when dashes vanish at 300 m
        public static bool HalfCovers = true;                  // C4 per-half Fresh Asphalt (Cover) bands (false = one full-width cover)

        // ---- upgrade works (mode H). Read at project creation (SiteFactory: AllAtOnce, PreCover) and every update (Director:
        //      PhasePlan.WindowPrimitive; Traffic: blockers, parking; Surfaces: layers). A switch turned off later only weakens what
        //      a running project does (the saved primitive is a ceiling), it never strengthens it.
        public static bool UpgradeDrop = RRWConst.kUwDropOn;               // lane drops with Traffic-owned blockers; false = no lane drop
                                                                           // at all. Never next to buildings (PhasePlan.WindowPrimitive)
        public static bool UpgradeVisualDrop = RRWConst.kUwVisualDropOn;   // soft visual drop on band lanes (never machine-safe)
        public static bool UpgradePreCover = RRWConst.kUwPreCoverOn;       // bands covered from the apply frame
        public static bool UpgradeNewParkingOff = RRWConst.kUwNewParkingOffOn; // empty new parking lanes of build bands disabled until opened
        public static bool UpgradeFootprintHold = RRWConst.kUwFootprintHoldOn; // old footprint held flat after a narrowing (Ground)
        public static bool UpgradeOldAsphalt = RRWConst.kUwOldAsphaltOn;
        public static bool UpgradeDefer = RRWConst.kUwDeferOn;   // upgrade works keep the old road (and junctions) until their finishing   // removed strip shows "RRW Old Asphalt" until it is broken up

        public static void Changed() => Revision++;

        // Names accepted by `rrw.gate` (Src/Dev parses them).
        public static readonly string[] Names =
        {
            "closedb", "soft", "c4swap", "d0sidewalks", "deadend",
            "fence", "fencelat", "divider", "signs", "signflip", "amber", "signalflip", "conelamps",
            "arrow", "lineSrc", "lineW", "lineRound", "lineQueue", "lineLod", "halfcovers",
            "uwdrop", "uwvdrop", "uwprecover", "uwparkoff", "uwhold", "uwoldasph",
        };

        // The upgrade-works switches by name (lower case): true + what = "name=value" when the name is one of them and the value
        // parses (1/0, on/off, true/false); false otherwise (error says why when the name is known). Bumps Revision. Src/Dev's
        // `rrw.gate` hands every name it does not know itself to this.
        public static bool TrySetUpgrade(string name, string value, out string what, out string error)
        {
            what = error = null;
            string n = (name ?? "").ToLowerInvariant();
            if (n != "uwdrop" && n != "uwvdrop" && n != "uwprecover" && n != "uwparkoff" && n != "uwhold" && n != "uwoldasph") return false;
            string v = (value ?? "").ToLowerInvariant();
            bool b;
            if (v == "1" || v == "on" || v == "true") b = true;
            else if (v == "0" || v == "off" || v == "false") b = false;
            else { error = n + " expects 1/0"; return false; }
            switch (n)
            {
                case "uwdrop": UpgradeDrop = b; break;
                case "uwvdrop": UpgradeVisualDrop = b; break;
                case "uwprecover": UpgradePreCover = b; break;
                case "uwparkoff": UpgradeNewParkingOff = b; break;
                case "uwhold": UpgradeFootprintHold = b; break;
                default: UpgradeOldAsphalt = b; break;
            }
            Changed();
            what = n + "=" + b;
            return true;
        }

        public static string Describe()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "rev=" + Revision
                + " closedb=" + ClosedB + " soft=" + Soft + " c4swap=" + C4Swap + " d0sidewalks=" + D0Sidewalks + " deadend=" + DeadEndRule
                + " | fence=" + KerbFenceVariant + " fencelat=" + FenceLateral + " divider=" + Divider + " signs=" + Signs
                + " signflip=" + SignYawFlip + " amber=" + AmberHead + " signalflip=" + SignalYawFlip + " conelamps=" + ConeLamps
                + " | arrow=" + ArrowBoard
                + " | lineSrc=" + TempLineSource + " lineW=" + TempLineWidth.ToString("0.###", ic) + " lineRound=" + TempLineRoundness.ToString("0.###", ic)
                + " lineQueue=" + TempLineQueueRaise + " lineLod=" + TempLineLodBias + " halfcovers=" + HalfCovers
                + " | uwdrop=" + UpgradeDrop + " uwvdrop=" + UpgradeVisualDrop + " uwprecover=" + UpgradePreCover
                + " uwparkoff=" + UpgradeNewParkingOff + " uwhold=" + UpgradeFootprintHold + " uwoldasph=" + UpgradeOldAsphalt;
        }
    }

    // Where the kerb fence beside an open sidewalk stands (experimental switch).
    public enum FenceLateral : byte
    {
        Inset = 0,      // on the works band's outer edge, RRWConst.kKerbFenceInset inside the carriageway edge (sidewalk keeps its width;
                        // Machines keep kFenceMachineClearance from it)
        Sidewalk = 1,   // default: on the sidewalk, RRWConst.kKerbFenceOutset outside the carriageway edge
    }

    // Painter arrow board (experimental switch).
    public enum ArrowBoardMode : byte
    {
        Auto = 0,       // SignalAnimation1 when the works half is on the painter's right (traffic passes left), else SignalAnimation2;
                        // geometric, NOT mirrored for left-hand traffic (game code: GetWorkingFlags maps RightLimit/LeftLimit the
                        // same way in LHT); still to be confirmed in game, Flip is the fallback
        Flip = 1,       // the opposite of Auto (if the game shows the animations the other way round)
        Off = 2,        // no arrow (beacons only)
        Both = 3,       // dev: SignalAnimation1 | 2
    }

    // City-wide facts the modules need in pure functions. SINGLE WRITER: the Director (every update, from
    // CityConfigurationSystem.leftHandTraffic / defaultTheme). Readers: all. Known = false until the first write after a load.
    public static class RRWCity
    {
        public static bool Known;
        public static bool LeftHandTraffic;  // CityConfigurationSystem.leftHandTraffic
        public static bool NaTheme;          // the city's default theme is North American (else EU): sign / signal variants, NA yellow lines
    }
}
