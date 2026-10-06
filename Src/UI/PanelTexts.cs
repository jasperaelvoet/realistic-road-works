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
        public readonly string[] Steps = new string[5];
        public int StepCount, StepIndex;

        public void Build(WorksPanelState st, RRWSetting s)
        {
            Title = RRWText.Get(RRWText.Title);
            KindText = RRWText.Get(st.Kind == WorksKind.Construction ? RRWText.KindConstruction : RRWText.KindDemolition);

            // stepper: the kind's phases (5 construction, 3 demolition); StepIndex == StepCount when complete
            WorksPhase firstPhase = PhasePlan.FirstPhase(st.Kind);
            StepCount = math.min(Steps.Length, PhasePlan.StepCount(st.Kind));
            for (int i = 0; i < StepCount; i++) Steps[i] = UiFormat.PhaseName((WorksPhase)((int)firstPhase + i));
            StepIndex = st.IsComplete ? StepCount : math.clamp(PhasePlan.StepIndex(st.Phase), 0, StepCount);

            PhaseText = st.IsComplete
                ? UiFormat.Get(UiFormat.KCompleteText)
                : RRWText.Format(RRWText.Phase, UiFormat.PhaseName(st.Phase), UiFormat.PhasePercent(st.F));

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

            // A line under the phase bar when the road is split into several crew sections.
            bool crews = st.ShowCrews;
            CrewsText = crews && st.Eta == EtaState.Working ? RRWText.Format(RRWText.CrewsWorking, st.Crews) : "";
            CrewsDetail = crews ? RRWText.Format(RRWText.CrewSection, st.Crews, (int)math.round(st.SectionLength)) : "";

            // money: construction only (demolition is free)
            if (st.Kind == WorksKind.Construction && !st.IsComplete)
            {
                PaidText = RRWText.Format(RRWText.Paid, UiFormat.Money(st.Paid));
                RefundText = RRWText.Format(RRWText.Refund, UiFormat.Money(st.Refund));
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
    }
}
