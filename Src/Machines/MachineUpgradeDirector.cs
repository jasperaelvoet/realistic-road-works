using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

// Upgrade works (mode H) in the machine director. A mode H project gets machines only with its upgrade data (a HalfWidth
// project without it gets none: fails closed). Its crews are the bands of the layout window (PhasePlan.Crew); a band crew plans
// in metres inside its band's machine-safe run (Choreo.UwLatRange), follows its band's own front (UwFrontOf) and runs the roster
// of its band's equivalent phase (ProjectView.Phase / F of the crew's context). When the layout window changes, the puppets of the
// old band leave along it and the new band's crew starts at its gate; a puppet whose box is no longer machine-safe (a lane
// intrusion, the window falling back to the slow zone) vacates the band at once.
namespace RealisticRoadWorks.V3.Machines
{
    public partial class MachineDirectorSystem
    {
        // A mode H project with its upgrade data.
        internal static bool UwProject(ProjectRecord pr) =>
            pr != null && pr.Mode == VisualMode.HalfWidth && pr.Upgrade != null && pr.Upgrade.Schedule.N > 0;

        // The project's mode brings machines: a new / replaced road (FullDig) or upgrade works with their upgrade data.
        internal static bool ModeHasMachines(ProjectRecord pr) =>
            pr.Mode == VisualMode.FullDig || (PhasePlan.HasMachines(pr.Mode) && UwProject(pr));

        // Front model of crew `crew` of a mode H view: its band's equivalent phase mapped onto project progress (the band's own
        // progress runs from G0 to 1 linearly over its window), swept over the trimmed chain like PhasePlan.UpgradeBandFront; the
        // re-marking band under Half uses the C4 fronts over [0, U]. Teardown (no band): the chain end.
        private static FrontRef UwFrontOf(ProjectRecord pr, in ProjectView v, int crew)
        {
            var u = v.Upgrade;
            var fr = new FrontRef { Model = pr.Model, Crews = 1, Crew = 0, Upgrade = 1 };
            float t0 = v.Trim0, t1 = v.Trim1 > t0 ? v.Trim1 : math.max(t0, v.U);
            int band = u.CrewBand(crew);
            if (band < 0) band = u.LeadBand(u.LayoutWindow);
            if (band < 0 || u.InTeardown)
            {
                fr.Phase = (byte)WorksPhase.Complete;
                fr.Base = t1;
                return fr;
            }
            var b = u.Band(band);
            PhasePlan.UpgradeBandPhase(v, band, out var ph, out _, out _);
            PhasePlan.UpgradePhaseRange(b.Kind, u.RemarkFollows(b.Window), ph, out float ga, out float gb, u.Gravel);
            float p0 = u.Schedule.P0(b.Window), p1 = u.Schedule.P1(b.Window);
            float g0 = math.saturate(b.G0), span = 1f - g0;
            fr.Phase = (byte)ph;
            fr.PMin = p0;
            fr.PStart = span > 1e-3f ? p0 + (ga - g0) / span * (p1 - p0) : p0;
            fr.PEnd = span > 1e-3f ? p0 + (gb - g0) / span * (p1 - p0) : p0;
            if (b.Kind == BandKind.Remark && b.Window == u.LayoutWindow && v.HalvesActive)
            {
                bool swap = v.SwapActive;
                fr.Base = 0f;
                fr.U = v.U;
                fr.Swap = swap ? (byte)1 : (byte)0;
                fr.Floor = !swap ? math.max(0f, v.Ctx.FrontFloor) : 0f;
                // the half the stage paints now (the Director's swap point, not the progress model's f)
                if (swap) fr.C4Stage = PhasePlan.StageIndexOf(v) == 0 ? (byte)1 : (byte)2;
            }
            else
            {
                fr.Base = t0;
                fr.U = math.max(0f, t1 - t0);
            }
            return fr;
        }

