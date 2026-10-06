using Unity.Mathematics;

// Machine report: Machines is the ONLY writer of ProjectRecord.MachineZones, MachinesOnCarriageway and
// MachinesReportUpdate. Written at the end of every MachineDirectorSystem update for EVERY registry project (None / 0 when
// the project has no puppets, is far away or machines are off), so the Director (order 210) reads a report that is exactly
// one update old. A puppet counts for the project whose ProjectId it carries (post-site / leaving puppets included).
//
// Zones of one puppet = union over its box now and along its committed plan (samples <= kReportStep of machine time) up
// to the end of its last finite motion leg ("scheduled onto"); an open-ended front-relative leg is looked at for
// kReportFrontLook s (its lateral is constant there, its u stays inside the anchor clamps). Per sample the box is taken
// in the chain frame (centre = u/lat + heading x BoxOffZ, half extents projected on the heading), its lateral interval is
// classified with RoadZoneMath.OfLateral against the EdgeSection under the box (both box ends), and Outside is added when
// RoadZoneMath.OutsideFootprint(uMin, uMax, Trim0, Trim1). MachinesOnCarriageway counts puppets whose zones include
// LeftHalf, RightHalf or Outside.
namespace RealisticRoadWorks.V3.Machines
{
    // One cached report sample: zones of the box at a plan instant; counts while now <= Until.
    public struct ReportSample
    {
        public double Until;
        public RoadZones Z;
    }

    public static class MachineReport
    {
        public static int LastSamples;   // dev: samples evaluated in the last report
        static int s_SoftLeft;            // re-anchor (soft) report cache rebuilds left in this report pass
        private static readonly PlanContext s_Ctx = new PlanContext();   // clock + track view for the sample cache

        public static void Write(double now)
        {
            uint ui = RRWClock.UpdateIndex;
            foreach (var pr in SiteRegistry.Projects.Values)
            {
                pr.MachineZones = RoadZones.None;
                pr.MachinesOnCarriageway = 0;
                pr.MachinesStuck = 0;
                // crews with at least one role puppet (same stamp as the report)
                int spawned = 0, rollers = 0;
                if (MachineRegistry.States.TryGetValue(pr.Id, out var st))
                {
                    for (int k = 0; k < st.Crews.Length; k++) if (st.Crews[k].Any()) spawned++;   // a crew with only a roller counts
                    rollers = st.RollerCount();
                }
                pr.CrewsSpawned = spawned;
                pr.RollersSpawned = rollers;   // live roller units of the roster, same stamp
            }
            s_Ctx.Clk = MachineRegistry.Clock;
            s_Ctx.Tv = MachineTrackStore.View();
            s_Ctx.Now = now;
            s_SoftLeft = MxConst.kReportSoftRebuilds;
            int samples = 0;
            var all = MachineRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (!SiteRegistry.TryGetProject(p.ProjectId, out var pr))
                {
                    p.ZonesNow = p.ZonesPlan = RoadZones.None;
                    continue;
                }
                var v = pr.View();
                Zones(p, v.Trim0, v.Trim1, now, out var zNow, out var zAll, ref samples);
                p.ZonesNow = zNow;
                p.ZonesPlan = zAll;
                pr.MachineZones |= zAll;
                if ((zAll & (RoadZones.Carriageway | RoadZones.Outside)) != 0) pr.MachinesOnCarriageway++;
                if (p.Stuck) pr.MachinesStuck++;   // set by the separation guard this update
            }
            foreach (var pr in SiteRegistry.Projects.Values) pr.MachinesReportUpdate = ui;
            LastSamples = samples;
        }

        // Machines disabled (guard faulted, every puppet removed): nothing is on any carriageway. A fresh empty report, so the
        // release gate and the staged opening behave as for a project without machines.
        public static void WriteDisabled()
        {
            uint ui = RRWClock.UpdateIndex;
            foreach (var pr in SiteRegistry.Projects.Values)
            {
                pr.MachineZones = RoadZones.None;
                pr.MachinesOnCarriageway = 0;
                pr.MachinesStuck = 0;
                pr.CrewsSpawned = 0;   // no puppets -> no crew with puppets
                pr.RollersSpawned = 0;
                pr.MachinesReportUpdate = ui;
            }
            LastSamples = 0;
        }

