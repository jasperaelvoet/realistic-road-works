using System;
using System.Collections.Generic;
using Game.Objects;
using Unity.Entities;
using Unity.Mathematics;

// The roller roster of one crew (CrewPlan.Roller0 / Roller1), processed after every
// puppet role (lowest budget priority: front owner -> TruckA -> the rest -> Roller0 -> Roller1), plus spawns (out of view at the
// anchor; in view: a relay of a leaving roller of the project, else the connected chain gate for crew 0, else a drive-in from the
// nearest out-of-view spot behind the anchor, else wait - never a pop-in in view; after a load the relay / parked slots spawn at their
// anchor), the hand-over matching (role by role, like the puppets) and the roller leave rules.
// A roller is a Puppet with IsRoller and no entity: ValidatePuppets drops it once its GameObject is gone.
namespace RealisticRoadWorks.V3.Machines
{
    public partial class MachineDirectorSystem
    {
        public static int RollerSpawns, RollerRelays, RollerGateSpawns, RollerInViewSpawns, RollerDefers, RollerLeaves, RollerDriveIns;
        private readonly List<Puppet> m_RollerScratch = new List<Puppet>(8);

        private static int RollersLive()
        {
            int n = 0;
            var all = MachineRegistry.All;
            for (int i = 0; i < all.Count; i++) if (all[i].IsRoller) n++;
            return n;
        }

        private static int RollerIntentKey(in RollerSlot rs, PlanContext c)
        {
            var s = rs.Slot;
            int h = (int)s.Activity * 7919 + (int)rs.Duty * 131 + (rs.RelayOnly ? 17 : 0) + (rs.WorksHalfOnly ? 29 : 0) + s.Facing * 3;
            if (s.Activity == MachineActivity.Parked || s.Activity == MachineActivity.DriveOut) h = h * 31 + (int)math.round(s.U);
            h = h * 31 + (c.Working ? 1 : 0);
            return h;
        }

        // The rollers of the crew selected in c (after the puppet roles; `working` = the crew's budget use so far).
        private void ProcessRollers(EntityManager em, PlanContext c, MachineProjectState st, ProjectRecord pr, TrackData td, RRWSetting settings,
                                    double now, int budget, ref int working, bool allowed, bool force0)
        {
            var cs = c.CS;
            for (int i = 0; i < RRWConst.kMaxRollersPerCrew; i++)
            {
                var rs = c.Crew.Roller(i);
                var p = cs.Rollers[i];
                bool want = rs.Active && settings.RollersOn && !RollerRenderSystem.Faulted;
                if (!want)
                {
                    cs.RollerDeferSince[i] = double.NaN;
                    if (p != null)
                    {
                        RollerLeaves++;
                        MakePostSite(em, p, c, now, !settings.RollersOn ? "rollers off" : "roller slot ended (" + c.View.Phase + ")");
                    }
                    continue;
                }
                if (rs.Slot.Activity == MachineActivity.DriveOut)
                {
                    cs.RollerDeferSince[i] = double.NaN;
                    if (p != null) { RollerLeaves++; MakePostSite(em, p, c, now, "roller drive-out (" + c.View.Phase + ")", rs.Slot.U); }
                    continue;
                }
                if (p == null)
                {
                    // never spawned for a relay slot or a parked hand-off spot: those take live rollers only (hand-over / late relay) -
                    // except in the update after a load / rebuild / ModelReset (SpawnAtAnchor, like the puppets): the relay
                    // roller is respawned at its hand-off spot, so the relay chain of the interior crews survives a load
                    if ((rs.RelayOnly || rs.Slot.Activity != MachineActivity.Compact) && !c.State.SpawnAtAnchor) continue;
                    if (!allowed || working >= budget) continue;
                    if (!cs.SpawnOk) { Gate(st, MachineRole.Roller, "crew LOD (far crew or > kMaxActiveCrewsGlobal crews)", null); continue; }
                    if (c.NoSpawn) { Gate(st, MachineRole.Roller, c.NoSpawnWhy, null); continue; }
                    if (c.HalvesActive) continue;
                    if (RollersLive() >= RRWConst.kMaxRollersGlobal) { Gate(st, MachineRole.Roller, "kMaxRollersGlobal", null); continue; }
                    if (CountLive() >= RRWConst.kMaxMachinesGlobal && !EvictOne(em)) continue;
                    MxPerf.Begin(MxT.B_Spawn);
                    p = SpawnRoller(em, c, rs, i, now, settings);
                    MxPerf.End(MxT.B_Spawn);
                    if (p != null) working++;
                    continue;
                }
                cs.RollerDeferSince[i] = double.NaN;
                p.Outgoing = false;
                working++;
                if (!c.ReadyOk) { Gate(st, MachineRole.Roller, "re-plan held: ", c.NoSpawnWhy); continue; }
                bool frontModel = p.Plan.FrontA.Swap != c.Front.Swap || p.Plan.FrontA.Floor != c.Front.Floor ||
                                  p.Plan.FrontA.Crews != c.Front.Crews || p.Plan.FrontA.Crew != c.Front.Crew ||
                                  (c.Upgrade && !p.Plan.FrontA.SameUpgradeBand(c.Front));   // mode H: band phase changed
                int key = RollerIntentKey(rs, c);
                // (the crew's phase: the project phase, for mode H band crews their band's equivalent phase)
                bool force = force0 || p.PlannedTrackRevision != td.Revision || p.PlannedPhase != c.View.Phase || frontModel || key != p.IntentKey;
                if (force || now >= p.ReplanAt)
                {
                    var saved = p.Plan;
                    bool held = p.GuardHeld;
                    p.RollerIndex = i;
                    Choreo.PlanRoller(p, c, rs);
                    if (held) ReleaseCheckTimed(p, c, saved, force);
                }
                p.IntentKey = key;
                p.Activity = rs.Slot.Activity;
                p.Duty = rs.Duty;
            }
        }

