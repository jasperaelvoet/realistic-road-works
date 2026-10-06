using System.Text;
using Unity.Mathematics;

// The bucket-tip TARGETS of one dig cycle - a port of the dig animation prototype (Build, PickDump,
// FitSegment, DumpClearance, Eval; verified: tip-floor gap 0.02 m, slew peak 34.5 deg/s, truck clearance 0.31 m), with the
// TIMING of Core's DigSchedule (fixed 20 s period: the planner's dig grid, the loading trucks' hops, the events and the bones share
// it). Keys are in arm space (slew deg, planar r, height h above the machine's ground, bucket attitude deg); the bone pass evaluates
// them every frame and solves the IK. Built at Modification1 only at a cycle boundary (the bucket is at Ready / Hold then, so a
// rebuild never pops the pose), from the floor under the bite, the truck the cycle dumps into (or the spoil side) and the mode.
namespace RealisticRoadWorks.V3.Machines
{
    // The loading truck as an oriented box in the EXCAVATOR's object space (+x right, +y up, +z forward), from its prefab bounds.
    public struct DigTruckBox
    {
        public float3 C;          // footprint centre (y unused)
        public float3 Ax, Az;     // truck right / forward, unit, horizontal
        public float Hx, Hz;      // half width / half length
        public float Y0, Y1;      // bottom / top height

        // Signed distance (m): > 0 outside the box, < 0 inside.
        public float Sdf(float3 p)
        {
            float3 d = p - C;
            float3 q = new float3(math.abs(math.dot(d, Ax)) - Hx, math.abs(p.y - 0.5f * (Y0 + Y1)) - 0.5f * (Y1 - Y0), math.abs(math.dot(d, Az)) - Hz);
            return math.length(math.max(q, 0f)) + math.min(math.cmax(q), 0f);
        }

        public bool SameAs(in DigTruckBox o, float tol) =>
            math.all(math.abs(C - o.C) < tol) && math.all(math.abs(Ax - o.Ax) < 0.02f) && math.abs(Hx - o.Hx) < tol && math.abs(Hz - o.Hz) < tol &&
            math.abs(Y0 - o.Y0) < tol && math.abs(Y1 - o.Y1) < tol;
    }

    public struct DigKey
    {
        public float Slew, R, H, Att;     // arm-space target at the END of the segment that ends at this key
        public float HLead;               // fraction of the segment at which h reaches this key (1 = with the others)
        public float HLag;                // fraction of the segment before h starts moving (return: stay high until clear of the truck)
        public float SLag;                // fraction of the segment before slew and r start moving (swing: boom up before slewing)
        public bool Shake;                // dump shake in this segment (attitude +-12 deg at 3 Hz)
        public bool Wrist;                // interpolate the bucket PIVOT (wrist) instead of the tip: the bucket turns about its pin
    }

    public struct DigTarget
    {
        public float Slew, R, H, Att;
        public DigKey Seg;                // (dev) the segment's end key
        public int SegIndex;
    }

    public sealed class DigKeys
    {
        // key 0 = ready (cycle start), 1 lower, 2 penetrate, 3 drag, 4 curl, 5 lift, 6 swing, 7 dump, 8 shake, 9 return (= ready)
        public const int N = 10;
        public readonly DigKey[] Keys = new DigKey[N];
        public DigSchedule Sched;
        public DigMode Mode;
        public double CycleStart = double.NaN;  // machine time of the cycle these keys belong to
        public float FloorH, GradeH, Bite, Clear;
        public float DigSlew, DumpSlew, DumpR, DumpH, RFar, RNear, RReady, RFloorMax, BedR, BedH, AttOpen, Lb;
        public bool HaveTruck, TruckOutOfReach, DumpFeasible, HasBox;
        public bool Provisional;                // built without the dump target (build budget); replaced before the swing
        public bool SpoilOutside;               // no spoil dump point inside the carriageway / floor was found (rrw.mx.check)
        public DigTruckBox Box;
        public Puppet Truck;                    // dump target (null = spoil side)
        public float SpoilSide = 1f;
        public float DumpClear = float.NaN, DumpRaised;
        public float SwingFit = float.NaN, ReturnFit = float.NaN;   // fitted durations (s) fed into DigSchedule.Make
        public string Fit = "", Notes = "", TargetWhy = "";
        public float BedTopWorldY = float.NaN;  // truck bed top (world) for the dump puff