        // The crew of a mode H context: band crews plan in metres in their band, with the band's phase as the context phase; no
        // narrow-site rules and no K-turns (a band is one lane wide for the machines). Spawns and new plans need a machine-safe run
        // at the crew's front.
        private static void UwCrewPolicy(PlanContext c)
        {
            var v = c.ViewBase;
            var u = v.Upgrade;
            int band = c.Crew.Band;
            // the re-marking band is a band crew too while it paints on the move (Paint: the painter keeps to its rolling closure)
            bool bandCrew = c.Crew.LateralMetres && band >= 0 && band < u.BandCount
                            && (u.Band(band).Kind != BandKind.Remark || u.Prim(u.Band(band).Window) == BandTraffic.Paint);
            c.LateralMetres = bandCrew;
            c.UwBand = bandCrew ? band : -1;
            c.View = v;
            c.ReadyOk = c.LaneReadyOk; c.NoSpawn = c.LaneNoSpawn; c.NoSpawnWhy = c.LaneNoSpawnWhy ?? "";
            c.Narrow = c.LaneNarrow; c.CanTurn = c.LaneCanTurn;
            if (band >= 0 && band < u.BandCount && PhasePlan.UpgradeBandPhase(v, band, out var ph, out float f, out _))
            {
                c.View.Phase = ph;
                c.View.F = f;
            }
            if (!bandCrew) return;
            c.Narrow = false;
            c.CanTurn = false;
            bool sw = c.Project.Switch == StageSwitch.Vacate || c.Project.Switch == StageSwitch.Swap || c.Project.Switch == StageSwitch.Drain;
            c.ReadyOk = UwRunAtFront(c);
            c.NoSpawn = sw || !c.ReadyOk;
            c.NoSpawnWhy = sw ? "stage switch (window change)" : c.ReadyOk ? "" : "no machine-safe part of the band at the crew front";
        }

        // A machine-safe run of the crew's band exists at its front (clamped into the crew's stretch).
        private static bool UwRunAtFront(PlanContext c)
        {
            var bv = c.View.Upgrade.Band(c.UwBand);
            if (!(bv.SafeHi > bv.SafeLo) || c.Track == null || !c.Track.Valid) return false;
            float F = c.FrontAt(c.Now);
            if (Choreo.UwStretchOf(c, null, out float s0, out float s1)) F = math.clamp(F, s0, s1);
            int ei = Choreo.UwEdgeAt(c.Track, F);
            return ei >= 0 && Choreo.UwRunAt(c, ei, c.UwBand, false, out _);
        }

        // Window switches: a crew whose band changed (a new layout window) sends its puppets off along their old band; the crew
        // starts again (dig grid, spawns at the new band's gate).
        private void UwCrewBands(EntityManager em, PlanContext c, MachineProjectState st, double now, int n)
        {
            var u = c.ViewBase.Upgrade;
            for (int k = 0; k < st.Crews.Length; k++)
            {
                var cs = st.Crews[k];
                int band = k < n ? u.CrewBand(k) : -1;
                int key = UwBandKey(u, band);
                if (cs.UwBand == band && cs.UwBandKey == key) continue;
                int old = cs.UwBand;
                m_Scratch.Clear();
                for (int r = 0; r < cs.Roles.Length; r++) if (cs.Roles[r] != null) m_Scratch.Add(cs.Roles[r]);
                for (int r = 0; r < cs.Rollers.Length; r++) if (cs.Rollers[r] != null) m_Scratch.Add(cs.Rollers[r]);
                int left = m_Scratch.Count;
                foreach (var p in m_Scratch)
                {
                    SetCrew(c, math.clamp(k, 0, n - 1), st);
                    MakePostSite(em, p, c, now, "window switch: band " + old + " done");
                }
                m_Scratch.Clear();
                cs.ResetGrid();
                cs.AnchorMask = 0;
                cs.RollerAnchorMask = 0;
                cs.UwBand = band;
                cs.UwBandKey = key;
                if (left > 0)
                {
                    MxUpgradeStats.WindowLeaves += left;
                    RRWLog.Info("machines: project #" + c.Project.Id + " crew " + k + " moves from band " + old + " to band " + band + " (window " + u.Window +
                                "): " + left + " machine(s) leave along their band");
                }
            }
        }

        // Identity of chain band `band` (0 = none).
        private static int UwBandKey(in UpgradeView u, int band)
        {
            if (band < 0 || band >= u.BandCount) return 0;
            var b = u.Band(band);
            unchecked
            {
                int h = 17 + (int)b.Kind * 31 + (int)b.Side * 131 + b.Window * 1009;
                h = h * 7919 + (int)math.round(b.Lo * 16f);
                h = h * 7919 + (int)math.round(b.Hi * 16f);
                return h == 0 ? 1 : h;
            }
        }

