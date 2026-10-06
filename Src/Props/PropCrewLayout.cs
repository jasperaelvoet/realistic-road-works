using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Props
{
    // Pure, allocation-free helpers for the crew sections. Public so the pure tests
    // can check them against Core's PhasePlan without the game.
    //
    // Crew depots. Crew 0 keeps the single-crew props (PropGroup.CrewProps at the chain start). Every other crew i of the
    // latched layout (ProjectRecord.Crews = n) gets the same set (3 truck-load props + 2 tall cones) at its section start
    // B_i = PhasePlan.SectionStart(i, n, U), at the same offsets from B_i as crew 0's set from Trim0.
    // Stable slots: a depot is keyed by the REDUCED fraction i/n of its boundary, not by (i, n). A boundary that both
    // layouts share (U/2 for 2 and 4 crews, U/3 for 3 and 6 crews) therefore keeps its entities when the Director re-latches
    // the crew count; only boundaries that exist in one layout alone appear / disappear, in the frame the latch changes (the
    // phase hand-over, where Machines relays / spawns the crews too). Interior boundaries are U*i/n on the 2 m grid, so the
    // same reduced fraction always gives the same chain u.
    // Teardown: from the last phase's barrier pick-up (StartBarrierKeep < 1, f > .90) every crew but crew 0 has driven
    // out, so its depot is gone; crew 0's set follows the start barriers as with a single crew.
    // Upgrade works (mode H): the crews are the bands of one window and work side by side over the same chain, not in sections,
    // so there are no depots; crew 0's set stands on a free strip of the lead band (PropUpgrade.cs).
    public static class PropCrewLayout
    {
        // reduced fractions a/b, 0 < a < b <= kMaxCrewsPerProject (6), in a fixed order: depot id = index
        private static readonly int[] s_Num = { 1, 1, 2, 1, 3, 1, 2, 3, 4, 1, 5 };
        private static readonly int[] s_Den = { 2, 3, 3, 4, 4, 5, 5, 5, 5, 6, 6 };
        public const int kMaxDepots = 11;
        public const int kDepotSlots = 3;           // = PhasePlan.SlotCount(CrewProps) when the chain is long enough
        public const float kDepotEndClear = 2f;     // the set (cones at +-1.5 m) keeps this far inside its section / the trims

        static int Gcd(int a, int b)
        {
            while (b != 0) { int t = a % b; a = b; b = t; }
            return a;
        }

        // Stable depot id of interior boundary i of an n-crew layout (-1 = none: i <= 0, i >= n, n out of range).
        public static int DepotId(int i, int n)
        {
            if (n <= 1 || n > RRWConst.kMaxCrewsPerProject || i <= 0 || i >= n) return -1;
            int g = Gcd(i, n);
            int a = i / g, b = n / g;
            for (int d = 0; d < kMaxDepots; d++) if (s_Num[d] == a && s_Den[d] == b) return d;
            return -1;
        }

        // Boundary index of depot `id` in an n-crew layout (-1 when the layout has no such boundary).
        public static int BoundaryOf(int id, int n)
        {
            if ((uint)id >= kMaxDepots || n <= 1) return -1;
            int b = s_Den[id];
            if (n % b != 0) return -1;
            return s_Num[id] * (n / b);
        }

        public static string DepotName(int id) => (uint)id < kMaxDepots ? s_Num[id] + "/" + s_Den[id] : "-";

        // Chain u of slot k (0..2) of the depot at boundary i: B_i + (crew 0's offset of slot k from Trim0).
        public static float DepotU(in ProjectView v, int i, int k)
        {
            int n = v.CrewCount;
            float b = PhasePlan.SectionStart(i, n, v.U);
            return b + (PhasePlan.SlotU(PropGroup.CrewProps, k, v) - v.Trim0);
        }

        // The whole set (cones 1.5 m beyond the outer slots) fits inside the crew's section and the trims.
        public static bool DepotFits(in ProjectView v, int i)
        {
            int n = v.CrewCount;
            if (n <= 1 || i <= 0 || i >= n) return false;
            float lo = DepotU(v, i, 0) - 1.5f, hi = DepotU(v, i, kDepotSlots - 1) + 1.5f;
            float secHi = PhasePlan.SectionStart(i + 1, n, v.U);
            return lo >= v.Trim0 + kDepotEndClear && hi <= math.min(v.Trim1, secHi) - kDepotEndClear;
        }

        // Fill of slot k of the depot at boundary i (-1 = no entity, 255 = standing). startKeep = PhasePlan.Props(v).StartBarrierKeep
        // (passed in so a diff evaluates the plan once).
        public static int DepotFill(in ProjectView v, int i, int k, float startKeep)
        {
            int n = v.CrewCount;
            if (v.IsUpgrade) return -1;   // band crews share the chain: no section depots
            if (n <= 1 || i <= 0 || i >= n || k < 0 || k >= kDepotSlots) return -1;
            if (v.Phase == WorksPhase.Complete || v.Phase == WorksPhase.None) return -1;
            if (PhasePlan.SlotCount(PropGroup.CrewProps, v) < kDepotSlots) return -1;
            if (startKeep < 1f) return -1;   // the other crews drove out at the start of the barrier pick-up
            if (!DepotFits(v, i)) return -1;
            return 255;
        }

        // Upper bound of depot props a view can show (global prop budget estimate): 5 per interior boundary.
        public static int DepotEstimate(in ProjectView v) => !v.IsUpgrade && v.CrewCount > 1 ? 5 * (v.CrewCount - 1) : 0;

        // Can group g give ANY slot a fill >= 1 in this view? Mirrors the guards at the head of each PhasePlan.PropFill case.
        // Used ONLY to skip whole groups in front-only diffs (PropSystem.FrontDiff); every full diff verifies it against the
        // real fills and switches it off for the session on the first contradiction (log "props: group predicate ...").
        public static bool GroupMayFill(PropGroup g, in ProjectView v)
        {
            var ph = v.Phase;
            if (ph == WorksPhase.Complete) return false;
            // upgrade works: project-level edge cones (Dressing) and crew props only; band heaps are not slot groups
            if (v.IsUpgrade) return g == PropGroup.EdgeCones || g == PropGroup.CrewProps;
            bool A = v.Mode == VisualMode.FullDig;
            bool constr = v.Kind == WorksKind.Construction;
            switch (g)
            {
                case PropGroup.SurveyCones: return A && constr;
                case PropGroup.EdgeCones: return !(A && constr);
                case PropGroup.SpoilHeaps:
                    if (!A) return false;
                    if (constr) return ph == WorksPhase.Excavation || ph == WorksPhase.Foundation || ph == WorksPhase.Paving || ph == WorksPhase.Finishing;
                    return v.Cancelled;
                case PropGroup.DumpHeaps:
                    return A && ((constr && ph == WorksPhase.Foundation) || (!constr && !v.Cancelled && ph == WorksPhase.Restore));
                case PropGroup.StoneWindrow:
                case PropGroup.Rubble:
                    return A && !constr && (ph == WorksPhase.BreakUp || ph == WorksPhase.Removal);
                case PropGroup.CrewProps:
                    return true;
                default:
                    return false;
            }
        }

        // Front component of the diff key: the per-crew front in 0.25 m steps (one crew: exactly the single-crew key
        // floor(MainFront * 4)). With n crews every crew front moves 1/n of the main front, so the same visual step needs
        // n times fewer diffs.
        public static uint FrontStep(in ProjectView v)
        {
            int n = v.CrewCount;
            float f = v.Front;
            return (uint)math.floor(n <= 1 ? f * 4f : f * 4f / n);
        }

        // Divider pick-up bucket (PropLayout.CentreConeStanding): the set of standing divider cones changes exactly when this
        // changes (cone k stands iff DividerU(k) > pick). 0 = nothing picked up.
        public static uint DividerPickKey(in ProjectView v)
        {
            if (v.Phase != WorksPhase.Finishing || v.F < 0.92f) return 0u;
            float pick = v.Trim0 + v.TrimmedLength * PhasePlan.Sweep(v.F, 0.92f, 0.96f);
            float x = (pick - v.Trim0 - RRWConst.kDividerEndGap) / RRWConst.kCentreConeSpacing;
            return x < 0f ? 1u : 2u + (uint)math.floor(x);
        }
    }
}