        public const float kAttReady = -35f, kAttLower = -25f, kAttPen = 5f, kAttDrag = 65f, kAttCurl = 125f, kAttCarry = 115f;
        public const float kHRate = 2.2f;       // average boom lift / lower speed of the tip used by the fit (m/s, verified)
        const int kFitSamples = 24;             // samples per fitted segment (prototype used 40; 24 keeps a build < ~2 ms on the main thread)

        static float2 Dir(float deg) => DigArm.Dir(deg);

        // Builds the keys of one cycle. floorH / gradeH: trench floor at the bite / road surface, relative to the machine's ground
        // (object y 0). haveTruck: the dump goes into the box `box` at (truckSlew, truckR) with the bed top truckH (all excavator
        // space); otherwise onto the spoil side (slew 100 deg x side, grade + 1 m). Returns the keys + the fitted swing / return.
        public static DigKeys Build(DigArm arm, DigMode mode, float floorH, float gradeH, float bite, float spoilSide, bool haveTruck,
                                    float truckSlew, float truckR, float truckH, in DigTruckBox box) =>
            Build(arm, mode, floorH, gradeH, bite, spoilSide, haveTruck, truckSlew, truckR, truckH, box, null);

        // fitFrom: keys of an earlier cycle with the same dump layout (truck box / spoil side, grade, bed height, mode): its dump pick and
        // swing / return fit are reused - they do not depend on the floor - so a deepening trench never repeats the expensive truck fit.
        // spoilSlew / spoilR = the spoil dump point chosen by the caller (side away from the crew's trucks, tip
        // inside the carriageway / floor; NaN = slew 100 deg x side at 0.8 x RFar); spoilBox = a truck near the spoil cycle: its box
        // goes into the dump pick and the swing / return fit like a truck target's (the bucket keeps clear of it).
        public static DigKeys Build(DigArm arm, DigMode mode, float floorH, float gradeH, float bite, float spoilSide, bool haveTruck,
                                    float truckSlew, float truckR, float truckH, in DigTruckBox box, DigKeys fitFrom,
                                    float spoilSlew = float.NaN, float spoilR = float.NaN, bool spoilBox = false)
        {
            var c = new DigKeys { Mode = mode, GradeH = gradeH, HaveTruck = haveTruck, SpoilSide = spoilSide >= 0f ? 1f : -1f };
            if (haveTruck || spoilBox) { c.HasBox = true; c.Box = box; }
            var notes = new StringBuilder();
            // Dig: the drag runs `bite` below the visible floor (0 = the verified scrape at floor + kDigTipClear). Break: the road surface.
            bool brk = mode == DigMode.Break;
            c.Bite = brk ? 0f : math.max(0f, bite);
            c.Clear = c.Bite > 0f ? 0f : RRWConst.kDigTipClear;
            c.FloorH = math.min(gradeH, floorH) - c.Bite;
            float fl = c.FloorH, clear = c.Clear;

            // reach envelope at the floor
            c.RFloorMax = arm.ReachAt(fl + clear, kAttPen);
            float rLower = arm.ReachAt(fl + 0.30f, kAttLower);
            c.RFar = math.min(c.RFloorMax * 0.92f, rLower);
            float rNearMin = arm.FrontClear + 0.6f;
            c.RNear = arm.MinReachAt(fl + clear, kAttDrag, rNearMin, math.max(rNearMin, c.RFar - 0.5f));
            float rCurl = arm.MinReachAt(fl + 0.45f, kAttCurl, c.RNear + 0.15f, math.max(c.RNear + 0.15f, c.RFar));
            c.RNear = math.max(c.RNear, rCurl - 0.15f);
            if (c.RFar - c.RNear < 1.0f) notes.Append("short-drag(").Append(RRWLog.F(c.RFar - c.RNear)).Append("m) ");
            if (c.RFloorMax <= 0.3f) notes.Append("FLOOR-OUT-OF-REACH ");
            c.RReady = math.max(rNearMin + 0.3f, 0.75f * arm.ReachAt(c.GradeH + 1.0f, kAttReady));

            // dump target: the open bucket's tip over the bed centre (truck) / the spoil heap, the wrist kept fixed while it opens
            float liftH = c.GradeH + 0.9f;
            c.Lb = arm.Lb;
            if (haveTruck) { c.DumpSlew = truckSlew; c.BedR = truckR; c.BedH = truckH; }
            else
            {
                c.DumpSlew = !float.IsNaN(spoilSlew) ? spoilSlew : c.SpoilSide * 100f;
                c.SpoilSide = c.DumpSlew >= 0f ? 1f : -1f;
                c.BedH = c.GradeH + 1.0f;
                c.BedR = !float.IsNaN(spoilR) ? math.max(arm.FrontClear + 1.5f, spoilR) : math.max(arm.FrontClear + 1.5f, 0.8f * c.RFar);
            }
            // the same dump layout: mode, target kind, box, dump slew / radius / bed height (before any clearance raise), grade
            bool reuse = fitFrom != null && !fitFrom.Provisional && fitFrom.Mode == mode && fitFrom.HaveTruck == haveTruck && fitFrom.HasBox == c.HasBox &&
                         math.abs(fitFrom.GradeH - gradeH) < 0.05f && fitFrom.DumpFeasible &&
                         math.abs(DigArm.Wrap180(fitFrom.DumpSlew - c.DumpSlew)) < 1f && math.abs(fitFrom.BedR - c.BedR) < (haveTruck ? 0.08f : 0.3f) &&
                         math.abs(fitFrom.BedH - fitFrom.DumpRaised - c.BedH) < 0.08f &&
                         (!c.HasBox || fitFrom.Box.SameAs(box, 0.08f));
            float dumpR;
            float2 carry;
            if (reuse)
            {
                c.DumpFeasible = true;
                c.BedR = fitFrom.BedR; c.BedH = fitFrom.BedH; c.DumpSlew = fitFrom.DumpSlew; c.AttOpen = fitFrom.AttOpen;
                c.DumpClear = fitFrom.DumpClear; c.DumpRaised = fitFrom.DumpRaised;
                dumpR = fitFrom.DumpR;
                carry = new float2(fitFrom.Keys[6].R, fitFrom.Keys[6].H);
                if (c.DumpRaised > 0.005f) notes.Append("dump-raised(+").Append(RRWLog.F(c.DumpRaised)).Append("m) ");
                notes.Append("fit-reused ");
            }
            else c.DumpFeasible = PickDump(arm, c, out dumpR, out carry);
            if (c.HasBox && c.DumpFeasible && !reuse)
            {
                float bed0 = c.BedH;
                for (int it = 0; it < 4; it++)
                {
                    c.DumpClear = DumpClearance(arm, c, dumpR, carry);
                    if (c.DumpClear >= RRWConst.kDigDumpClear - 0.01f) break;
                    float keepH = c.BedH, keepR = dumpR, keepAtt = c.AttOpen;
                    float2 keepCarry = carry;
                    c.BedH += RRWConst.kDigDumpClear - c.DumpClear + 0.02f;
                    if (!PickDump(arm, c, out dumpR, out carry))
                    {
                        c.BedH = keepH; dumpR = keepR; carry = keepCarry; c.AttOpen = keepAtt;
                        c.DumpClear = DumpClearance(arm, c, dumpR, carry);
                        notes.Append("dump-raise-infeasible ");
                        break;
                    }
                }
                c.DumpRaised = c.BedH - bed0;
                if (c.DumpRaised > 0.005f) notes.Append("dump-raised(+").Append(RRWLog.F(c.DumpRaised)).Append("m) ");
                if (c.DumpClear < RRWConst.kDigTruckClearMin) notes.Append("DUMP-CLIPS-TRUCK(").Append(RRWLog.F(c.DumpClear)).Append("m) ");
            }
            if (!c.DumpFeasible)
            {
                notes.Append("dump-infeasible(r=").Append(RRWLog.F(c.BedR)).Append(" h=").Append(RRWLog.F(c.BedH)).Append(") ");
                c.AttOpen = -20f;
                carry = new float2(c.BedR, c.BedH) - Dir(-90f - c.AttOpen) * c.Lb + Dir(-90f - kAttCarry) * c.Lb;
                dumpR = c.BedR;
            }
            else if (math.abs(dumpR - c.BedR) > 0.05f)
            {
                notes.Append(haveTruck ? "truck-dump-shifted(" : "spoil-shifted(").Append(RRWLog.F(dumpR - c.BedR)).Append("m) ");
                if (haveTruck && math.abs(dumpR - c.BedR) > 0.8f) c.TruckOutOfReach = true;
            }
            c.DumpR = dumpR;
            c.DumpH = carry.y;

            // the product digs straight ahead (bite lateral = the machine's own lateral): dig slew 0
            c.DigSlew = 0f;
            float slewTime = math.abs(DigArm.Wrap180(c.DumpSlew - c.DigSlew)) / math.max(5f, RRWConst.kDigSlewRateAvg);
            float swing = math.max(RRWConst.kDigSwingMin, slewTime);
            float ds = c.DigSlew;
            var K = c.Keys;
            float penH = brk ? fl + clear + RRWConst.kBreakLift : fl + clear;
            K[0] = Key(ds, c.RReady, c.GradeH + 1.0f, kAttReady);                          // ready
            K[1] = Key(ds, c.RFar, fl + 0.30f + (brk ? RRWConst.kBreakLift : 0f), kAttLower); // boom down, stick out, bucket open
            K[2] = Key(ds, c.RFar - 0.15f, penH, brk ? 5f : kAttPen);                      // teeth bite at the far end (Break: lifted)
            K[3] = Key(ds, c.RNear + 0.25f, penH, brk ? 5f : kAttDrag);                    // drag (Break: the strikes, Eval)
            K[4] = Key(ds, c.RNear + 0.15f, fl + 0.45f, kAttCurl);                         // breakout: bucket full, rolls back
            K[5] = Key(ds, c.RNear + 0.6f, liftH, kAttCarry);                              // lift clear of the trench
            K[6] = Key(c.DumpSlew, carry.x, carry.y, kAttCarry); K[6].HLead = 0.7f;        // swing loaded, boom up while swinging
            K[7] = Key(c.DumpSlew, dumpR, c.BedH, c.AttOpen); K[7].Wrist = true;           // open over the bed, about the bucket pin
            K[8] = Key(c.DumpSlew, dumpR, c.BedH, c.AttOpen); K[8].Wrist = true; K[8].Shake = true;   // shake it empty
            K[9] = Key(ds, c.RReady, c.GradeH + 1.0f, kAttReady); K[9].HLead = 0.6f;       // swing back empty (= ready)
            float swingDur = swing, retDur = swing;
            // keep the bucket out of the truck: boom up before the slew reaches the truck, down only after it has left it
            if (reuse)
            {
                K[6].SLag = fitFrom.Keys[6].SLag; K[6].HLag = fitFrom.Keys[6].HLag; K[6].HLead = fitFrom.Keys[6].HLead;
                K[9].SLag = fitFrom.Keys[9].SLag; K[9].HLag = fitFrom.Keys[9].HLag; K[9].HLead = fitFrom.Keys[9].HLead;
                swingDur = fitFrom.SwingFit;
                retDur = fitFrom.ReturnFit;
                c.Fit = fitFrom.Fit;
            }
            else if (c.HasBox)
            {
                string f1 = FitSegment(arm, c, 6, slewTime, ref swingDur, true);
                string f2 = FitSegment(arm, c, 9, slewTime, ref retDur, false);
                c.Fit = f1 + " " + f2;
                if (f1.Contains("FAILED") || f2.Contains("FAILED")) notes.Append("truck-clear-fit-failed ");
            }
            c.SwingFit = swingDur;
            c.ReturnFit = retDur;
            c.Sched = DigSchedule.Make(mode, swingDur, retDur);
            if (c.Sched.Compressed) notes.Append("compressed(swing ").Append(RRWLog.F(c.Sched.Swing)).Append(" return ").Append(RRWLog.F(c.Sched.Return)).Append(") ");
            c.Notes = notes.ToString().TrimEnd();
            return c;
        }