        // Every live puppet of a band crew whose box no longer lies inside its band's machine-safe run (a vehicle on a dropped lane,
        // the window primitive weakened, the group reopened) leaves at once along its lane; the crew spawns again once the band is
        // machine-safe (two clean drain checks for a lane drop).
        private void UwVacate(EntityManager em, PlanContext c, MachineProjectState st, double now, int n)
        {
            for (int k = 0; k < n && k < st.Crews.Length; k++)
            {
                var cs = st.Crews[k];
                if (cs.UwBand < 0 || !cs.Any()) continue;
                SetCrew(c, k, st);
                if (!c.LateralMetres) continue;
                m_Scratch.Clear();
                for (int r = 0; r < cs.Roles.Length; r++) if (cs.Roles[r] != null) m_Scratch.Add(cs.Roles[r]);
                for (int r = 0; r < cs.Rollers.Length; r++) if (cs.Rollers[r] != null) m_Scratch.Add(cs.Rollers[r]);
                foreach (var p in m_Scratch)
                {
                    if (p.PostSite || p.Plan.Count <= 0 || p.UwBand < 0) continue;
                    if (UwBoxSafe(c, p, now, out string why)) continue;
                    MxUpgradeStats.Vacates++;
                    MxUpgradeStats.LastVacate = p + " " + why;
                    RRWLog.Info("machines: " + p + " vacates its band: " + why);
                    MakePostSite(em, p, c, now, "vacate: " + why);
                }
                m_Scratch.Clear();
            }
        }

        // The box of p now lies inside its band's machine-safe run (kUwVacateTol of slack). why: what is wrong otherwise.
        private static bool UwBoxSafe(PlanContext c, Puppet p, double now, out string why)
        {
            why = "";
            MachineMotion.State(p.Plan, c.Clk, now, out var s);
            float2 h = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
            float hw = Choreo.UwHalfWidth(p), hl = p.BoxHalfLen;
            float cu = s.U + h.x * p.BoxOffZ, cl = s.Lat + h.y * p.BoxOffZ;
            float eu = math.abs(h.x) * hl + math.abs(h.y) * hw, el = math.abs(h.y) * hl + math.abs(h.x) * hw;
            int ei = Choreo.UwEdgeAt(c.Track, cu);
            if (ei < 0) { why = "no track"; return false; }
            if (!Choreo.UwRunAt(c, ei, p.UwBand, Choreo.UwNearNode(c.Track, ei, cu - eu, cu + eu), out var r))
            {
                why = "no machine-safe run of band " + p.UwBand + " at u=" + RRWLog.F(cu);
                return false;
            }
            if (cl - el < r.Lo - MxConst.kUwVacateTol || cl + el > r.Hi + MxConst.kUwVacateTol)
            {
                why = "box [" + RRWLog.F(cl - el) + "," + RRWLog.F(cl + el) + "] outside the machine-safe run [" + RRWLog.F(r.Lo) + "," + RRWLog.F(r.Hi) + "] at u=" + RRWLog.F(cu);
                return false;
            }
            return true;
        }

        // A standing box of p at (u, lat) for a spawn: inside the band's machine-safe run with its clearances, outside the driveway
        // keep-outs.
        private static bool UwSpawnBoxOk(PlanContext c, Puppet p, float u, float lat)
        {
            if (!Choreo.LatRange(c, p, u, out float a, out float b)) return false;
            if (lat < a - 0.02f || lat > b + 0.02f) return false;
            return !Choreo.UwInKeepOut(c, p, u, lat);
        }

        // A planning context of crew `crew` of a mode H project for read-only use (dev commands): its band, phase and front.
        internal static PlanContext UwReadContext(ProjectRecord pr, TrackData td, int crew, PlanContext c)
        {
            var v = pr.View();
            c.Em = default;
            c.Project = pr;
            c.View = c.ViewBase = v;
            c.Upgrade = v.IsUpgrade;
            c.Track = td;
            c.Tv = MachineTrackStore.View();
            c.Clk = MachineRegistry.Clock;
            c.Now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            c.Trim0 = v.Trim0;
            c.Trim1 = v.Trim1;
            c.State = null;
            c.CS = null;
            int n = v.CrewCount;
            c.CrewIndex = math.clamp(crew, 0, n - 1);
            c.Crews = n;
            c.Crew = PhasePlan.Crew(v, c.CrewIndex);
            c.SecLo = c.Crew.SecLo;
            c.SecHi = c.Crew.SecHi;
            if (!(c.SecHi > c.SecLo)) { c.SecLo = c.Trim0; c.SecHi = c.Trim1; }
            c.Front = v.IsUpgrade ? UwFrontOf(pr, v, c.CrewIndex) : default;
            c.FrontSpeed = MachineMotion.FrontSpeed(c.Front, c.Clk, c.Now);
            c.LaneReadyOk = true; c.LaneNoSpawn = false; c.LaneNoSpawnWhy = "";
            c.LaneNarrow = false; c.LaneCanTurn = false;
            c.LateralMetres = false;
            c.UwBand = -1;
            if (c.Upgrade) UwCrewPolicy(c);
            return c;
        }

