using Unity.Mathematics;

namespace RealisticRoadWorks.V3.UI
{
    // Ready-made display strings of the info section (property table and layout). Built from a computed
    // WorksPanelState when the panel refreshes (a few times per second at most), never per frame.
    public sealed class PanelTexts
    {
        public string Title, KindText, PhaseText, EtaText, TrafficText, PaidText, RefundText;
        public string RushText, CantAffordText, CancelText, ConfirmText, FocusText, SegmentsText;
        public string CrewsText;     // "3 crews working" (only when the project has more than one crew and they work), else ""
        public string CrewsDetail;   // "3 crews · about 330 m each" (dev dump; "" with one crew)
        // Stepper: the kind's phases, or the windows of upgrade works ("Right side", "Markings"); StepIndex == StepCount when complete.
        public readonly string[] Steps = new string[math.max(5, RRWConst.kUwMaxWindows)];
        public int StepCount, StepIndex;
        public float StepFraction;   // fill of the connector after the current dot (phase fraction, or the window fraction)
        // Upgrade works: one line per band of the current window ("Left side · Paving · 47%"), and the note under the traffic row
        // while the window had to fall back to a slow zone ("Lanes cannot be closed here · slow zone, no machines in the lanes");
        // else empty.
        public readonly string[] BandLines = new string[RRWConst.kUwMaxChainBands];
        public int BandLineCount;
        public string TrafficNote;

        public void Build(WorksPanelState st, RRWSetting s)
        {
            Title = RRWText.Get(RRWText.Title);
            KindText = RRWText.Get(st.Kind == WorksKind.Construction ? RRWText.KindConstruction : RRWText.KindDemolition);
            BandLineCount = 0;
            TrafficNote = "";

            if (st.IsUpgrade) BuildUpgrade(st);
            else
            {
                // stepper: the kind's phases (5 construction, 3 demolition); StepIndex == StepCount when complete
                WorksPhase firstPhase = PhasePlan.FirstPhase(st.Kind);
                StepCount = math.min(Steps.Length, PhasePlan.StepCount(st.Kind));
                for (int i = 0; i < StepCount; i++) Steps[i] = UiFormat.PhaseName((WorksPhase)((int)firstPhase + i));
                StepIndex = st.IsComplete ? StepCount : math.clamp(PhasePlan.StepIndex(st.Phase), 0, StepCount);
                StepFraction = st.IsComplete ? 1f : math.saturate(st.F);

                PhaseText = st.IsComplete
                    ? UiFormat.Get(UiFormat.KCompleteText)
                    : RRWText.Format(RRWText.Phase, UiFormat.PhaseName(st.Phase), UiFormat.PhasePercent(st.F));
            }

            switch (st.Eta)
            {
                case EtaState.Finishing: EtaText = RRWText.Get(RRWText.Finishing); break;
                case EtaState.Clearing: EtaText = RRWText.Get(RRWText.ClearingTraffic); break;
                case EtaState.CrewsOff: EtaText = RRWText.Format(RRWText.CrewsOff, UiFormat.Clock(st.ShiftStart)); break;
                default:
                    EtaText = RRWText.Format(RRWText.EtaWork, UiFormat.Duration(st.HoursLeft), UiFormat.Finish(st.NormalizedTime, st.CalendarFrames));
                    break;
            }

            TrafficText = Traffic(st, s);
            if (UpgradeSlowZone(st, s)) TrafficNote = RRWText.Get(RRWText.UpgradeTrafficNoLaneClosure);

            // A line under the phase bar when the road is split into several crew sections.
            bool crews = st.ShowCrews;
            CrewsText = crews && st.Eta == EtaState.Working ? RRWText.Format(RRWText.CrewsWorking, st.Crews) : "";
            CrewsDetail = crews ? RRWText.Format(RRWText.CrewSection, st.Crews, (int)math.round(st.SectionLength)) : "";

            // money: construction only (demolition is free); upgrade works are never cancelled, so no cancel refund
            if (st.Kind == WorksKind.Construction && !st.IsComplete)
            {
                PaidText = RRWText.Format(RRWText.Paid, UiFormat.Money(st.Paid));
                RefundText = st.ModeH ? "" : RRWText.Format(RRWText.Refund, UiFormat.Money(st.Refund));
            }
            else PaidText = RefundText = "";

            RushText = st.Rushed ? RRWText.Get(RRWText.Rushed) : RRWText.Format(RRWText.Rush, UiFormat.Money(st.RushCost));
            CantAffordText = RRWText.Get(RRWText.CantAfford);
            if (st.Kind == WorksKind.Construction) CancelText = RRWText.Format(RRWText.CancelBuild, UiFormat.Money(st.Refund));
            else CancelText = st.CanCancel ? RRWText.Get(RRWText.CancelDemolition) : RRWText.Get(RRWText.CannotCancel);
            ConfirmText = UiFormat.Get(UiFormat.KConfirmCancel);
            FocusText = RRWText.Get(RRWText.Focus);
            SegmentsText = st.SelectedTotal > st.SelectedInProject
                ? RRWText.Format(RRWText.Segments, st.SelectedInProject, st.SelectedTotal)
                : "";
        }