        // The spoil reach of a cycle with these inputs (the same RFar as Build): default dump radius (0.8 x RFar),
        // the smallest one (chassis front + 1.5 m) and RFar.
        public static void SpoilReach(DigArm arm, DigMode mode, float floorH, float gradeH, float bite, out float rDef, out float rMin, out float rFar)
        {
            bool brk = mode == DigMode.Break;
            float b = brk ? 0f : math.max(0f, bite);
            float clear = b > 0f ? 0f : RRWConst.kDigTipClear;
            float fl = math.min(gradeH, floorH) - b;
            rFar = math.min(arm.ReachAt(fl + clear, kAttPen) * 0.92f, arm.ReachAt(fl + 0.30f, kAttLower));
            rMin = arm.FrontClear + 1.5f;
            rDef = math.max(rMin, 0.8f * rFar);
        }

        // Shallow copy for the next cycle with the same inputs (the key array is shared: never changed after Build).
        public DigKeys Copy() => (DigKeys)MemberwiseClone();

        static DigKey Key(float slew, float r, float h, float att) => new DigKey { Slew = slew, R = r, H = h, Att = att, HLead = 1f };

        // Segment i (1..9) start / end times on the schedule.
        public float SegStart(int i) => i <= 1 ? 0f : SegEnd(i - 1);
        public float SegEnd(int i)
        {
            switch (i)
            {
                case 1: return Sched.TLower;
                case 2: return Sched.TPenetrate;
                case 3: return Sched.TDrag;
                case 4: return Sched.TCurl;
                case 5: return Sched.TLift;
                case 6: return Sched.TSwing;
                case 7: return Sched.TDump;
                case 8: return Sched.TShake;
                default: return Sched.TReturn;
            }
        }

