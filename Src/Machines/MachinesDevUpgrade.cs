#if DEVTOOLS
using System.Collections.Generic;
using System.Text;
using RealisticRoadWorks.Dev;
using Unity.Mathematics;

// Machines dev command for upgrade works (mode H): rrw.mx.uw. Read only.
namespace RealisticRoadWorks.V3.Machines
{
    // rrw.mx.uw [p<id>] [check=1]: per mode H project the layout window and its primitive; per crew its band, the band's
    // equivalent phase and front (the job front against Core's band front), its stretch of the chain, the machine-safe run at the
    // front and the driveway keep-outs on its side; per puppet its box against the run there, keep-out and slew. check=1 adds
    // the mode H invariant lines (the same as rrw.check). One line each.
    public sealed class MxUpgradeCommand : IDevCommand
    {
        public string Name => "rrw.mx.uw";
        public string Help => "rrw.mx.uw [p<id>] [check=1] - mode H machines: window / primitive, per crew band (phase, front, stretch, machine-safe run at the front, keep-outs), per puppet box vs run, keep-out, slew; check=1 adds the invariant lines";

        private static readonly PlanContext s_Ctx = new PlanContext();

        public void Run(DevContext ctx, string[] a)
        {
            uint only = 0;
            bool check = false;
            foreach (var s in a)
            {
                if (s.Length > 1 && s[0] == 'p') uint.TryParse(s.Substring(1), out only);
                else if (s == "check=1") check = true;
            }
            int projects = 0;
            foreach (var pr in SiteRegistry.Projects.Values)
            {
                if (only != 0 && pr.Id != only) continue;
                if (!MachineDirectorSystem.UwProject(pr)) continue;
                projects++;
                var v = pr.View();
                var u = v.Upgrade;
                ctx.Log("rrw mx uw p" + pr.Id + " window=" + u.Window + "/" + u.WindowCount + " layout=" + u.LayoutWindow + " traffic=" + u.Traffic +
                        " applied=" + u.AppliedWindow + ":" + u.AppliedTraffic + (u.Vacating ? "(vacating)" : "") +
                        " reason=" + u.Reason + " zones=" + RoadZoneMath.Describe(u.Zones) + " appliedZones=" + RoadZoneMath.Describe(u.AppliedZones) +
                        " ready=" + RoadZoneMath.Describe(v.WorkZonesReady) +
                        " machineSafe=" + RoadZoneMath.Describe(u.MachineSafe) + " allAtOnce=" + u.AllAtOnce + " switch=" + pr.Switch +
                        " crews=" + v.CrewCount + " allow=" + pr.AllowMachines + " phase=" + pr.Phase + " p=" + MxDev.F(pr.Progress) +
                        " machineZones=" + RoadZoneMath.Describe(pr.MachineZones));
                if (!MachineTrackStore.TryGet(pr.Id, out var td) || td == null || !td.Valid) { ctx.Log("rrw mx uw p" + pr.Id + " no machine track"); continue; }
                for (int k = 0; k < v.CrewCount; k++)
                {
                    var c = MachineDirectorSystem.UwReadContext(pr, td, k, s_Ctx);
                    var cp = c.Crew;
                    int band = cp.Band;
                    var sb = new StringBuilder();
                    sb.Append("rrw mx uw p").Append(pr.Id).Append("/c").Append(k).Append(" band=").Append(band);
                    if (band >= 0 && band < u.BandCount) sb.Append(' ').Append(u.Band(band).ToString());
                    float F = c.FrontAt(c.Now), Fc = PhasePlan.CrewFront(v, k);
                    sb.Append(" metres=").Append(c.LateralMetres).Append(" small=").Append(cp.SmallMachines)
                      .Append(" phase=").Append(c.View.Phase).Append(" f=").Append(MxDev.F(c.View.F))
                      .Append(" front=").Append(MxDev.F(F)).Append(" coreFront=").Append(MxDev.F(Fc))
                      .Append(math.abs(F - Fc) > 0.05f ? " FRONT-MISMATCH" : "")
                      .Append(c.Front.C4Stage != 0 ? " paintHalf=" + c.Front.C4Stage : "")
                      .Append(" v=").Append(MxDev.F(c.FrontSpeed)).Append(" ready=").Append(c.ReadyOk)
                      .Append(c.NoSpawn ? " noSpawn(" + c.NoSpawnWhy + ")" : "");
                    if (c.LateralMetres)
                    {
                        if (Choreo.UwStretchOf(c, null, out float s0, out float s1)) sb.Append(" stretch=[").Append(MxDev.F(s0)).Append(',').Append(MxDev.F(s1)).Append(']');
                        float Fs = F;
                        if (Choreo.UwStretchOf(c, null, out s0, out s1)) Fs = math.clamp(F, s0, s1);
                        int ei = Choreo.UwEdgeAt(td, Fs);
                        if (ei >= 0 && Choreo.UwRunAt(c, ei, band, false, out var r))
                            sb.Append(" run@front=[").Append(MxDev.F(r.Lo)).Append('+').Append(MxDev.F(r.ClrLo)).Append(',').Append(MxDev.F(r.Hi)).Append('-').Append(MxDev.F(r.ClrHi))
                              .Append(']').Append(r.Drop ? " drop" : "");
                        else sb.Append(" run@front=none");
                        sb.Append(" keepOutsOnSide=").Append(Choreo.UwKeepOutsOnSide(c, band));
                    }
                    sb.Append(" roles:");
                    for (int r = 0; r < (int)MachineRole.Count; r++)
                    {
                        var slot = cp.Get((MachineRole)r);
                        if (slot.Active) sb.Append(' ').Append((MachineRole)r).Append('=').Append(slot.Activity).Append('@').Append(MxDev.F(slot.U)).Append('/').Append(MxDev.F(slot.Lateral));
                    }
                    if (cp.Rollers > 0) sb.Append(" rollers=").Append(cp.Rollers);
                    ctx.Log(sb.ToString());
                }
                double now = MxDev.Now();
                foreach (var p in MachineRegistry.All)
                {
                    if (p.ProjectId != pr.Id) continue;
                    MachineMotion.State(p.Plan, MachineRegistry.Clock, now, out var s);
                    var sb = new StringBuilder();
                    sb.Append("rrw mx uw   ").Append(p).Append(" band=").Append(p.UwBand).Append(p.PostSite ? " LEAVING(" + p.LeaveWhy + ")" : " " + p.Activity)
                      .Append(" u=").Append(MxDev.F(s.U)).Append(" lat=").Append(MxDev.F(s.Lat)).Append(" v=").Append(MxDev.F(s.Speed))
                      .Append(" leg=").Append((LegKind)s.LegKind).Append(" scale=").Append(MxDev.F(p.Scale))
                      .Append(" halfWid=").Append(MxDev.F(Choreo.UwHalfWidth(p))).Append(" zones=").Append(RoadZoneMath.Describe(p.ZonesNow));
                    if (p.UwDropStretch) sb.Append(" dropStretch");
                    if (p.UwBand >= 0)
                    {
                        int ei = Choreo.UwEdgeAt(td, s.U);
                        float hl = p.BoxHalfLen + math.abs(p.BoxOffZ);
                        if (ei >= 0 && Choreo.UwRunCore(pr, v, td, ei, p.UwBand, Choreo.UwNearNode(td, ei, s.U - hl, s.U + hl), out var r))
                        {
                            float hw = Choreo.UwHalfWidth(p);
                            sb.Append(" box=[").Append(MxDev.F(s.Lat - hw)).Append(',').Append(MxDev.F(s.Lat + hw)).Append("] run=[").Append(MxDev.F(r.Lo)).Append(',').Append(MxDev.F(r.Hi)).Append(']')
                              .Append(s.Lat - hw < r.Lo + r.ClrLo - 0.02f || s.Lat + hw > r.Hi - r.ClrHi + 0.02f ? " CLEARANCE" : "");
                        }
                        else sb.Append(" run=NONE");
                        if (Choreo.UwInKeepOut(td, s.U, s.Lat, hl)) sb.Append(math.abs(s.Speed) < 0.05f ? " IN-KEEP-OUT(standing)" : " keepOut(moving)");
                    }
                    if (p.Kind == MachineKind.Excavator) sb.Append(" slew=").Append(MxDev.F(MachineChecks.CycleSlew(p)));
                    if (p.Dig != null && p.Dig.Cur != null) sb.Append(" dig=").Append(p.Dig.LastWhy);
                    ctx.Log(sb.ToString());
                }
            }
            ctx.Log("rrw mx uw projects=" + projects + " " + MxUpgradeStats.Summary());
            if (check)
            {
                var problems = new List<string>();
                MachineChecks.RoundUpgrade(ctx.EntityManager, problems);
                ctx.Log(problems.Count == 0 ? "rrw mx uw check ok" : "rrw mx uw check FAIL: " + string.Join("; ", problems));
            }
        }
    }
}
#endif