        // Zones of one puppet now (zNow) and over its committed plan (zAll, includes zNow).
        // Performance: plans are deterministic, so the samples of the plan's FINITE parts (finite legs, open-ended
        // Holds) are cached per puppet under a fingerprint of everything Sample reads (plan legs + front models, clock,
        // track revision, trims, box); a later update only unions the cached samples that still lie ahead (t >= now, a
        // finite Hold counts until its end). Only zNow and the open-ended front-relative legs (their window moves with now)
        // are evaluated every update. Any change of the fingerprint rebuilds the cache with exactly the old sampling.
        public static void Zones(Puppet p, float trim0, float trim1, double now, out RoadZones zNow, out RoadZones zAll, ref int samples)
        {
            zNow = zAll = RoadZones.None;
            var plan = p.Plan;
            if (plan.Count <= 0) { p.ReportCacheValid = false; return; }
            var td = p.Track;
            if (td == null || !td.Valid) MachineTrackStore.TryGet(p.ProjectId, out td);
            var clk = MachineRegistry.Clock;
            zNow = Sample(p, plan, td, clk, now, trim0, trim1);
            zAll = zNow;
            samples++;
            MxPerf.Count(MxC.ReportPuppets);
            MxPerf.Count(MxC.ReportNowSamples);
            ulong key = Fingerprint(p, plan, td, clk, trim0, trim1);
            if (!p.ReportCacheValid || p.ReportCacheKey != key || now < p.ReportCacheFrom) BuildCache(p, plan, td, clk, now, trim0, trim1, key, ref samples);
            else if (p.ReportSoftStale)
            {
                // A re-anchor moved finite FrontA legs this cache holds samples of (the fingerprint has no
                // models). Rebuilt here, at most kReportSoftRebuilds puppets per update (a re-anchor touches every puppet of the
                // project at once); a deferred one keeps its zones (decimetres off) for a few updates
                if (s_SoftLeft > 0) { s_SoftLeft--; MxPerf.Count(MxC.ReportSoftRebuilds); BuildCache(p, plan, td, clk, now, trim0, trim1, key, ref samples); }
                else MxPerf.Count(MxC.ReportSoftDeferred);
            }
            var cache = p.ReportCache;
            for (int i = 0; i < cache.Count; i++)
                if (cache[i].Until >= now) zAll |= cache[i].Z;
            // open-ended front-relative legs: their look-ahead window starts at now. Sampled on the plan
            // sample cache's absolute grid, so each grid sample is classified once per plan (only the window's new far end costs)
            double rel = now - plan.Epoch;
            int i0 = MachineMotion.ActiveLeg(plan, rel);
            bool validated = false;
            for (int i = i0; i < plan.Count; i++)
            {
                var leg = plan.Get(i);
                var k = (LegKind)leg.Kind;
                if (!leg.OpenEnded || k == LegKind.Hold) continue;
                bool front = k == LegKind.Follow || k == LegKind.DigHop || k == LegKind.Shuttle || k == LegKind.Scrape;
                double from = math.max(now, plan.Epoch + leg.T0);
                double end = from + math.max(leg.CatchDur, 1f) + (front ? MxConst.kReportFrontLook : 0f);
                if (!validated) { PlanSamples.Validate(p, clk, now); validated = true; }
                long k0 = PlanSamples.Ceil(from), k1 = PlanSamples.Ceil(end);
                if (k1 - k0 > MxConst.kReportMaxSamples) k1 = k0 + MxConst.kReportMaxSamples;
                for (long g = k0; g <= k1; g++) zAll |= PlanSamples.ZonesAt(p, g, s_Ctx, td, trim0, trim1);
                samples += (int)(k1 - k0 + 1);
                MxPerf.Count(MxC.ReportOpenSamples, k1 - k0 + 1);
            }
        }

