using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

// Formation and separation fixes (reported problems: machines placed on top of each other, guard deadlocks, leavers crawling out):
//  * Front-relative anchors are clamped to the trimmed chain / crew section PER MACHINE. Crew 0's front starts at u 0, behind a
//    junction trim (Trim0 > 0), so at every phase start the machines of that crew collapse onto their own AnchorLo (Trim0 + 2.1 m +
//    half length): the D0 breaker and its inline truck (both lat 0, 0.9 m apart), the C3 paver and its feed truck / crew truck, two
//    leavers taking the same exit end slot. The helpers below keep the formation instead: room checks for inline loading, follower
//    homes out of the working machines' paths, a slot search over every resting box AND every working machine's path.
//  * The separation guard's rule "a standing puppet never yields" turned every static slot in a mover's way into a deadlock (TruckB
//    waiting at its base in the excavator's lane, a leaver parked at a crew's section start in front of the paver). MakeWay moves the
//    STANDING one aside when the mover has the higher make-way priority (front owner > truck on a trip > other working role >
//    standing idle > leaver); MachineGuard.MakeWayPass runs it after kMakeWaySeconds.
//  * Leavers turn around (compact K-turn sized to the lane policy's band) instead of reversing hundreds of metres, or drive on to the
//    end ahead when no turn fits (ChooseLeaveEnd / MakePostSite).
// Main thread only, no allocation on the per-update paths (log strings only on rare events).
namespace RealisticRoadWorks.V3.Machines
{
    public enum MakeWayResult : byte { None = 0, Done, Budget }

    public static partial class Choreo
    {
        // ------------------------------------------------------------ liveness (offline tests)

        // Another puppet counts for the slot / spot tests when its entity is alive. The offline tests (no World) plan with a default
        // EntityManager: every registered puppet with a plan counts there (in game the context always carries the system's manager).
        internal static bool Live(Puppet o, PlanContext c) => c.Em.Equals(default(EntityManager)) ? o.Plan.Count > 0 : o.Alive(c.Em);

        // ------------------------------------------------------------ working paths

        // Upper end of the anchors of crew `crew` of the context's project (its section end, the trims with one crew).
        static float CrewSectionHi(PlanContext c, int crew)
        {
            if (crew == c.CrewIndex) return c.SecHi;
            int n = c.View.CrewCount;
            if (n <= 1 || crew < 0 || crew >= n) return c.Trim1;
            return math.min(c.Trim1, PhasePlan.SectionStart(crew + 1, n, c.View.U));
        }

        // The plan works with its front from the running leg on (a Follow / DigHop / Shuttle / Scrape leg is running or still to come):
        // the machine advances along +u up to its section end. s = the state now, frontLat = the lateral of its first front leg.
        // digHopOnly: only a DigHop leg counts (the loading trucks hop through the section with the excavator).
        static bool WorksForward(in MachinePlan plan, PlanContext c, out ChainState s, out float frontLat, out float frontU, bool digHopOnly = false)
        {
            MachineMotion.State(plan, c.Clk, c.Now, out s);
            frontLat = s.Lat;
            frontU = s.U;
            for (int i = math.max(0, s.Leg); i < plan.Count; i++)
            {
                var l = plan.Get(i);
                if (digHopOnly ? l.Kind == (byte)LegKind.DigHop : FrontDep(l)) { if (i != s.Leg) { frontLat = l.L0; frontU = l.U1; } return true; }
            }
            return false;
        }

        // Follow-up (C4 swap report): a PATH owner sweeps its whole section with the front - the crew's front owner (excavator, grader,
        // paver / painter) and the loading trucks hopping with the excavator. Followers (crew truck, rollers, the C3 feed truck in its
        // hopper Follow) keep designed offsets in the formation; their "path" is not a corridor static slots must avoid (a roller's
        // window ahead of the crew truck is fine).
        static bool PathOwner(Puppet o, PlanContext c, out ChainState s, out float frontLat, out float frontU)
        {
            if (!o.IsRoller && o.Role == c.Crew.FrontOwner(c.View.Phase)) return WorksForward(o.Plan, c, out s, out frontLat, out frontU);
            if (o.IsTruck) return WorksForward(o.Plan, c, out s, out frontLat, out frontU, true);
            s = default; frontLat = 0f; frontU = 0f;
            return false;
        }

        // The working machine (not leaving, a front-relative leg running or planned) of the context's project whose PATH holds a box of p
        // standing at (u, lat): ahead of it up to its crew's section end (+ kSecSlack + its box), in its lane band (its lateral now or
        // of its front leg). Static slots - parking, leave stops, truck bases, follower homes - stay out of these corridors, so a working
        // machine never drives into a machine standing on its way. horizonS (finite): only the part of the path it reaches within that
        // many machine s (its drive to the front leg, then the front's speed, + 10 m) - spawn spots, which are left again soon (a truck
        // of crew i may wait in crew i-1's section far ahead of that crew's excavator). Null = none.
        public static Puppet InWorkPath(PlanContext c, Puppet p, float u, float lat) => InWorkPath(c, p, u, lat, float.PositiveInfinity);

        public static Puppet InWorkPath(PlanContext c, Puppet p, float u, float lat, float horizonS) => InWorkPath(c, p, u, lat, horizonS, horizonS);

        // horizonOther: the horizon for path owners of OTHER crews than p's (a spawn spot is left again within seconds when another crew's
        // owner is concerned; its own crew's owner may keep it waiting at its base for minutes)
        public static Puppet InWorkPath(PlanContext c, Puppet p, float u, float lat, float horizonS, float horizonOther)
        {
            if (c.Project == null) return null;
            float hlP = p.BoxHalfLen + math.abs(p.BoxOffZ), hwP = p.BoxHalfWid;
            for (int i = 0; i < c.Others.Count; i++)
            {
                var o = c.Others[i];
                if (o == p || o.ProjectId != c.Project.Id || o.PostSite || o.Outgoing || o.Plan.Count <= 0 || !Live(o, c)) continue;
                if (!PathOwner(o, c, out var s, out float fl, out float fu)) continue;
                float band = hwP + o.BoxHalfWid + 2f * MxConst.kOverlapMargin;
                if (math.abs(lat - s.Lat) >= band && math.abs(lat - fl) >= band) continue;
                float hlO = o.BoxHalfLen + math.abs(o.BoxOffZ);
                float a0 = math.min(s.U, fu) - hlO - MxConst.kOverlapMargin;
                float a1 = CrewSectionHi(c, o.Crew) + MxConst.kSecSlack + hlO + MxConst.kOverlapMargin;
                float h = o.Crew == p.Crew ? horizonS : horizonOther;
                if (!float.IsPositiveInfinity(h))
                {
                    float vF = math.abs(MachineMotion.FrontSpeed(o.Plan.FrontA, c.Clk, c.Now));
                    a1 = math.min(a1, math.max(s.U, fu) + hlO + MxConst.kOverlapMargin + 10f + vF * h);
                }
                if (u + hlP < a0 || u - hlP > a1) continue;
                return o;
            }
            return null;
        }