        // The anchor u of a roller slot seen from the camera: within kMachineDespawnRadius and inside the camera frustum.
        private static bool RollerAnchorInView(PlanContext c, float u, float lat)
        {
            if (c.Track == null || !c.Tv.Point(c.Track.Slot, u, out var tp)) return false;
            float2 xz = tp.Pos.xz + tp.Right * lat;
            var w = new float3(xz.x, tp.Pos.y, xz.y);
            if (math.any(math.isnan(c.Cam))) return false;
            if (math.distance(w.xz, c.Cam.xz) > RRWConst.kMachineDespawnRadius) return false;
            return RollerView.InFrustum(w);
        }

        private Puppet SpawnRoller(EntityManager em, PlanContext c, in RollerSlot rs, int idx, double now, RRWSetting settings)
        {
            var cs = c.CS;
            int bit = 1 << idx;
            float anchor = math.clamp(rs.Slot.U, c.Trim0, c.Trim1);
            var probe = new Puppet { Kind = MachineKind.Roller, Role = MachineRole.Roller, IsRoller = true, RollerIndex = idx, Seed = (ushort)c.Project.Seed,
                                     Upgrade = c.Upgrade, UwBand = c.LateralMetres ? c.UwBand : -1 };
            Size(probe);
            if (c.LateralMetres) anchor = Choreo.ClampU(c, probe, anchor);
            Choreo.RollerLatRange(c, probe, anchor, rs.WorksHalfOnly, out float latLo, out float latHi);
            float lat0 = PhasePlan.RollerPassLateral((int)(probe.Seed % 7u) + idx * 3, latLo, latHi, RRWConst.kRollerPassShift);
            bool atAnchor = c.State.SpawnAtAnchor;   // after a load, rebuild or ModelReset: directly at the anchor (same rule as the puppets)
            if (MachineDebug.ForceRollerUntil > RRWClock.UpdateIndex && (MachineDebug.ForceRollerProject == 0 || MachineDebug.ForceRollerProject == c.Project.Id) &&
                MachineDebug.ForceRollerCrew == c.CrewIndex && MachineDebug.ForceRollerIndex == idx)
            {
                atAnchor = true;   // dev rrw.mx.roller spawn
                MachineDebug.ForceRollerUntil = 0;
            }
            bool inView = !atAnchor && RollerAnchorInView(c, anchor, lat0);
            float u0 = anchor;
            string how = atAnchor ? "anchor (load / rebuild / reset)" : "anchor (out of view)";
            if (inView)
            {
                // 1) a relay: a leaving roller of the project that reaches the anchor within kHandOverLateSeconds
                var relay = FindRelay(c, anchor, now);
                if (relay != null) { Revive(em, relay, c, rs, idx, now); RollerRelays++; return relay; }
                // 2) crew 0 with a connected chain start: from the gate, driving in at walking pace
                if (c.CrewIndex == 0 && c.View.ExitAtStart)
                {
                    u0 = c.Trim0 + RRWConst.kRollerEndClear + probe.BoxHalfLen;
                    // the half away from the crew truck's lane
                    var ct = cs.Roles[(int)MachineRole.CrewTruck];
                    if (ct != null && ct.Plan.Count > 0)
                    {
                        MachineMotion.State(ct.Plan, c.Clk, now, out var cts);
                        Choreo.RollerLatRange(c, probe, u0, rs.WorksHalfOnly, out float a, out float b);
                        lat0 = cts.Lat >= 0.5f * (a + b) ? a : b;
                    }
                    how = "chain gate (anchor in view)";
                    RollerGateSpawns++;
                }
                else if (DriveInSpot(c, probe, rs, anchor, lat0, out float du, out float dl))
                {
                    // 3) drive in from the nearest spot behind the anchor that is out of view (towards the
                    //    section / chain start: the finished side), at the roller's limits - never a pop-in in view
                    u0 = du;
                    lat0 = dl;
                    how = "drive-in from out of view (" + RRWLog.F(anchor - du) + " m behind the anchor in view)";
                    RollerDriveIns++;
                }
                else
                {
                    // 4) no out-of-view spot behind it: wait for the view to move (or a relay); never spawned in view
                    if (double.IsNaN(cs.RollerDeferSince[idx])) { cs.RollerDeferSince[idx] = now; RollerDefers++; }
                    Gate(c.State, MachineRole.Roller, "roller anchor in view: waiting for a relay / the view to move (no out-of-view drive-in spot)", null);
                    return null;
                }
            }
            var p = new Puppet
            {
                ProjectId = c.Project.Id, Role = MachineRole.Roller, Kind = MachineKind.Roller, IsRoller = true, RollerIndex = idx, Prefab = Entity.Null,
                Seed = (ushort)(c.Project.Seed + idx * 131 + c.CrewIndex * 977), SpawnUpdate = RRWClock.UpdateIndex, Track = c.Track,
                Crew = c.CrewIndex, LimPhase = c.View.Phase, Duty = rs.Duty,
                Upgrade = c.Upgrade, UwBand = c.LateralMetres ? c.UwBand : -1,
            };
            Size(p);
            var rnd = new Unity.Mathematics.Random((uint)(c.Project.Seed * 7919u + 104729u * 7u + (uint)c.CrewIndex * 15485863u + (uint)idx * 31u) | 1u);
            p.LightJitter = rnd.NextFloat(-0.05f, 0.05f);
            u0 = Choreo.ClampU(c, p, u0);
            if (!SpawnSpot(c, p, ref u0, lat0))
            {
                if (RRWLog.VerboseEnabled) RRWLog.Verbose("machines: roller spawn of p" + c.Project.Id + "/c" + c.CrewIndex + "/" + idx + " deferred: no free spot near u=" + RRWLog.F(u0));
                return null;
            }
            if (c.LateralMetres ? !UwSpawnBoxOk(c, p, u0, lat0) : (StandZones(c, p, u0, lat0) & RoadZones.AllLanes & ~c.Ready) != RoadZones.None)
            {
                Gate(c.State, MachineRole.Roller, c.LateralMetres ? "roller spawn box outside the band's machine-safe run" : "roller spawn box touches a not-ready group", null);
                return null;
            }
            if ((cs.RollerAnchorMask & bit) != 0) { cs.RollerAnchorMask &= ~bit; MxSpeed.HandAnchorSpawns++; }
            try { TrackBuilder.EnsureY(em, m_Terrain, c.Track, u0 - 20f, u0 + 20f); }
            catch (Exception ex) { RRWLog.ErrorOnce("machines roller spawn track y", ex); }
            var plan0 = new MachinePlan { Track = c.Track.Slot, Role = (byte)MachineRole.Roller, MKind = (byte)MachineKind.Roller, SlewDeg = 90f, FrontA = c.Front, FrontB = c.Front };
            plan0.HalfLen = RollerModel.WheelbaseHalf;
            plan0.HalfWid = RollerModel.DrumHalfW;
            plan0.FootOff = 0f;
            plan0.Epoch = now;
            plan0.Set(0, new MachineLeg { T0 = 0f, T1 = float.PositiveInfinity, Kind = (byte)LegKind.Hold, Facing = 1, U0 = u0, L0 = lat0 });
            plan0.Count = 1;
            plan0.Flags = TransformFlags.RearLights;
            p.Plan = plan0;
            p.PassK = 0;
            Choreo.PlanRoller(p, c, rs);
            p.Activity = rs.Slot.Activity;
            p.IntentKey = RollerIntentKey(rs, c);
            p.PlannedPhase = c.View.Phase;
            p.PlannedWorking = c.Working;
            p.PlannedTrackRevision = c.Track.Revision;
            MachineMotion.State(p.Plan, c.Clk, now, out var s);
            if (!MachineMotion.Pose(p.Plan, c.Tv, s, out float3 pos, out _)) return null;
            RollerWorld.Create(p, p.Seed);
            p.LastPos = pos;
            p.HasPos = true;
            cs.Rollers[idx] = p;
            cs.RollerDeferSince[idx] = double.NaN;
            MachineRegistry.Add(p);
            if (!c.Others.Contains(p)) c.Others.Add(p);   // visible to the rest of this update's planning
            MachineRegistry.Spawned++;
            RollerSpawns++;
            RRWLog.Once("machines-roller-first", "machines: first road roller unit " + p + " (" + rs.Duty + ") box=" + RRWLog.F(2f * p.BoxHalfLen) + "x" + RRWLog.F(2f * p.BoxHalfWid) +
                        " shader=" + RollerMaterials.Source);
            if (RRWLog.VerboseEnabled)
                RRWLog.Verbose("machines: spawn roller " + p + " " + rs.Duty + " at u=" + RRWLog.F(u0) + " lat=" + RRWLog.F(lat0) + " (" + how + ") window=[" +
                               RRWLog.F(rs.WinLo) + "," + RRWLog.F(rs.WinHi) + "]");
            return p;
        }