        // Tip target at cycle time t (s from the cycle start; the Hold after the return stands at Ready).
        public DigTarget Eval(float t)
        {
            float P = Sched.Period;
            if (!(P > 0f)) return new DigTarget { Slew = Keys[0].Slew, R = Keys[0].R, H = Keys[0].H, Att = Keys[0].Att };
            t = t % P;
            if (t < 0f) t += P;
            if (t >= Sched.TReturn) { var k = Keys[N - 1]; return new DigTarget { Slew = k.Slew, R = k.R, H = k.H, Att = k.Att, Seg = k, SegIndex = N }; }
            int i = 1;
            while (i < N - 1 && t > SegEnd(i)) i++;
            var a = Keys[i - 1];
            var b = Keys[i];
            float t0 = SegStart(i), dur = SegEnd(i) - t0;
            float u = dur > 1e-4f ? math.saturate((t - t0) / dur) : 1f;
            float sm = Smooth(u);
            float ks = b.SLag > 0f ? Smooth(math.saturate((u - b.SLag) / (1f - b.SLag))) : sm;
            float kh = Smooth(math.saturate((u - b.HLag) / math.max(0.05f, b.HLead - b.HLag)));
            var o = new DigTarget
            {
                Slew = a.Slew + DigArm.Wrap180(b.Slew - a.Slew) * ks,
                R = math.lerp(a.R, b.R, ks),
                H = math.lerp(a.H, b.H, kh),
                Att = math.lerp(a.Att, b.Att, sm),
                Seg = b, SegIndex = i,
            };
            if (Mode == DigMode.Break && i == 3)
            {
                // D0 chop: kBreakStrikes vertical strikes stepping from the far reach to the near reach; the tip drops from
                // surface + kBreakLift to the surface at each StrikeT(i); attitude +5 deg
                int n = math.max(1, RRWConst.kBreakStrikes);
                float x = (t - t0) / math.max(1e-3f, dur) * n;   // strike phase 0..n
                float ph = x - math.floor(x);
                float down = math.sin(math.PI * ph);              // 0 at the phase ends, 1 at the strike (ph 0.5 = StrikeT)
                o.H = FloorH + Clear + RRWConst.kBreakLift * (1f - down * down);
                o.R = math.lerp(a.R, b.R, math.saturate((math.floor(x) + Smooth(math.saturate((ph - 0.6f) / 0.4f))) / n));
                o.Att = 5f;
            }
            if (b.Shake) o.Att += 12f * math.sin(2f * math.PI * 3f * (t - t0)) * math.sin(math.PI * u);
            if (b.Wrist)
            {
                float2 wa = new float2(a.R, a.H) - Dir(-90f - a.Att) * Lb, wb = new float2(b.R, b.H) - Dir(-90f - b.Att) * Lb;
                float2 tip = math.lerp(wa, wb, sm) + Dir(-90f - o.Att) * Lb;
                o.R = tip.x;
                o.H = tip.y;
            }
            return o;
        }