        // Box-centre range of p at u: the lane policy's (visible road, kept halves, parking strips, fences) or the floor (hidden).
        static void LatBand(PlanContext c, Puppet p, float u, out float a, out float b)
        {
            LatRange(c, p, u, out a, out b);
            if (float.IsInfinity(a) || float.IsInfinity(b))
            {
                var smp = TrackBuilder.At(c.Track, u);
                float flat = smp.FlatHalf > 0f ? smp.FlatHalf : 4f;
                float lim = math.max(0f, flat - p.BoxHalfWid - 0.3f);
                a = -lim; b = lim;
            }
        }

        // ------------------------------------------------------------ static slots

        // A static slot (Parked role) whose box meets another puppet's resting box, or (preferred) lies in a working machine's path,
        // moves along u in kParkStep steps - away from the nearer end of the crew section first, at most kStaticShiftMax - keeping
        // its lateral. A leaver that drives away does not block (SlotFree tests resting boxes: its stop at the exit). False when
        // nothing within reach is better (unchanged: the guard's make-way is the backstop).
        public static bool ResolveStatic(PlanContext c, Puppet p, ref float u, float lat, out string how)
        {
            how = "";
            if (c.Track == null) return false;
            bool free0 = SlotFree(p, c, u, lat);
            if (free0 && InWorkPath(c, p, u, lat) == null) return false;
            float lo = AnchorLo(c, p), hi = AnchorHi(c, p);
            float d1 = u <= 0.5f * (c.SecLo + c.SecHi) ? 1f : -1f;
            int n = (int)(MxConst.kStaticShiftMax / MxConst.kParkStep);
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1 && free0) break;   // free but in a path: a free-only spot elsewhere is no better
                for (int k = 1; k <= n; k++)
                    for (int sg = 0; sg < 2; sg++)
                    {
                        float uu = u + (sg == 0 ? d1 : -d1) * k * MxConst.kParkStep;
                        if (uu < lo - 0.01f || uu > hi + 0.01f) continue;
                        if (!SlotFree(p, c, uu, lat)) continue;
                        if (pass == 0 && InWorkPath(c, p, uu, lat) != null) continue;
                        how = "slot moved " + RRWLog.F(uu - u) + " m" + (pass == 0 ? "" : " (still in a working path)");
                        u = uu;
                        MachineDebug.StaticShifts++;
                        return true;
                    }
            }
            return false;
        }

        // Relation of a follower p to the path owner w whose path holds its home (ClearOfPaths `rel`).
        public const int kRelSpawn = 0;      // p does not exist yet (spawn): any home is reachable (it appears there)
        public const int kRelBehind = 1;     // p stands behind w (or level with it): it can get behind / stay behind without passing w
        public const int kRelAheadAside = 2; // p stands ahead of w but outside w's lane band (verge, other lane): w can pass it
        public const int kRelAheadInBand = 3;// p stands ahead of w IN its band: neither can pass the other in a narrow lane (inverted)

        // A follower held at its home (C3 / C4 crew truck) must not wait in a path owner's path (the paver / painter starting at the
        // section start would run into it): (1) a lateral clear of that machine inside the lane band, (2) else the home behind it (its
        // rear - separation, >= uMin) - only when the follower can get there without passing it (a spawn, or it stands behind it).
        // horizonS: the paths that count are the parts their owners reach within that many machine s (the home is left again once the
        // front is past it: crew i-1's painter reaches the tail of its section only at the end of the phase). True when lo / lat changed.
        // `blocker` = the owner whose path still holds the home (null when cleared or none); `rel` = p's relation to it.
        public static bool ClearOfPaths(PlanContext c, Puppet p, ref float lo, ref float lat, float uMin, out Puppet blocker) =>
            ClearOfPaths(c, p, ref lo, ref lat, uMin, float.PositiveInfinity, out blocker, out _);

        public static bool ClearOfPaths(PlanContext c, Puppet p, ref float lo, ref float lat, float uMin, float horizonS, out Puppet blocker, out int rel)
        {
            rel = kRelSpawn;
            blocker = InWorkPath(c, p, lo, lat, horizonS);
            if (blocker == null || c.Track == null) return false;
            var w = blocker;
            MachineMotion.State(w.Plan, c.Clk, c.Now, out var ws);
            float need = p.BoxHalfWid + w.BoxHalfWid + 2f * MxConst.kOverlapMargin + 0.05f;
            LatBand(c, p, lo, out float ra, out float rb);
            // the side it already is on first (no lane change across the working machine), else the slot's side
            float side = lat;
            if (p.Plan.Count > 0)
            {
                MachineMotion.State(p.Plan, c.Clk, c.Now, out var ps);
                side = ps.Lat;
                bool inBand = math.abs(ps.Lat - ws.Lat) < need - 0.05f;
                rel = ps.U <= ws.U + 0.05f ? kRelBehind : inBand ? kRelAheadInBand : kRelAheadAside;
            }
            for (int i = 0; i < 2; i++)
            {
                bool right = (side >= ws.Lat) == (i == 0);
                float l = right ? ws.Lat + need : ws.Lat - need;
                if (l < ra - 1e-3f || l > rb + 1e-3f) continue;
                if (InWorkPath(c, p, lo, l, horizonS) != null || !SlotFree(p, c, lo, l)) continue;
                lat = l;
                blocker = null;
                return true;
            }
            float back = ws.U - SepU(p, w);
            if ((rel == kRelSpawn || rel == kRelBehind) && back >= uMin && back < lo && InWorkPath(c, p, back, lat, horizonS) == null && SlotFree(p, c, back, lat))
            {
                lo = back;
                blocker = null;
                return true;
            }
            return false;
        }

        // A leading crew truck whose lead cannot last to the end of the stage (the last crew: its upper clamp is the painter's): it drives
        // off kLeadEndSeconds before the painter would close in on it (MachineDirectorSystem: leave + the role is done for this stage).
        // tEnd = the machine time of that (+inf: the lead lasts - an interior crew leads into the next section's start).
        public static bool LeadEnd(PlanContext c, Puppet p, out double tEnd)
        {
            tEnd = double.PositiveInfinity;
            if (!LeadParams(c, p, out float off, out _, out float hi)) return false;
            var w = c.CS.Roles[(int)c.Crew.FrontOwner(c.View.Phase)];
            var last = w.Plan.Get(w.Plan.Count - 1);
            float sep = SepU(p, w) + 0.5f, wOff = off - sep;
            float wHi = last.Kind == (byte)LegKind.Follow ? last.Hi : CrewHi(c, w);
            // the painter never gets further than its clamp or its section end + its offset (the crew front stops at the section end)
            float wMax = c.Crews > 1 && c.CrewIndex < c.Crews - 1 ? math.min(wHi, c.SecHi + wOff) : wHi;
            if (wMax + sep <= hi + 0.05f) return false;
            float F = c.FrontAt(c.Now), vF = math.max(0.01f, math.abs(c.FrontSpeed));
            float room = hi - (F + wOff + sep) - vF * MxConst.kLeadEndSeconds;
            if (room <= 0f) { tEnd = c.Now; return true; }
            tEnd = c.Now + room / vF;
            return false;
        }

        // Upper clamp of a follower p behind its crew's front owner: the owner's anchor clamp (its Follow leg's Hi) minus the separation.
        // False without an owner in a Follow leg.
        public static bool BehindOwnerHi(PlanContext c, Puppet p, out float hi)
        {
            hi = float.PositiveInfinity;
            var w = c.CS != null ? c.CS.Roles[(int)c.Crew.FrontOwner(c.View.Phase)] : null;
            if (w == null || w == p || w.PostSite || w.Outgoing || w.Plan.Count <= 0) return false;
            var last = w.Plan.Get(w.Plan.Count - 1);
            if (last.Kind != (byte)LegKind.Follow) return false;
            hi = last.Hi - SepU(p, w);
            return true;
        }

        // Follow-up (C4 swap report): a C4 crew truck standing AHEAD of its painter in the painter's lane (the C3 -> C4 relay puts crew i-1's
        // paver at B_i + 1.7 behind crew i's crew truck, which came home at B_i + 12): in a works half too narrow to pass it leads the
        // painter instead of reversing through it - its anchor moves with the front kFinisherLead + separation ahead of the painter's
        // (interior crews: up to that far into the next section, which that crew's painter left long before). False = no painter.
        public static bool LeadParams(PlanContext c, Puppet p, out float off, out float lo, out float hi)
        {
            off = 0f; lo = CrewLo(c, p); hi = CrewHi(c, p);
            var w = c.CS != null ? c.CS.Roles[(int)c.Crew.FrontOwner(c.View.Phase)] : null;
            if (w == null || w == p || w.PostSite || w.Outgoing || w.Plan.Count <= 0) return false;
            var last = w.Plan.Get(w.Plan.Count - 1);
            float wOff = last.Kind == (byte)LegKind.Follow ? last.U0 : RRWConst.kFinisherLead;
            float sep = SepU(p, w) + 0.5f;
            off = wOff + sep;
            if (c.Crews > 1 && c.CrewIndex < c.Crews - 1) hi = math.max(hi, math.min(AnchorHi(c, p), c.SecHi + wOff + sep + 0.5f));
            if (hi < lo) hi = lo;
            return true;
        }

        // ------------------------------------------------------------ narrow inline loading (D0 / D1, narrow C1)

        // Inline loading offset (truck pivot - excavator anchor) that clears the excavator's TRUE body box (its rear extent behind the
        // pivot; it faces +u) and the truck's box by kInlineClear. The simple offset (kExcavatorBack + truck half length + 1) assumes
        // a 3.5 m rear extent; the rig's box reaches further (it put the excavator body on the truck in D0).
        public static float InlineLoadOffset(PlanContext c, Puppet p)
        {
            float off = -RRWConst.kExcavatorBack - p.BoxHalfLen - 1.0f;
            var e = c.CS != null ? c.CS.Roles[(int)MachineRole.Excavator] : null;
            float rearE = e != null ? e.BoxHalfLen - e.BoxOffZ : RRWConst.kExcavatorBack;
            float need = math.max(0f, rearE) + p.BoxHalfLen + math.abs(p.BoxOffZ) + MxConst.kInlineClear;
            return math.min(off, -need);
        }

        // Room behind the excavator for an INLINE loading truck: its stop (the excavator's position when it arrives + LoadOffset) must
        // not be clamped up to the truck's AnchorLo (into the excavator) while the excavator still stands at its own clamp at the
        // section start (crew 0 of a road trimmed past a junction: the front starts behind the trim). The first grid cycle start >= tk
        // whose inline stop lies inside the trims; NaN = none within kInlineRoomCycles; tk itself for side-by-side loading.
        public static double LoadRoomFrom(PlanContext c, Puppet p, in TruckCfg cfg, double tk, float C)
        {
            var cs = c.CS;
            if (cfg.Mode != 1 || cs == null || cfg.LoadOffset > -2f || !(C > 0f)) return tk;
            float low = AnchorLo(c, p) - 0.05f;
            for (int k = 0; k <= MxConst.kInlineRoomCycles; k++)
            {
                double t = tk + k * (double)C;
                if (DigAnchor(c, cs, t - 0.5 * C) + cfg.LoadOffset >= low) return t;
            }
            return double.NaN;
        }

        // C3 feed: the lower clamp of the crew's paver anchor (its committed Follow leg's Lo, else its CrewLo); -inf without a paver.
        // The feed truck's stop / creep keep their distance to it (both used to clamp to their own AnchorLo at a trimmed start).
        static float PaverLo(PlanContext c)
        {
            var f = c.CS != null ? c.CS.Roles[(int)MachineRole.Finisher] : null;
            if (f == null || f.PostSite || f.Outgoing || f.Plan.Count <= 0) return float.NegativeInfinity;
            var l = f.Plan.Get(f.Plan.Count - 1);
            return l.Kind == (byte)LegKind.Follow ? l.Lo : CrewLo(c, f);
        }

        // A narrow / inline truck's waiting point never lies closer to the excavator than the inline clearance (the rolling base 33 m
        // behind the front 90 s ahead could sit right behind a slow excavator): base <= excavator now + LoadOffset (>= AnchorLo).
        public static float InlineBaseCap(PlanContext c, Puppet p, in TruckCfg cfg, float baseU)
        {
            if (cfg.Mode != 1 || cfg.LoadOffset > -2f || c.CS == null) return baseU;
            var e = c.CS.Roles[(int)MachineRole.Excavator];
            if (e == null || e.Plan.Count <= 0) return baseU;
            MachineMotion.State(e.Plan, c.Clk, c.Now, out var es);
            return math.max(AnchorLo(c, p), math.min(baseU, es.U + cfg.LoadOffset));
        }

        // ------------------------------------------------------------ make-way

        // Make-way priority of a puppet that WANTS to move (held by the guard / standing still for a conflict): 5 its crew's front owner,
        // 4 a truck, 3 any other working role, 1 a leaver.
        public static int MoverPriority(Puppet p, MachineRole owner)
        {
            if (p.PostSite || p.Outgoing) return 1;
            if (!p.IsRoller && p.Role == owner) return 5;
            return p.IsTruck ? 4 : 3;
        }

        // ... of a STANDING puppet: 5 the front owner, 4 a truck on a trip (approach / load / tip / feed), 2 anything else standing (a
        // truck waiting at its base or in a stand-still, a parked role, a follower held at its home), 1 a leaver.
        public static int BlockerPriority(Puppet q, MachineRole owner)
        {
            if (q.PostSite || q.Outgoing) return 1;
            if (!q.IsRoller && q.Role == owner) return 5;
            if (q.IsTruck && q.Stage == TruckStage.Outbound && !q.StandStill) return 4;
            return 2;
        }

        static readonly float[] s_MwLat = new float[12];
        static readonly bool[] s_MwVerge = new bool[12];
        // per-call caches of MakeWay (no allocation): the others' resting / current boxes, the working corridors, the mover's path boxes
        const int kMwMax = 128;
        static readonly Obb[] s_MwRest = new Obb[kMwMax], s_MwNow = new Obb[kMwMax];
        static readonly bool[] s_MwHasRest = new bool[kMwMax], s_MwHasNow = new bool[kMwMax];
        static readonly float4[] s_MwWork = new float4[kMwMax];   // (a0, a1, lat now, lat of the front leg)
        static readonly float[] s_MwWorkHw = new float[kMwMax];
        static readonly Obb[] s_MwPath = new Obb[(int)(MxConst.kMakeWayHorizon / MxConst.kSampleStep) + 4];

        static void MwAdd(ref int n, float lat, bool verge, float cur)
        {
            if (float.IsNaN(lat)) return;
            for (int i = 0; i < n; i++) if (math.abs(s_MwLat[i] - lat) < 0.3f) return;
            if (n >= s_MwLat.Length) return;
            // insertion by distance from the current lateral (index 0 = the current lane stays first)
            int j = n++;
            while (j > 1 && math.abs(s_MwLat[j - 1] - cur) > math.abs(lat - cur)) { s_MwLat[j] = s_MwLat[j - 1]; s_MwVerge[j] = s_MwVerge[j - 1]; j--; }
            s_MwLat[j] = lat;
            s_MwVerge[j] = verge;
        }

        // q stands in the way of mover m (m held kMakeWaySeconds by the guard, higher make-way priority), or the two stand on top of each
        // other (overlapping: q is the one that separates). q moves to the nearest standing spot that is (1) free of every resting box
        // and every box now, (2) out of every working machine's path (a leaver: only such spots), (3) clear of m's wanted path over
        // kMakeWayHorizon s, (4) reached on a route that meets nobody (overlapping: that never closes on m). Candidates: the lane
        // fractions / verges at q's u (nearest lateral first), then along u in kParkStep rings up to kMakeWayReach both ways. The plan is
        // returned (not committed). Budget = a route check ran out of the per-update check budget (try again next update). The boxes of
        // the others, the working corridors and m's path are evaluated once per call: a candidate costs a few box tests until its route.
        public static MakeWayResult MakeWay(Puppet q, Puppet m, in MachinePlan mPath, PlanContext c, bool overlapping, out MachinePlan plan, out string how) =>
            MakeWay(q, m, mPath, c, overlapping, false, out plan, out how);

        // force: the route checks run to their end (no per-update check budget; after two Budget results in a row: the search always ends)
        public static MakeWayResult MakeWay(Puppet q, Puppet m, in MachinePlan mPath, PlanContext c, bool overlapping, bool force, out MachinePlan plan, out string how)
        {
            plan = default;
            how = "";
            if (q.Plan.Count <= 0 || c.Track == null || m == null) return MakeWayResult.None;
            MxPerf.Begin(MxT.X_PlanYield);
            var r = MakeWayCore(q, m, mPath, c, overlapping, force, out plan, out how);
            MxPerf.End(MxT.X_PlanYield);
            return r;
        }

        static MakeWayResult MakeWayCore(Puppet q, Puppet m, in MachinePlan mPath, PlanContext c, bool overlapping, bool force, out MachinePlan plan, out string how)
        {
            plan = default;
            how = "";
            double now = c.Now;
            MachineMotion.State(q.Plan, c.Clk, now, out var s);
            sbyte facing = s.Hu >= 0f ? (sbyte)1 : (sbyte)-1;
            int nl = 0;
            s_MwLat[0] = s.Lat; s_MwVerge[0] = false; nl = 1;
            MwAdd(ref nl, LatOf(c, -0.45f, s.U, q), false, s.Lat);
            MwAdd(ref nl, LatOf(c, -PhasePlan.kRightLane, s.U, q), false, s.Lat);
            MwAdd(ref nl, LatOf(c, -0.15f, s.U, q), false, s.Lat);
            MwAdd(ref nl, LatOf(c, 0f, s.U, q), false, s.Lat);
            MwAdd(ref nl, LatOf(c, 0.15f, s.U, q), false, s.Lat);
            MwAdd(ref nl, LatOf(c, PhasePlan.kRightLane, s.U, q), false, s.Lat);
            MwAdd(ref nl, LatOf(c, 0.45f, s.U, q), false, s.Lat);
            if (!c.Visible)
            {
                MwAdd(ref nl, LatOf(c, -1f, s.U, q), true, s.Lat);
                MwAdd(ref nl, LatOf(c, 1f, s.U, q), true, s.Lat);
            }
            // (1) the others' boxes at rest and now (planner margins) and (2) the working corridors (InWorkPath, whole section)
            int no = 0, nw = 0;
            bool slow = c.Others.Count > kMwMax;
            for (int i = 0; i < c.Others.Count && !slow; i++)
            {
                var o = c.Others[i];
                if (o == q || o.Plan.Count <= 0 || !Live(o, c)) continue;
                s_MwHasRest[no] = RestBox(o, c, out s_MwRest[no]);
                s_MwHasNow[no] = Box(o, o.Plan, c, now, out s_MwNow[no]);
                no++;
                if (c.Project == null || o.ProjectId != c.Project.Id || o.PostSite || o.Outgoing || !PathOwner(o, c, out var ws, out float fl, out float fu)) continue;
                float hlO = o.BoxHalfLen + math.abs(o.BoxOffZ);
                s_MwWork[nw] = new float4(math.min(ws.U, fu) - hlO - MxConst.kOverlapMargin,
                                          CrewSectionHi(c, o.Crew) + MxConst.kSecSlack + hlO + MxConst.kOverlapMargin, ws.Lat, fl);
                s_MwWorkHw[nw] = o.BoxHalfWid;
                nw++;
            }
            // (3) m's path boxes over the horizon (planner margins)
            double to = now + MxConst.kMakeWayHorizon;
            if (mPath.Count > 0)
            {
                var last = mPath.Get(mPath.Count - 1);
                bool open = last.OpenEnded && FrontDep(last);
                double mEnd = MachineMotion.MotionEnd(mPath);
                if (!open && !double.IsNaN(mEnd) && mEnd + 2.0 < to) to = math.max(now + 2.0, mEnd + 2.0);
            }
            int np = 0;
            if (mPath.Count > 0)
                for (long k = PlanSamples.Floor(now), k1 = PlanSamples.Ceil(to); k <= k1 && np < s_MwPath.Length; k++)
                {
                    if (!BoxAt(m, mPath, c, PlanSamples.Time(k), out var b, out _)) continue;
                    b.HL += MxConst.kOverlapMargin; b.HW += MxConst.kOverlapMargin;
                    s_MwPath[np++] = b;
                }
            float gap0 = float.NaN;
            if (overlapping && BoxAt(q, q.Plan, c, now, out var qa, out _) && BoxAt(m, m.Plan, c, now, out var ma, out _)) gap0 = Gap(qa, ma);
            float hlQ = q.BoxHalfLen + math.abs(q.BoxOffZ), hwQ = q.BoxHalfWid;
            float lo = AnchorLo(c, q), hi = AnchorHi(c, q);
            bool leaver = q.PostSite || q.Outgoing;
            int rings = (int)(MxConst.kMakeWayReach / MxConst.kParkStep);
            int routed = 0;
            for (int pass = 0; pass < (leaver ? 1 : 2); pass++)
                for (int r = 0; r <= rings; r++)
                    for (int sg = 0; sg < (r == 0 ? 1 : 2); sg++)
                    {
                        float uu = s.U + (sg == 0 ? 1f : -1f) * r * MxConst.kParkStep;
                        if (uu < lo - 0.01f || uu > hi + 0.01f) continue;
                        for (int li = 0; li < nl; li++)
                        {
                            if (r == 0 && li == 0) continue;   // where it stands now
                            float ll = s_MwLat[li];
                            if (!StandBox(q, c, uu, ll, out var a)) continue;
                            // (1) free of every box at rest and now (the slow path for a crowded neighbourhood)
                            bool free = true;
                            if (slow) free = SpotFree(q, c, uu, ll);
                            else for (int i = 0; i < no && free; i++)
                                    if ((s_MwHasRest[i] && Overlap(a, s_MwRest[i])) || (s_MwHasNow[i] && Overlap(a, s_MwNow[i]))) free = false;
                            if (!free) continue;
                            // (2) working corridors: pass 0 only spots out of every path, pass 1 only the spots pass 0 skipped
                            bool inPath = false;
                            if (slow) inPath = InWorkPath(c, q, uu, ll) != null;
                            else for (int i = 0; i < nw && !inPath; i++)
                                {
                                    var w = s_MwWork[i];
                                    float band = hwQ + s_MwWorkHw[i] + 2f * MxConst.kOverlapMargin;
                                    if (math.abs(ll - w.z) >= band && math.abs(ll - w.w) >= band) continue;
                                    inPath = uu + hlQ >= w.x && uu - hlQ <= w.y;
                                }
                            if (pass == 0 ? inPath : !inPath) continue;
                            // (3) clear of m's wanted path
                            bool clear = true;
                            for (int i = 0; i < np && clear; i++) if (Overlap(a, s_MwPath[i])) clear = false;
                            if (!clear) continue;
                            if (s_MwVerge[li] && !VergeClear(c, q, uu, ll)) continue;
                            // (4) the route
                            if (++routed > MxConst.kMakeWayCandidates) return MakeWayResult.None;
                            var b = Begin(q, c);
                            b.Leaving = leaver;
                            GoTo(ref b, q, c, uu, ll, facing, false);
                            Hold(ref b, float.PositiveInfinity, AnimKind.Rest);
                            if (b.Overflow) continue;
                            double arrive = MachineMotion.MotionEnd(b.Plan);
                            if (overlapping && !Separates(q, b.Plan, m, c, now, arrive, gap0)) continue;
                            if (RouteConflict(q, b.Plan, c, now, arrive + 1.0, overlapping ? m : null, force, out bool aborted))
                            {
                                if (aborted) return MakeWayResult.Budget;   // check budget exhausted (unknown): next update
                                continue;
                            }
                            plan = b.Plan;
                            how = (r == 0 ? "aside" : (uu > s.U ? "ahead " : "back ") + RRWLog.F(math.abs(uu - s.U)) + " m") +
                                  " to lat " + RRWLog.F(ll) + (s_MwVerge[li] ? " (verge)" : "") + (inPath ? " (in a working path)" : "");
                            return MakeWayResult.Done;
                        }
                    }
            return MakeWayResult.None;
        }

        static readonly ConflictScan s_MwScan = new ConflictScan();

        // The route plan meets another puppet's committed plan over [from, to] (guard margin; `ignore` skipped). Inside the per-update
        // check budget unless forced; aborted = the budget cut the check (unknown).
        internal static bool RouteConflict(Puppet q, in MachinePlan plan, PlanContext c, double from, double to, Puppet ignore, bool force, out bool aborted)
        {
            aborted = false;
            if (!force && MxBudget.CheckExhausted) { aborted = true; return true; }
            long t0 = MxBudget.Start();
            var s = s_MwScan;
            ScanStart(s, q, plan, c, from, to, MxConst.kGuardMargin, ignore);
            var st = ScanRun(s, c, force ? NoDeadline : MxBudget.CheckDeadline(t0));
            MxBudget.StopCheck(t0);
            s.Me = s.Ignore = s.Who = null;
            s.Near.Clear();
            if (st == ScanState.Running) { s.State = ScanState.Idle; aborted = true; return true; }
            return st == ScanState.Conflict;
        }

        // A blocking leaver with no spot aside: its drive-off to the chain end on the FAR side of the machine it blocks (PlanLeave from
        // its state now: lane, end slot, turn rules), returned when that route meets nobody (not committed). None: already heading there,
        // or the route is blocked too (the guard then removes the leaver); Budget: the route check ran out of the check budget.
        public static MakeWayResult LeaveAway(Puppet y, Puppet o, PlanContext c, bool force, out MachinePlan plan, out float end, out bool more, out string where)
        {
            plan = default; end = float.NaN; more = false; where = "";
            if (y.Plan.Count <= 0 || o.Plan.Count <= 0 || c.Track == null) return MakeWayResult.None;
            double now = c.Now;
            MachineMotion.State(y.Plan, c.Clk, now, out var ys);
            MachineMotion.State(o.Plan, c.Clk, now, out var os);
            end = os.U <= ys.U ? c.Trim1 : c.Trim0;
            if (!float.IsNaN(y.LeaveEndU) && math.abs(end - y.LeaveEndU) < 0.5f) return MakeWayResult.None;   // already heading there
            var saved = y.Plan;
            double savedAt = y.ReplanAt;
            PlanLeave(y, c, end, out where, out more);
            plan = y.Plan;
            y.Plan = saved;
            y.ReplanAt = savedAt;
            double to = math.min(MachineMotion.MotionEnd(plan) + 1.0, now + MxConst.kPlanCheckMax);
            if (RouteConflict(y, plan, c, now, math.max(to, now + 1.0), null, force, out bool aborted))
                return aborted ? MakeWayResult.Budget : MakeWayResult.None;
            return MakeWayResult.Done;
        }

        // ------------------------------------------------------------ head-on leavers

        // A leaver still on its way out: it has an exit end and its plan moves, or the guard holds it / it waits for a continuation.
        public static bool Travelling(Puppet p, double now)
        {
            if (!p.PostSite || p.Plan.Count <= 0 || float.IsNaN(p.LeaveEndU)) return false;
            return MachineMotion.MotionEnd(p.Plan) > now + 0.1 || p.GuardHeld || p.StandStill || p.LeaveContinue;
        }

        // Two leavers at (uA, latA) and (uB, latB) heading for endA / endB meet head-on: opposite directions, B ahead of A in A's direction
        // (they approach each other), each still has to pass the other's position, and their lane bands overlap (no passing in the lane).
        public static bool HeadOn(float uA, float latA, float hwA, float hlA, float endA, float uB, float latB, float hwB, float hlB, float endB)
        {
            float dA = endA - uA, dB = endB - uB;
            if (math.abs(dA) < 0.5f || math.abs(dB) < 0.5f || math.sign(dA) == math.sign(dB)) return false;
            if ((uB - uA) * math.sign(dA) <= 0f) return false;                          // moving apart
            float reach = hlA + hlB;
            if ((endA - uB) * math.sign(dA) < -reach || (endB - uA) * math.sign(dB) < -reach) return false;   // one stops before the other
            return math.abs(latA - latB) < hwA + hwB + 2f * MxConst.kOverlapMargin;
        }

        static bool HeadOnPair(Puppet a, float endA, Puppet b, PlanContext c)
        {
            MachineMotion.State(a.Plan, c.Clk, c.Now, out var sa);
            MachineMotion.State(b.Plan, c.Clk, c.Now, out var sb);
            return HeadOn(sa.U, sa.Lat, a.BoxHalfWid, a.BoxHalfLen + math.abs(a.BoxOffZ), endA, sb.U, sb.Lat, b.BoxHalfWid, b.BoxHalfLen + math.abs(b.BoxOffZ), b.LeaveEndU);
        }

        static readonly List<Puppet> s_HoGroupA = new List<Puppet>(8), s_HoGroupB = new List<Puppet>(8);

        // The group that switches exits in the last HeadOnChoice that returned true (valid until the next call; no allocation).
        public static List<Puppet> HeadOnGroup { get; private set; } = s_HoGroupA;

        // `lead` and the travelling leavers of its project that follow it to the same exit in its lane band (behind it in its direction):
        // when the lead turns round to the other exit, its followers would meet it head-on - they switch with it.
        static void Followers(PlanContext c, Puppet lead, List<Puppet> g)
        {
            g.Clear();
            g.Add(lead);
            MachineMotion.State(lead.Plan, c.Clk, c.Now, out var s);
            float dir = math.sign(lead.LeaveEndU - s.U);
            var all = MachineRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var q = all[i];
                if (q == lead || q.ProjectId != lead.ProjectId || !Travelling(q, c.Now) || math.abs(q.LeaveEndU - lead.LeaveEndU) > 0.5f) continue;
                MachineMotion.State(q.Plan, c.Clk, c.Now, out var qs);
                if ((qs.U - s.U) * dir >= 0f) continue;                                                       // ahead of the lead
                if (math.abs(qs.Lat - s.Lat) >= lead.BoxHalfWid + q.BoxHalfWid + 2f * MxConst.kOverlapMargin) continue;   // another lane
                g.Add(q);
            }
        }

        // Leavers a and b of the context's project head for each other in one lane (a narrow carriageway; a wide excavator spans both
        // halves): one SIDE takes the other side's exit - a side = the leaver and the leavers following it to its exit in its lane (they
        // would meet it head-on after it turned). Both sides then leave as a convoy (the one behind at the pace of the one ahead).
        //   cost(side G switches) = max(the longest time a member of G needs to the new exit, the longest time a member of the other side
        //   needs to its own exit) - leaving limits, reverse chunks / K-turns included (LeaveNeedSeconds) - + 300 s for a dead end + 1000 s
        //   per working machine on a new route + 10000 s for another leaver a new route meets head-on;
        // the cheaper side switches (ties within 1 s: the side of the lower rank, `aFirst`). None when both options still meet someone
        // head-on, or a member already switched twice (no ping-pong). HeadOnGroup = the switching side, end = its new exit.
        public static bool HeadOnChoice(PlanContext c, Puppet a, Puppet b, bool aFirst, out bool switchA, out float end, out string why)
        {
            switchA = false; end = float.NaN; why = "";
            if (!Travelling(a, c.Now) || !Travelling(b, c.Now) || a.ProjectId != b.ProjectId) return false;
            if (!HeadOnPair(a, a.LeaveEndU, b, c)) return false;
            Followers(c, a, s_HoGroupA);
            Followers(c, b, s_HoGroupB);
            double ca = SideCost(c, s_HoGroupA, s_HoGroupB, b.LeaveEndU);
            double cb = SideCost(c, s_HoGroupB, s_HoGroupA, a.LeaveEndU);
            if (double.IsInfinity(ca) && double.IsInfinity(cb)) { why = "no switch left (ping-pong limit)"; return false; }
            switchA = math.abs(ca - cb) < 1.0 ? aFirst : ca < cb;
            double best = switchA ? ca : cb;
            if (best >= 10000.0) { why = "both options meet another leaver head-on"; return false; }
            HeadOnGroup = switchA ? s_HoGroupA : s_HoGroupB;
            end = switchA ? b.LeaveEndU : a.LeaveEndU;
            why = "head-on with " + (switchA ? b : a) + ": " + HeadOnGroup.Count + " leaver(s) take its exit u=" + RRWLog.F(end) +
                  " (all out in ~" + RRWLog.F((float)best) + " s vs ~" + RRWLog.F((float)(switchA ? cb : ca)) + " s the other way)";
            return true;
        }

        static double SideCost(PlanContext c, List<Puppet> g, List<Puppet> h, float end)
        {
            double tG = 0.0, tH = 0.0, pen = 0.0;
            for (int i = 0; i < g.Count; i++)
            {
                var y = g[i];
                if (y.HeadOnSwitches >= 2) return double.PositiveInfinity;
                MachineMotion.State(y.Plan, c.Clk, c.Now, out var s);
                tG = math.max(tG, LeaveNeedSeconds(y, c, s.U, s.Hu >= 0f ? (sbyte)1 : (sbyte)-1, end, c.Now) - 30.0);
                if (CrewOnRoute(c, y, s.U, s.Lat, end) != null) pen += 1000.0;
                var all = MachineRegistry.All;
                for (int k = 0; k < all.Count; k++)
                {
                    var q = all[k];
                    if (q.ProjectId != y.ProjectId || g.Contains(q) || h.Contains(q) || !Travelling(q, c.Now)) continue;
                    if (HeadOnPair(y, end, q, c)) { pen += 10000.0; break; }
                }
            }
            for (int i = 0; i < h.Count; i++)
            {
                var o = h[i];
                MachineMotion.State(o.Plan, c.Clk, c.Now, out var s);
                tH = math.max(tH, LeaveNeedSeconds(o, c, s.U, s.Hu >= 0f ? (sbyte)1 : (sbyte)-1, o.LeaveEndU, c.Now) - 30.0);
            }
            bool atEnd = math.abs(end - c.Trim1) < math.abs(end - c.Trim0);
            if (!EndConnected(c, atEnd)) pen += 300.0;
            return math.max(tG, tH) + pen;
        }

        // The convoy cap of leaver p ends when its leader is gone (left through the exit) or no longer on its way.
        public static void ConvoyRelease(Puppet p, double now)
        {
            if (p.LeaveVmax < 0f && double.IsNaN(p.ConvoyDepart)) return;
            var l = p.ConvoyLeader;
            if (l == null || !MachineRegistry.All.Contains(l) || !Travelling(l, now)) { p.LeaveVmax = -1f; p.ConvoyLeader = null; p.ConvoyDepart = double.NaN; }
        }

        // Departure hold of a leaver's next drive-off candidate (the scan lead, or the convoy's delayed full-speed departure).
        public static float LeaveLead(Puppet p, double now) =>
            math.max(p.ScanLead, double.IsNaN(p.ConvoyDepart) ? 0f : (float)math.max(0.0, p.ConvoyDepart - now));

        // Convoy: leaver p's full-speed drive-off `cand` met `who`, a leaver ahead of it heading for the same exit. Two ways to
        // follow it without closing in: drive at its pace (vmax, ConvoySpeed) or wait and drive at full speed so it arrives just after
        // `who` stopped (a long slow crawl far behind the leader looks wrong and arrives late). The earlier arrival wins. False = not a
        // convoy pair. Sets p.LeaveVmax / p.ConvoyDepart / p.ConvoyLeader for the next candidate.
        public static bool ConvoyFollow(Puppet p, Puppet who, in MachinePlan cand, double now)
        {
            float cv = ConvoySpeed(p, who, now);
            if (!(cv > 0f)) return false;
            MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var sp);
            var last = cand.Get(cand.Count - 1);
            float dist = math.abs(last.U0 - sp.U);
            double travel = math.max(0.0, MachineMotion.MotionEnd(cand) - now - math.max(0.0, LeaveLead(p, now)));
            double leaderEnd = MachineMotion.MotionEnd(who.Plan);
            double tDelayed = math.max(now + travel, leaderEnd + 1.0);
            double tCrawl = now + dist / cv + 1.0;
            p.ConvoyLeader = who;
            if (!double.IsInfinity(leaderEnd) && tDelayed + 1.0 < tCrawl)
            {
                p.ConvoyDepart = tDelayed - travel;
                p.LeaveVmax = -1f;
            }
            else
            {
                p.ConvoyDepart = double.NaN;
                p.LeaveVmax = p.LeaveVmax < 0f ? cv : math.min(p.LeaveVmax, cv);
            }
            return true;
        }

        // Convoy: leaver p's drive-off met `who`, a leaver ahead of it heading for the same exit: p drives at most 0.9 x the
        // fastest leg `who` still plans (or its leaving limit when it stands), so it never closes in on it. -1 = not such a pair.
        public static float ConvoySpeed(Puppet p, Puppet who, double now)
        {
            if (who == null || p == null || !who.PostSite || float.IsNaN(who.LeaveEndU) || float.IsNaN(p.LeaveEndU)) return -1f;
            if (math.abs(who.LeaveEndU - p.LeaveEndU) > 0.5f || who.Plan.Count <= 0 || p.Plan.Count <= 0) return -1f;
            var clk = MachineRegistry.Clock;
            MachineMotion.State(p.Plan, clk, now, out var sp);
            MachineMotion.State(who.Plan, clk, now, out var sw);
            if ((sw.U - sp.U) * (p.LeaveEndU - sp.U) <= 0f) return -1f;   // who is not ahead of p towards the exit
            float v = 0f;
            for (int i = math.max(0, sw.Leg); i < who.Plan.Count; i++)
            {
                var l = who.Plan.Get(i);
                if (l.Kind == (byte)LegKind.Drive) v = math.max(v, l.Vmax);
            }
            if (!(v > 0.05f))
            {
                bool rev = (who.LeaveEndU - sw.U) * sw.Hu < 0f;
                v = who.Lim.VFor(rev, false, true) * MxConst.kSpeedMargin;
            }
            return math.max(0.2f, 0.9f * v);
        }

        // Work stop of a truck at machine time t for its rolling base (no K-turns): the front for loading / tipping, the hopper position
        // for the C3 feed - relative to the paver's clamped anchor (at a trimmed start the front lies behind the paver: a base taken
        // from the front stood on the paver).
        public static float WorkStopAt(PlanContext c, in TruckCfg cfg, double t)
        {
            float F = c.FrontAt(t);
            if (cfg.Mode != 3) return F;
            return math.max(F + RRWConst.kFinisherLead, PaverLo(c)) + 3.7f + 0.4f + 4.5f;
        }

        // Overlapping pair: the route of q never closes on m's box now (true-box gap never below its start value) and ends apart.
        static bool Separates(Puppet q, in MachinePlan route, Puppet m, PlanContext c, double from, double to, float gap0)
        {
            if (!BoxAt(m, m.Plan, c, from, out var mb, out _)) return false;
            float g0 = float.IsNaN(gap0) ? float.NegativeInfinity : gap0;
            float g = g0;
            for (double t = from + 0.25; ; t += 0.25)
            {
                double tt = math.min(t, to);
                if (!BoxAt(q, route, c, tt, out var qb, out _)) return false;
                g = Gap(qb, mb);
                if (g < g0 - 0.05f) return false;
                if (tt >= to) break;
            }
            return g > 2f * MxConst.kGuardMargin;
        }

        // ------------------------------------------------------------ leavers: compact K-turn

        // Lateral half sweep of p's box about the turn's pivot lateral during a three-arc K-turn of radius R (MachineMotion Turn: pivot
        // lateral within +-R/2, heading 0..180 deg; box centre offset folded in), + the guard margin.
        public static float TurnSweepHalf(Puppet p, float R)
        {
            float hl = p.BoxHalfLen, hw = p.BoxHalfWid, oz = math.abs(p.BoxOffZ);
            float best = 0f;
            for (int i = 0; i <= 36; i++)
            {
                float th = math.PI * i / 36f;
                float X;
                if (th <= math.PI / 3f) X = R * (1f - math.cos(th));
                else if (th <= 2f * math.PI / 3f) X = R * math.cos(th);
                else X = -R - R * math.cos(th);
                float sn = math.sin(th), cs = math.abs(math.cos(th));
                best = math.max(best, math.abs(X) + (oz + hl) * sn + hw * cs);
            }
            return best + MxConst.kGuardMargin;
        }

        // Edges of the band a turning box may sweep at u: the lane policy's box-centre range widened by the half width (visible road,
        // kept halves, parked strips, fences), else the trench floor. False when the box does not fit the policy band at all.
        static bool TurnBand(PlanContext c, Puppet p, float u, out float eL, out float eR)
        {
            bool fit = LatRange(c, p, u, out float a, out float b);
            if (!float.IsInfinity(a) && !float.IsInfinity(b))
            {
                eL = a - p.BoxHalfWid; eR = b + p.BoxHalfWid;
                return fit && eR > eL;
            }
            var smp = TrackBuilder.At(c.Track, u);
            float flat = smp.FlatHalf > 0f ? smp.FlatHalf : 4f;
            eL = -flat + 0.3f; eR = flat - 0.3f;
            return true;
        }

        // A compact K-turn for a leaver whose exit lies behind its nose beyond kMaxReverseLeg where the standard K-turn (kTurnRadius,
        // kTurnSweepHalf) does not fit: the largest radius of kLeaveTurnRadii whose sweep fits the band (checked at the pivot and one
        // radius either side), at the first spot from u towards the exit (kParkStep steps over kLeaveTurnSearch m). latC = the band centre.
        public static bool LeaveTurnFit(PlanContext c, Puppet p, float u, sbyte mv, out float tu, out float latC, out float R)
        {
            tu = u; latC = 0f; R = 0f;
            if (p.IsRoller || c.Track == null) return false;
            float lo = RunLo(c, p) + 1f, hi = RunHi(c, p) - 1f;
            var radii = MxConst.kLeaveTurnRadii;
            int nk = (int)(MxConst.kLeaveTurnSearch / MxConst.kParkStep);
            for (int ir = 0; ir < radii.Length; ir++)
            {
                float r = radii[ir], S = TurnSweepHalf(p, r);
                for (int k = 0; k <= nk; k++)
                {
                    float x = u + mv * k * MxConst.kParkStep;
                    if (x < lo + r || x > hi - r) continue;
                    float eL = float.NegativeInfinity, eR = float.PositiveInfinity;
                    bool ok = true;
                    for (int j = -1; j <= 1 && ok; j++)
                    {
                        if (!TurnBand(c, p, x + j * r, out float a, out float b)) ok = false;
                        else { eL = math.max(eL, a); eR = math.min(eR, b); }
                    }
                    if (!ok || eR - eL < 2f * S) continue;
                    tu = x; latC = 0.5f * (eL + eR); R = r;
                    return true;
                }
            }
            return false;
        }

        // The exit lies behind the nose further than 2 x kMaxReverseLeg, no K-turn of any size fits the road and the end ahead is
        // another chain end with no working machine on the way: the leaver drives on to that end (a dead end: removed there)
        // instead of reversing hundreds of metres. Otherwise `end` unchanged.
        public static float NoTurnAhead(PlanContext c, Puppet p, float u, float lat, sbyte facing, float end, ref string why)
        {
            if (p.IsRoller || c.CanTurn || c.Track == null || float.IsNaN(end)) return end;
            float dist = math.abs(end - u);
            sbyte mv = end >= u ? (sbyte)1 : (sbyte)-1;
            if (mv == facing || dist <= 2f * RRWConst.kMaxReverseLeg) return end;
            if (LeaveTurnFit(c, p, u, mv, out _, out _, out _)) return end;
            float ahead = facing >= 0 ? c.Trim1 : c.Trim0;
            if (math.abs(ahead - end) < 0.5f) return end;
            if (CrewOnRoute(c, p, u, lat, ahead) != null) return end;
            MachineDebug.NoTurnAhead++;
            why += "; no room to turn (exit " + RRWLog.F(dist) + " m behind): the end ahead";
            return ahead;
        }
    }
}