        // The nearest spot behind the anchor (10 m steps, at most kRollerDriveInSearch m, not before the chain's first
        // anchor) whose roller pivot is out of view (RollerAnchorInView false: beyond kMachineDespawnRadius or outside the frustum).
        private static bool DriveInSpot(PlanContext c, Puppet probe, in RollerSlot rs, float anchor, float latWant, out float u, out float lat)
        {
            u = anchor;
            lat = latWant;
            float lo = Choreo.AnchorLo(c, probe);
            for (int k = 1; k * 10f <= kRollerDriveInSearch; k++)
            {
                float uu = math.max(lo, anchor - 10f * k);
                Choreo.RollerLatRange(c, probe, uu, rs.WorksHalfOnly, out float a, out float b);
                float ll = a <= b ? math.clamp(latWant, a, b) : 0.5f * (a + b);
                if (c.Tv.Point(c.Track.Slot, uu, out _) && !RollerAnchorInView(c, uu, ll)) { u = uu; lat = ll; return true; }
                if (uu <= lo) break;
            }
            return false;
        }

        private const float kRollerDriveInSearch = 240f;

        // Box / limits of a roller (kRollerLength x kRollerWidth, pivot at the joint = the box centre).
        private static void Size(Puppet p)
        {
            p.BoxHalfLen = 0.5f * RRWConst.kRollerLength;
            p.BoxHalfWid = 0.5f * RRWConst.kRollerWidth;
            p.BoxOffZ = 0f;
            p.FootHalfLen = RollerModel.WheelbaseHalf;
            p.FootHalfWid = RollerModel.DrumHalfW;
            p.FootOff = 0f;
            p.FootSource = "drums";
            p.Lim = MachineLimits.Of(MachineRole.Roller, p.LimPhase);
            p.Speed = p.Lim.VFwd;
            p.Accel = p.Lim.Accel;
        }