        public static float Smooth(float u) { u = math.saturate(u); return u * u * (3f - 2f * u); }

        // ------------------------------------------------------------ truck clearance (verified fit)

        private static readonly float3[] s_Pts = new float3[4];

        // Lowest signed distance of the bucket sample points to the truck box for one arm-space tip target (+inf without a box).
        public float ClearanceAt(DigArm arm, float slew, float r, float h, float att)
        {
            if (!HasBox) return float.PositiveInfinity;
            var res = arm.SolveArm(slew, r, h, att, float.NegativeInfinity);
            int n = arm.BucketPoints(res.Pose.F4, s_Pts);
            float m = float.PositiveInfinity;
            for (int i = 0; i < n; i++) m = math.min(m, Box.Sdf(s_Pts[i]));
            return m;
        }

        // Dump with the wrist fixed: the bucket turning from the loaded carry attitude to open, and the +-12 deg shake.
        static float DumpClearance(DigArm arm, DigKeys c, float dumpR, float2 carry)
        {
            float2 tip = new float2(dumpR, c.BedH);
            float2 w = tip - Dir(-90f - c.AttOpen) * arm.Lb;
            float m = c.ClearanceAt(arm, c.DumpSlew, carry.x, carry.y, kAttCarry);
            for (int i = 0; i <= 12; i++)
            {
                float a = i <= 10 ? math.lerp(kAttCarry, c.AttOpen, i / 10f) : c.AttOpen + (i == 11 ? -12f : 12f);
                float2 t = w + Dir(-90f - a) * arm.Lb;
                m = math.min(m, c.ClearanceAt(arm, c.DumpSlew, t.x, t.y, a));
            }
            return m;
        }