        // Upgrade works: kind by class, the windows as steps (named by their bands), the bar text "Step 2 of 3" (setup / teardown
        // have their own text) and one line per band of the current window with its equivalent phase.
        private void BuildUpgrade(WorksPanelState st)
        {
            var v = st.View;
            var u = v.Upgrade;
            string kindKey = RRWText.UpgradeKindKey(st.UpClass);
            if (kindKey != null) KindText = RRWText.Get(kindKey);

            int n = math.clamp((int)u.WindowCount, 1, Steps.Length);
            StepCount = n;
            for (int w = 0; w < n; w++) Steps[w] = WindowLabel(u, w);
            if (st.IsComplete || u.InTeardown) { StepIndex = n; StepFraction = 1f; }
            else if (u.InSetup) { StepIndex = 0; StepFraction = 0f; }
            else { StepIndex = math.clamp((int)u.Window, 0, n - 1); StepFraction = math.saturate(u.Gw); }

            if (st.IsComplete) PhaseText = UiFormat.Get(UiFormat.KCompleteText);
            else if (u.InSetup) PhaseText = RRWText.Get(RRWText.UpgradeSetup);
            else if (u.InTeardown) PhaseText = RRWText.Get(RRWText.UpgradeTeardown);
            else PhaseText = RRWText.Format(RRWText.UpgradeWindowLine, StepIndex + 1, n);

            if (st.IsComplete || u.InSetup || u.InTeardown) return;
            for (int i = 0; i < u.BandCount && BandLineCount < BandLines.Length; i++)
            {
                if (u.Band(i).Window != u.Window) continue;
                BandLines[BandLineCount++] = BandLine(v, i);
            }
        }

        // Step label of window w: the names of its bands ("Left side", "Left side + Right side", "Markings"); "Step 1 of 2" when
        // the window has no band (devices only).
        public static string WindowLabel(in UpgradeView u, int w)
        {
            string label = null;
            for (int i = 0; i < u.BandCount; i++)
            {
                var b = u.Band(i);
                if (b.Window != w) continue;
                string name = RRWText.Get(RRWText.UpgradeBandKey(b.Kind, b.Side));
                if (label == null) label = name;
                else if (!ContainsPart(label, name)) label += " + " + name;
            }
            return label ?? RRWText.Format(RRWText.UpgradeWindowLine, w + 1, math.max(1, (int)u.WindowCount));
        }

        private static bool ContainsPart(string label, string name)
        {
            foreach (var part in label.Split(new[] { " + " }, System.StringSplitOptions.None))
                if (part == name) return true;
            return false;
        }

        // "Left side · Paving · 47%" (the band's equivalent phase); the re-marking band: "Markings · 47%".
        public static string BandLine(in ProjectView v, int band)
        {
            var b = v.Upgrade.Band(band);
            string name = RRWText.Get(RRWText.UpgradeBandKey(b.Kind, b.Side));
            if (!PhasePlan.UpgradeBandPhase(v, band, out WorksPhase ph, out float f, out float g)) return name;
            if (b.Kind == BandKind.Remark) return RRWText.Format(RRWText.Phase, name, UiFormat.PhasePercent(g));
            return RRWText.Format(RRWText.UpgradeBandPhase, name, UiFormat.PhaseName(ph), UiFormat.PhasePercent(f));
        }

        // The note under the traffic row: the applied window of upgrade works had to fall back to a slow zone with cones only
        // (no lane closed, no machine in a lane) although the Realistic policy would close lanes. Not under the Always slow zone or
        // Visual only setting (the player chose that), not while the window still waits for the data its decision needs (the
        // decision may close lanes moments later), not for a window not decided yet, and not during a window switch.
        public static bool UpgradeSlowZone(WorksPanelState st, RRWSetting s)
        {
            if (!st.IsUpgrade || st.IsComplete || st.Releasing) return false;
            if (s != null && s.Policy != ClosurePolicy.Realistic) return false;
            if (UpgradeSwitching(st)) return false;
            var u = st.View.Upgrade;
            return u.AppliedTraffic == BandTraffic.Dressing && u.Reason != StageBlockReason.Waiting;
        }

        // A window switch runs: the previous window's layout is still applied while its machines leave (Vacate), or the lane
        // groups are being swapped and drained.
        private static bool UpgradeSwitching(WorksPanelState st) =>
            st.Switch == StageSwitch.Vacate || st.Switch == StageSwitch.Swap || st.Switch == StageSwitch.Drain || st.View.Upgrade.Vacating;

        // Dressing, or a window whose primitive is not decided yet (treated like Dressing: nothing closes).
        private static bool IsSlowZonePrimitive(BandTraffic t) => t == BandTraffic.Dressing || t == BandTraffic.Undecided;