        // A leaving roller of the project (not released / completed) that reaches u within kHandOverLateSeconds at its limits.
        private Puppet FindRelay(PlanContext c, float u, double now)
        {
            if (c.Releasing || c.Upgrade) return null;   // mode H: a leaving roller drives off along its own band
            Puppet best = null;
            float bestT = RRWConst.kHandOverLateSeconds;
            var lim = MachineLimits.Of(MachineRole.Roller, c.View.Phase);
            foreach (var q in MachineRegistry.All)
            {
                if (!q.IsRoller || q.ProjectId != c.Project.Id || !(q.PostSite || q.Outgoing) || q.Unit == null || q.Plan.Count <= 0) continue;
                MachineMotion.State(q.Plan, c.Clk, now, out var s);
                float t = MachineLimits.MinLegSeconds(u - s.U, lim.VFwd * MxConst.kSpeedMargin, lim.Accel);
                if (t <= bestT) { bestT = t; best = q; }
            }
            return best;
        }

        // A leaving roller takes the slot (relay): back on the roster of this crew, re-planned from where it is.
        private void Revive(EntityManager em, Puppet p, PlanContext c, in RollerSlot rs, int idx, double now)
        {
            p.PostSite = false;
            p.Outgoing = false;
            p.LeaveTau = double.NaN;
            p.ReleaseTau = double.NaN;
            p.LeaveSince = 0;
            p.LeaveCap = RRWConst.kPostSiteMaxSeconds;
            p.LeaveWhy = "";
            p.LeaveContinue = false;
            p.DeadEndLeave = false;
            p.LightsOff = false;
            p.LeaveEndU = float.NaN;
            p.Crew = c.CrewIndex;
            p.RollerIndex = idx;
            p.LodOut = false;
            Choreo.ClearBlocked(p);
            c.CS.Rollers[idx] = p;
            Choreo.PlanRoller(p, c, rs);
            p.Activity = rs.Slot.Activity;
            p.Duty = rs.Duty;
            p.IntentKey = RollerIntentKey(rs, c);
            RRWLog.Verbose("machines: roller " + p + " relays to crew " + c.CrewIndex + " slot " + idx + " (" + rs.Duty + ")");
        }