        // Re-plan key of a band crew's role: the intent key plus the excavator's driveway keep-out clamp state.
        private static int RoleKey(PlanContext c, Puppet p, MachineRole role, CrewSlot slot)
        {
            int key = IntentKey(role, slot, c);
            if (!c.LateralMetres || p == null || (slot.Activity != MachineActivity.Dig && slot.Activity != MachineActivity.Break)) return key;
            float lo = float.NegativeInfinity, hi = float.PositiveInfinity;
            int ko = Choreo.UwDigClamp(c, p, slot.U - c.FrontAt(c.Now), slot.Lateral, ref lo, ref hi);
            return ko < 0 ? key : key * 31 + 7 + ko;
        }

        // A leaver of a band crew that stops at the end of its band's stretch (not at a connected chain end) fades there like a
        // dead-end leaver (it never drives on through the approach zone of a lane drop).
        private static void UwLeaveEnd(Puppet p, PlanContext c, float end)
        {
            if (!c.Upgrade || p.UwBand < 0 || p.DeadEndLeave) return;
            float stop = Choreo.ClampU(c, p, end);
            if (math.abs(stop - end) <= MxConst.kExitReach) return;
            p.DeadEndLeave = true;
            p.DeadEndWhy = "end of the band's machine-safe stretch u=" + RRWLog.F(stop);
        }
    }

    public static partial class MachineChecks
    {
        private static readonly PlanContext s_UwCtx = new PlanContext();