        // Traffic row: what the road carries now, from the APPLIED state (Traffic's ClosureApplied,
        // OpenLanesApplied, SoftApplied, ClosedBApplied), so the text never promises a lane before vehicles can use it.
        //   released, still Closed                          -> "Works complete · opening once the machines have left"
        //   Closed + C4 switch Swap/Drain or a car half SOFT  -> "Switching sides: waiting for vehicles to clear"
        //   Closed + a car half applied open                 -> "One direction open at {km/h} km/h, other direction detours"
        //   Closed + sidewalks open, staged C4, no car half  -> "Closed to vehicles – sidewalks open (<reason>)"
        //   Closed + only the sidewalks open                 -> "Closed to vehicles – sidewalks open"
        //   otherwise                                        -> plain Closed / Slow zone / Open
        // Waiting buildings: "reachable on foot, waiting for vehicle access" when the sidewalks are open; nothing when every car
        // half of every Closed edge is applied open or CLOSED-B (houses on the works half are reached from the open lane).
        public static string Traffic(WorksPanelState st, RRWSetting s)
        {
            string traffic = TrafficState(st, s);
            if (st.Rerouted > 0 || st.ParkedMoved > 0) traffic += " · " + RRWText.Format(RRWText.Rerouted, st.Rerouted, st.ParkedMoved);
            if (st.BuildingsWaiting > 0 && !st.CarAccessKept)
                traffic += " · " + (st.SidewalksOpen
                    ? RRWText.Format(RRWText.WaitingVehicles, st.BuildingsWaiting)
                    : RRWText.Format(RRWText.WaitingAccess, st.BuildingsWaiting));
            return traffic;
        }

        // The traffic state phrase alone (no reroute / waiting suffix). Public for the dev dump.
        public static string TrafficState(WorksPanelState st, RRWSetting s)
        {
            int kmh = s != null ? s.WorkZoneSpeedKmh : 30;
            if (st.IsUpgrade && !st.IsComplete && !st.Releasing) return UpgradeTrafficState(st, s, kmh);
            if (st.Closure == ClosureLevel.Closed)
            {
                if (st.Releasing) return RRWText.Get(RRWText.OpeningAfterMachines);
                if (st.SwitchingSides) return RRWText.Get(RRWText.TrafficSwitchingSides);
                if (st.CarHalfOpen && st.CrewChangingSides) return RRWText.Format(RRWText.TrafficCrewChangingSides, kmh);
                if (st.CarHalfOpen) return RRWText.Format(RRWText.TrafficOneDirection, kmh);
                if (st.SidewalksOpen)
                {
                    if (st.StagedC4 && !st.CarHalfAllowed && st.StageReason != StageBlockReason.None)
                        return RRWText.Format(RRWText.TrafficPedestriansReason, RRWText.Get(PhasePlan.StageReasonKey(st.StageReason)));
                    return RRWText.Get(RRWText.TrafficPedestrians);
                }
                return RRWText.Get(RRWText.TrafficClosed);
            }
            if (st.Closure == ClosureLevel.SlowZone) return RRWText.Format(RRWText.TrafficSlow, kmh);
            return RRWText.Get(RRWText.TrafficOpen);
        }

        // Upgrade works: the primitive of the window whose layout is applied now (the Director's decision; the pip shows the
        // applied closure).
        //   policy visual only                     -> "Open to traffic"
        //   a window switch runs (Vacate .. Drain)  -> "Changing work area: waiting for vehicles to clear"
        //   lane drop                              -> "Outer lanes closed · traffic keeps flowing at {km/h} km/h"
        //   slow zone (no lane can be closed)      -> "Slow zone · <reason>" ("Slow zone · waiting for traffic data" while the
        //                                             decision waits for its inputs), or "Slow zone {km/h} km/h – access kept"
        //                                             without a reason
        //   otherwise                              -> the primitive's line (one direction closed, road closed, sidewalk closed,
        //                                             traffic not affected)
        private static string UpgradeTrafficState(WorksPanelState st, RRWSetting s, int kmh)
        {
            if (s != null && s.Policy == ClosurePolicy.VisualOnly) return RRWText.Get(RRWText.TrafficOpen);
            if (UpgradeSwitching(st)) return RRWText.Get(RRWText.UpgradeTrafficSwitching);
            var u = st.View.Upgrade;
            var t = u.AppliedTraffic;
            if (t == BandTraffic.Drop) return RRWText.Format(RRWText.UpgradeTrafficLaneDrop, kmh);
            if (IsSlowZonePrimitive(t))
                return u.Reason != StageBlockReason.None
                    ? RRWText.Format(RRWText.UpgradeTrafficDressing, RRWText.Get(PhasePlan.StageReasonKey(u.Reason)))
                    : RRWText.Format(RRWText.TrafficSlow, kmh);
            return RRWText.Get(RRWText.UpgradeTrafficKey(t));
        }
    }
}