        static void SegTarget(in DigKey a, in DigKey b, float u, float sLag, float hLag, float hLead, out float slew, out float r, out float h, out float att)
        {
            float k = Smooth(u);
            float ks = Smooth(sLag > 0f ? math.saturate((u - sLag) / (1f - sLag)) : u);
            float kh = Smooth(math.saturate((u - hLag) / math.max(0.05f, hLead - hLag)));
            slew = a.Slew + DigArm.Wrap180(b.Slew - a.Slew) * ks;
            r = math.lerp(a.R, b.R, ks);
            h = math.lerp(a.H, b.H, kh);
            att = math.lerp(a.Att, b.Att, k);
        }

        float SegClearance(DigArm arm, in DigKey a, in DigKey b, float sLag, float hLag, float hLead)
        {
            float m = float.PositiveInfinity;
            for (int i = 0; i <= kFitSamples; i++)
            {
                SegTarget(a, b, i / (float)kFitSamples, sLag, hLag, hLead, out float sl, out float r, out float h, out float att);
                m = math.min(m, ClearanceAt(arm, sl, r, h, att));
            }
            return m;
        }

        private static readonly float[] s_Lags = { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f };
        private static readonly float[] s_LeadsOut = { 0.7f, 0.6f, 0.5f };
        private static readonly float[] s_LeadsBack = { 0.6f, 0.8f, 1f };

        // Choose the slew lag (swing out: boom up first) or the h lag (return: stay high until past the truck) of segment i so that
        // the bucket keeps kDigFitClear from the truck box (or what its end poses allow), stretching the segment only as much as the
        // nominal slew rate and an average tip lift speed of kHRate need. Shortest duration wins (as in the verified prototype fit).
        static string FitSegment(DigArm arm, DigKeys c, int i, float slewTime, ref float dur, bool swingOut)
        {
            var a = c.Keys[i - 1];
            var b = c.Keys[i];
            float ends = math.min(c.ClearanceAt(arm, a.Slew, a.R, a.H, a.Att), c.ClearanceAt(arm, b.Slew, b.R, b.H, b.Att));
            float need = math.min(RRWConst.kDigFitClear, ends - 0.02f);
            float dh = math.abs(b.H - a.H);
            float d0 = dur;
            float baseClear = c.SegClearance(arm, a, b, b.SLag, b.HLag, b.HLead);
            bool found = false;
            float bestD = float.MaxValue, bestS = 0f, bestHl = 0f, bestHd = b.HLead, bestClear = baseClear;
            var leads = swingOut ? s_LeadsOut : s_LeadsBack;
            foreach (float lag in s_Lags)
                foreach (float lead0 in leads)
                {
                    float sLag = swingOut ? lag : 0f;
                    float hLag = swingOut ? 0f : lag;
                    float hLead = swingOut ? lead0 : math.min(1f, math.max(lead0, hLag + 0.3f));
                    if (hLead - hLag < 0.25f) continue;
                    float d = math.max(d0, math.max(slewTime / (1f - sLag), dh / (kHRate * (hLead - hLag))));
                    if (d > bestD + 1e-3f) continue;
                    if (found && math.abs(d - bestD) <= 1e-3f) continue;   // same duration: keep the smaller lag found first
                    float cl = c.SegClearance(arm, a, b, sLag, hLag, hLead);
                    if (cl < need) continue;
                    found = true; bestD = d; bestS = sLag; bestHl = hLag; bestHd = hLead; bestClear = cl;
                }
            string name = swingOut ? "swing" : "return";
            if (!found) return name + "(FAILED clear=" + RRWLog.F(baseClear) + " need=" + RRWLog.F(need) + ")";
            b.SLag = bestS; b.HLag = bestHl; b.HLead = bestHd;
            c.Keys[i] = b;
            dur = bestD;
            return name + "(sLag=" + RRWLog.F(bestS) + " hLag=" + RRWLog.F(bestHl) + " hLead=" + RRWLog.F(bestHd) + " dur=" + RRWLog.F(bestD) + "s clear=" +
                   RRWLog.F(bestClear) + " was " + RRWLog.F(baseClear) + ")";
        }