        // Mode H invariants (rrw.check and rrw.mx.check lines that must stay 0), for every working puppet of a band crew:
        //  * its box (the excavator: the house swept by the front-dump slew) outside its band's machine-safe run;
        //  * its box within kUwBandMachineClear of an open lane edge (kUwFenceClear of a fence side) - the run end clearances;
        //  * standing (a hold, or the stationary part of a dig cycle) inside a driveway keep-out on its side;
        //  * an excavator whose dig cycle slews beyond RRWConst.kUwSlewLimitDeg.
        public static void RoundUpgrade(EntityManager em, List<string> problems)
        {
            double now = MachineRegistry.Clock.Tau(RRWClock.RenderFrame, RRWClock.RenderFrameTime);
            int outside = 0, close = 0, keepOut = 0, slew = 0;
            string fOut = "", fClose = "", fKeep = "", fSlew = "";
            var c = s_UwCtx;
            c.Clk = MachineRegistry.Clock;
            c.Tv = MachineTrackStore.View();
            c.Now = now;
            foreach (var p in MachineRegistry.All)
            {
                if (!p.Upgrade || p.UwBand < 0 || p.PostSite || p.Outgoing || p.Plan.Count <= 0 || !p.Alive(em)) continue;
                if (!SiteRegistry.TryGetProject(p.ProjectId, out var pr) || !MachineDirectorSystem.UwProject(pr)) continue;
                if (!MachineTrackStore.TryGet(pr.Id, out var td) || td == null || !td.Valid) continue;
                c.Project = pr;
                c.View = c.ViewBase = pr.View();
                c.Track = td;
                c.Upgrade = c.View.IsUpgrade;
                c.LateralMetres = false;
                c.UwBand = -1;
                if (!c.Upgrade) continue;
                MachineMotion.State(p.Plan, c.Clk, now, out var s);
                float2 h = math.normalizesafe(new float2(s.Hu, s.Hl), new float2(1f, 0f));
                float hw = Choreo.UwHalfWidth(p), hl = p.BoxHalfLen;
                float cu = s.U + h.x * p.BoxOffZ, cl = s.Lat + h.y * p.BoxOffZ;
                float eu = math.abs(h.x) * hl + math.abs(h.y) * hw, el = math.abs(h.y) * hl + math.abs(h.x) * hw;
                int ei = Choreo.UwEdgeAt(td, cu);
                if (ei < 0 || !Choreo.UwRunCore(pr, c.View, td, ei, p.UwBand, Choreo.UwNearNode(td, ei, cu - eu, cu + eu), out var r))
                {
                    // the band has no machine-safe run under it: counted as outside unless the vacate runs this update
                    if (outside++ == 0) fOut = p + " band " + p.UwBand + " u=" + RRWLog.F(cu) + ": no machine-safe run";
                    continue;
                }
                if (cl - el < r.Lo - MxConst.kUwVacateTol || cl + el > r.Hi + MxConst.kUwVacateTol)
                { if (outside++ == 0) fOut = p + " box [" + RRWLog.F(cl - el) + "," + RRWLog.F(cl + el) + "] run [" + RRWLog.F(r.Lo) + "," + RRWLog.F(r.Hi) + "] u=" + RRWLog.F(cu); }
                else if (cl - el < r.Lo + r.ClrLo - 0.02f || cl + el > r.Hi - r.ClrHi + 0.02f)
                { if (close++ == 0) fClose = p + " box [" + RRWLog.F(cl - el) + "," + RRWLog.F(cl + el) + "] run [" + RRWLog.F(r.Lo) + "+" + RRWLog.F(r.ClrLo) + "," + RRWLog.F(r.Hi) + "-" + RRWLog.F(r.ClrHi) + "] u=" + RRWLog.F(cu); }
                if (Standing(p, s) && Choreo.UwInKeepOut(td, s.U, s.Lat, p.BoxHalfLen + math.abs(p.BoxOffZ)))
                { if (keepOut++ == 0) fKeep = p + " u=" + RRWLog.F(s.U) + " lat=" + RRWLog.F(s.Lat) + " leg=" + (LegKind)s.LegKind; }
                if (p.Kind == MachineKind.Excavator)
                {
                    float sl = CycleSlew(p);
                    if (sl > RRWConst.kUwSlewLimitDeg + 0.5f) { if (slew++ == 0) fSlew = p + " slew=" + RRWLog.F(sl); }
                }
            }
            if (outside > 0) problems.Add("machines: " + outside + " mode H puppets with a box outside their band's machine-safe run (first: " + fOut + ")");
            if (close > 0) problems.Add("machines: " + close + " mode H puppets closer than the band clearance to an open lane / fence side (first: " + fClose + ")");
            if (keepOut > 0) problems.Add("machines: " + keepOut + " mode H puppets standing inside a driveway keep-out (first: " + fKeep + ")");
            if (slew > 0) problems.Add("machines: " + slew + " mode H excavators slewing beyond " + RRWLog.F(RRWConst.kUwSlewLimitDeg) + " deg (first: " + fSlew + ")");
        }

        // Standing now: a hold of at least 3 s (a roller's reversing pause is not one), or a dig / load cycle in its stationary part.
        private static bool Standing(Puppet p, in ChainState s)
        {
            if (math.abs(s.Speed) >= 0.05f) return false;
            if (s.LegKind == (byte)LegKind.DigHop) return true;
            if (s.LegKind != (byte)LegKind.Hold || s.Leg < 0 || s.Leg >= p.Plan.Count) return false;
            var l = p.Plan.Get(s.Leg);
            return l.OpenEnded || l.T1 - l.T0 >= 3f;
        }

        // Largest |slew| the excavator's current dig cycle uses: the dump slew of its IK keys, else the keyframe cycle's SlewDeg.
        internal static float CycleSlew(Puppet p)
        {
            float m = 0f;
            if (p.Dig != null && p.Dig.Cur != null) m = math.max(m, math.abs(p.Dig.Cur.DumpSlew));
            if (p.Dig != null && p.Dig.Next != null) m = math.max(m, math.abs(p.Dig.Next.DumpSlew));
            if (p.Dig == null || p.Dig.Cur == null)
            {
                if (p.Plan.Count > 0)
                {
                    var l = p.Plan.Get(p.Plan.Count - 1);
                    if (l.Kind == (byte)LegKind.DigHop && l.Anim == (byte)AnimKind.Dig) m = math.max(m, p.Plan.SlewDeg);
                }
            }
            return m;
        }
    }
}