        // The finite parts of the plan from 'now' on, sampled exactly as before (shared budget, step >= kReportStep).
        static void BuildCache(Puppet p, in MachinePlan plan, TrackData td, in ClockData clk, double now, float trim0, float trim1, ulong key, ref int samples)
        {
            MxPerf.Begin(MxT.R_ReportCache);
            MxPerf.Count(MxC.ReportCacheRebuilds);
            int samples0 = samples;
            var cache = p.ReportCache;
            cache.Clear();
            p.ReportCacheKey = key;
            p.ReportCacheFrom = now;
            p.ReportCacheValid = true;
            p.ReportSoftStale = false;
            p.ReportFrontUntil = double.NegativeInfinity;
            double rel = now - plan.Epoch;
            int i0 = MachineMotion.ActiveLeg(plan, rel);
            int budget = MxConst.kReportMaxSamples;
            for (int i = i0; i < plan.Count && budget > 0; i++)
            {
                var leg = plan.Get(i);
                double a = plan.Epoch + leg.T0;
                double from = math.max(now, a);
                var k = (LegKind)leg.Kind;
                if (leg.OpenEnded && k != LegKind.Hold) continue;   // evaluated every update (Zones)
                if (k == LegKind.Hold)
                {
                    // a Hold stands still: valid until it ends (open-ended: for good)
                    double until = leg.OpenEnded ? double.PositiveInfinity : plan.Epoch + leg.T1;
                    if (until < from) continue;
                    cache.Add(new ReportSample { Until = until, Z = Sample(p, plan, td, clk, from, trim0, trim1) });
                    samples++; budget--;
                    continue;
                }
                double end = plan.Epoch + leg.T1;
                if (end < from) continue;
                if (leg.Front == 0 && Choreo.FrontDep(leg)) p.ReportFrontUntil = math.max(p.ReportFrontUntil, end);   // re-anchors make these samples soft-stale
                double step = math.max(MxConst.kReportStep, (end - from) / math.max(1, budget));
                for (double t = from; t < end && budget > 0; t += step)
                {
                    cache.Add(new ReportSample { Until = t, Z = Sample(p, plan, td, clk, t, trim0, trim1) });
                    samples++; budget--;
                }
                cache.Add(new ReportSample { Until = end, Z = Sample(p, plan, td, clk, end, trim0, trim1) });
                samples++; budget--;
            }
            MxPerf.Count(MxC.ReportCacheSamples, samples - samples0);
            MxPerf.End(MxT.R_ReportCache);
        }

        // Fingerprint of every input of Sample except the time: the plan sample fingerprint + the trims.
        static ulong Fingerprint(Puppet p, in MachinePlan plan, TrackData td, in ClockData clk, float trim0, float trim1)
        {
            ulong h = PlanSamples.Fingerprint(p, plan, td, clk);
            PlanSamples.Mix(ref h, trim0);
            PlanSamples.Mix(ref h, trim1);
            return h;
        }

        static RoadZones Sample(Puppet p, in MachinePlan plan, TrackData td, in ClockData clk, double t, float trim0, float trim1)
        {
            MachineMotion.State(plan, clk, t, out var s);
            return ZoneOf(p, s.U, s.Lat, s.Hu, s.Hl, td, trim0, trim1);
        }

        // Zones of p's box at chain state (u, lat, heading).
        public static RoadZones ZoneOf(Puppet p, float u, float lat, float hu, float hlat, TrackData td, float trim0, float trim1)
        {
            float2 h = math.normalizesafe(new float2(hu, hlat), new float2(1f, 0f));
            float cu = u + h.x * p.BoxOffZ, cl = lat + h.y * p.BoxOffZ;
            float hl = p.BoxHalfLen, hw = p.BoxHalfWid;
            float eu = math.abs(h.x) * hl + math.abs(h.y) * hw;
            float el = math.abs(h.y) * hl + math.abs(h.x) * hw;
            // Shuttle strokes / Scrape bumps move u inside the leg: the samples cover them (<= 0.5 s apart)
            var z = RoadZones.None;
            if (TrackBuilder.SectionAt(td, cu - eu, out var s0, out bool r0)) z |= RoadZoneMath.OfLateral(cl - el, cl + el, s0, r0);
            if (TrackBuilder.SectionAt(td, cu + eu, out var s1, out bool r1)) z |= RoadZoneMath.OfLateral(cl - el, cl + el, s1, r1);
            else if (z == RoadZones.None && !TrackBuilder.SectionAt(td, cu, out _, out _))
            {
                // no section known (track gone): anything within the old flat floor counts as the carriageway
                if (math.abs(cl) - el < 6f) z |= RoadZones.Carriageway;
            }
            if (RoadZoneMath.OutsideFootprint(cu - eu, cu + eu, trim0, trim1)) z |= RoadZones.Outside;
            return z;
        }
    }
}