        static bool Clean(DigArm arm, float slew, float2 tip, float att)
        {
            var r = arm.SolveArm(slew, tip.x, tip.y, att, float.NegativeInfinity);
            return !r.Unreachable && r.Limits == 0 && r.Err < 0.01f;
        }

        private static readonly float[] s_Opens = { -40f, -30f, -20f, -10f, 0f, 10f };

        // Most open attitude (and the radius closest to the bed centre, +-1.5 m) for which the loaded carry pose, the open pose and the
        // +-12 deg shake around it are all clean with the wrist fixed.
        static bool PickDump(DigArm arm, DigKeys c, out float dumpR, out float2 carry)
        {
            for (int step = 0; step <= 30; step++)
            {
                float dr = (step + 1) / 2 * 0.1f * ((step & 1) == 0 ? 1f : -1f);
                float rT = c.BedR + dr;
                if (rT < arm.P1.x + 0.5f) continue;
                foreach (float att in s_Opens)
                {
                    float2 tip = new float2(rT, c.BedH);
                    float2 w = tip - Dir(-90f - att) * arm.Lb;
                    float2 cy = w + Dir(-90f - kAttCarry) * arm.Lb;
                    if (!Clean(arm, c.DumpSlew, tip, att) || !Clean(arm, c.DumpSlew, cy, kAttCarry)) continue;
                    if (!Clean(arm, c.DumpSlew, w + Dir(-90f - (att - 12f)) * arm.Lb, att - 12f)) continue;
                    if (!Clean(arm, c.DumpSlew, w + Dir(-90f - (att + 12f)) * arm.Lb, att + 12f)) continue;
                    c.AttOpen = att;
                    dumpR = rT;
                    carry = cy;
                    return true;
                }
            }
            dumpR = c.BedR;
            carry = default;
            return false;
        }

        public string SummaryText() =>
            "mode=" + Mode + " period=" + RRWLog.F(Sched.Period) + "s breakoutAt=" + RRWLog.F(Sched.BreakoutT) + "s dumpAt=" + RRWLog.F(Sched.DumpT) +
            "s hold=" + RRWLog.F(Sched.Hold) + "s floorH=" + RRWLog.F(FloorH) + " bite=" + RRWLog.F(Bite) + " gradeH=" + RRWLog.F(GradeH) +
            " rFar=" + RRWLog.F(RFar) + " rNear=" + RRWLog.F(RNear) + " rReady=" + RRWLog.F(RReady) +
            " dump=(slew " + RRWLog.F(DumpSlew) + " r " + RRWLog.F(DumpR) + " h " + RRWLog.F(BedH) + " open " + RRWLog.F(AttOpen) + ")" +
            (HaveTruck ? " truck=" + (Truck != null ? Truck.ToString() : "?") + " dumpClear=" + RRWLog.F(DumpClear) + " fit=[" + Fit.Trim() + "]" : " spoil side=" + RRWLog.F(SpoilSide)) +
            (DumpFeasible ? "" : " DUMP-INFEASIBLE") + (TruckOutOfReach ? " TRUCK-OUT-OF-REACH" : "") +
            " target=" + TargetWhy + (Notes.Length > 0 ? " notes=[" + Notes + "]" : "");
    }
}