        // Hand-over for rollers (called from HandOver after the puppet roles): every live roller of the project is matched to the
        // roller slots of the new layout (Compact, Parked or RelayOnly) it reaches within HandOverSeconds (late relay when the anchor
        // is in view), shortest first; unmatched rollers leave; unmatched non-relay slots spawn at their anchor.
        private void HandOverRollers(EntityManager em, PlanContext c, MachineProjectState st, double now, string why, float budgetS)
        {
            var view = c.View;
            int n = view.CrewCount;
            m_RollerScratch.Clear();
            for (int k = 0; k < st.Crews.Length; k++)
            {
                var rl = st.Crews[k].Rollers;
                for (int i = 0; i < rl.Length; i++) if (rl[i] != null) { m_RollerScratch.Add(rl[i]); rl[i] = null; }
            }
            if (m_RollerScratch.Count == 0) return;
            m_HoPairs.Clear();
            int slotMask = 0;
            var lim = MachineLimits.Of(MachineRole.Roller, view.Phase);
            for (int i = 0; i < n && c.RollersOn; i++)
            {
                var plan = PhasePlan.Crew(view, i);
                for (int j = 0; j < RRWConst.kMaxRollersPerCrew; j++)
                {
                    var rs = plan.Roller(j);
                    if (!rs.Active || rs.Slot.Activity == MachineActivity.DriveOut) continue;
                    int slotId = i * RRWConst.kMaxRollersPerCrew + j;
                    slotMask |= 1 << slotId;
                    float anchor = math.clamp(rs.Slot.U, c.Trim0, c.Trim1);
                    float lateS = AnchorInView(c, anchor) ? math.max(budgetS, RRWConst.kHandOverLateSeconds) : budgetS;
                    for (int q = 0; q < m_RollerScratch.Count; q++)
                    {
                        var p = m_RollerScratch[q];
                        if (p.Plan.Count <= 0) continue;
                        if (c.Upgrade && p.Crew != i) continue;   // mode H: never across to another band
                        MachineMotion.State(p.Plan, c.Clk, now, out var s);
                        float t = MachineLimits.MinLegSeconds(anchor - s.U, lim.VFwd, lim.Accel);
                        if (t <= lateS) m_HoPairs.Add(new HoPair { P = q, Crew = slotId, T = t, Late = t > budgetS });
                    }
                }
            }
            m_HoPairs.Sort(s_HoCmp);
            int usedP = 0, usedS = 0, kept = 0, remapped = 0, left = 0;
            foreach (var hp in m_HoPairs)
            {
                if ((usedP & (1 << hp.P)) != 0 || (usedS & (1 << hp.Crew)) != 0) continue;
                usedP |= 1 << hp.P;
                usedS |= 1 << hp.Crew;
                var p = m_RollerScratch[hp.P];
                int crew = hp.Crew / RRWConst.kMaxRollersPerCrew, idx = hp.Crew % RRWConst.kMaxRollersPerCrew;
                st.Crews[crew].Rollers[idx] = p;
                if (hp.Late) MxSpeed.HandLate++;
                if (p.Crew != crew || p.RollerIndex != idx) remapped++; else kept++;
                p.Crew = crew;
                p.RollerIndex = idx;
                p.IntentKey = int.MinValue;   // re-plan (the relay drive runs at the roller's limits)
            }
            for (int q = 0; q < m_RollerScratch.Count; q++)
            {
                if ((usedP & (1 << q)) != 0) continue;
                var p = m_RollerScratch[q];
                if (slotMask == 0 && p.Crew < n && st.Crews[p.Crew].Rollers[math.clamp(p.RollerIndex, 0, RRWConst.kMaxRollersPerCrew - 1)] == null)
                {
                    st.Crews[p.Crew].Rollers[math.clamp(p.RollerIndex, 0, RRWConst.kMaxRollersPerCrew - 1)] = p;   // the roster ends it (drive-out / slot ended)
                    continue;
                }
                SetCrew(c, math.clamp(p.Crew, 0, n - 1), st);
                MakePostSite(em, p, c, now, "hand-over (" + why + "): no roller slot within " + RRWLog.F(budgetS) + " s");
                left++;
            }
            for (int s = 0; s < n * RRWConst.kMaxRollersPerCrew; s++)
            {
                if ((slotMask & (1 << s)) == 0 || (usedS & (1 << s)) != 0) continue;
                int crew = s / RRWConst.kMaxRollersPerCrew, idx = s % RRWConst.kMaxRollersPerCrew;
                if (!PhasePlan.Crew(view, crew).Roller(idx).RelayOnly) st.Crews[crew].RollerAnchorMask |= 1 << idx;
            }
            MxSpeed.HandKept += kept;
            MxSpeed.HandRemapped += remapped;
            MxSpeed.HandLeft += left;
            if (remapped + left > 0)
                RRWLog.Verbose("machines: project #" + c.Project.Id + " roller hand-over (" + why + "): kept " + kept + ", re-mapped " + remapped + ", left " + left);
            m_RollerScratch.Clear();
            m_HoPairs.Clear();
        }
    }
}
