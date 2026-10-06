using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // The IK dig cycle as a pure, job-safe TIME SCHEDULE. Machines' IK (a port of the dig-animation prototype,
    // verified in game) turns each key into a bucket-tip target in arm space and solves the bones; the planner (DigGrid /
    // hop window), the loading truck, the dust puffs and the truck load all read the SAME schedule, so they agree on every event.
    //
    // Timeline of one cycle (machine seconds, t in [0, Period)):
    //   0            Ready   (end of the previous Return / Hold)
    //   -> Lower     boom down / stick out over the far bite (bucket in the air)
    //   -> Penetrate teeth bite at the far end of the floor            (bucket at the floor from here ...)
    //   -> Drag      drag towards the machine (Break: kBreakStrikes chop strikes)
    //   -> Curl      the bucket fills and rolls back (BREAKOUT event at its start)
    //   -> Lift      clear of the trench                               (... to the end of Curl + kDigGroundMargin)
    //   -> Swing     loaded, to the truck bed / spoil side (fitted)
    //   -> Dump      open over the bed (DUMP event kDigDumpAt into it)
    //   -> Shake     shake it empty
    //   -> Return    empty, back to Ready (fitted)
    //   -> Hold      operator pause at Ready (Period - everything else; 0 when the swings use the whole period)
    // The root (tracks) must not move in [0, StationaryEnd]; the step-and-dig hop (and the loading truck's hop, with exactly the same
    // parameters) runs in [Period - HopDur, Period) with HopDur in [HopDefault, HopWindowMax] (the step-and-dig hop rule, timed by the IK schedule).
    public enum DigMode : byte
    {
        Dig = 0,          // C1, D1: bucket bites into the trench floor and drags towards the machine
        Break = 1,        // D0: chop strikes on the road surface, then scoop the broken asphalt
    }

    public enum DigKey : byte
    {
        Ready = 0, Lower = 1, Penetrate = 2, Drag = 3, Curl = 4, Lift = 5, Swing = 6, Dump = 7, Shake = 8, Return = 9, Hold = 10, Count = 11,
    }

    [System.Flags]
    public enum DigEvents : byte
    {
        None = 0,
        Breakout = 1,     // the bucket breaks out of the soil (start of Curl): dust puff at the tip on the floor
        Dump = 2,         // the bucket is half open over the bed / spoil: dust puff there + truck load +25 % (truck target only)
        Strike = 4,       // Break mode: a chop strike reaches the surface (no puff by default; dev / audio hook)
    }

    public struct DigSchedule
    {
        public DigMode Mode;
        public float Period;
        // END time of each key (cumulative from the cycle start). Ready ends at 0.
        public float TLower, TPenetrate, TDrag, TCurl, TLift, TSwing, TDump, TShake, TReturn;
        public float Swing, Return, Hold;   // durations (s) of the fitted segments and the hold
        public float BreakoutT, DumpT;      // event times
        public float StationaryEnd;         // the root stands still in [0, StationaryEnd]
        public bool Compressed;             // the fitted swings did not fit the fixed period and were shortened (rrw.mx.check counts it)

        public float HopWindowMax => Period - StationaryEnd;             // longest hop window (grows into the work part)
        public float HopDefault => math.min(HopWindowMax, Period - TShake); // default hop window: the return swing + the hold

        // Schedule with the fitted swing-out / return durations (machine s; NaN or <= 0 = nominal). The period is always
        // RRWConst.kDigCyclePeriod: a shorter fit leaves a Hold, a longer one is compressed proportionally (not below kDigSwingMin).
        public static DigSchedule Make(DigMode mode, float swing, float ret)
        {
            var s = new DigSchedule { Mode = mode, Period = RRWConst.kDigCyclePeriod };
            if (!(swing > 0f)) swing = RRWConst.kDigSwingNominal;
            if (!(ret > 0f)) ret = RRWConst.kDigSwingNominal;
            swing = math.max(RRWConst.kDigSwingMin, swing);
            ret = math.max(RRWConst.kDigSwingMin, ret);
            float fixedPart = RRWConst.kDigLowerSeconds + RRWConst.kDigPenetrateSeconds + RRWConst.kDigDragSeconds + RRWConst.kDigCurlSeconds
                              + RRWConst.kDigLiftSeconds + RRWConst.kDigDumpSeconds + RRWConst.kDigShakeSeconds;
            float room = s.Period - fixedPart;
            if (swing + ret > room + 1e-4f)
            {
                s.Compressed = true;
                float k = room / (swing + ret);
                swing = math.max(RRWConst.kDigSwingMin, swing * k);
                ret = math.max(RRWConst.kDigSwingMin, room - swing);
                if (swing + ret > room) swing = math.max(0.5f, room - ret);   // only with absurd constants
            }
            s.Swing = swing; s.Return = ret;
            s.TLower = RRWConst.kDigLowerSeconds;
            s.TPenetrate = s.TLower + RRWConst.kDigPenetrateSeconds;
            s.TDrag = s.TPenetrate + RRWConst.kDigDragSeconds;
            s.TCurl = s.TDrag + RRWConst.kDigCurlSeconds;
            s.TLift = s.TCurl + RRWConst.kDigLiftSeconds;
            s.TSwing = s.TLift + swing;
            s.TDump = s.TSwing + RRWConst.kDigDumpSeconds;
            s.TShake = s.TDump + RRWConst.kDigShakeSeconds;
            s.TReturn = s.TShake + ret;
            s.Hold = math.max(0f, s.Period - s.TReturn);
            s.BreakoutT = s.TDrag;
            s.DumpT = s.TSwing + RRWConst.kDigDumpAt * RRWConst.kDigDumpSeconds;
            s.StationaryEnd = math.min(s.Period, s.TCurl + RRWConst.kDigGroundMargin);
            return s;
        }

        public static DigSchedule Nominal(DigMode mode) => Make(mode, float.NaN, float.NaN);

        // Wrap a machine time onto the cycle grid that starts at `origin` (t - origin mod Period, in [0, Period)).
        public float CycleTime(double t, double origin)
        {
            double x = (t - origin) % Period;
            if (x < 0) x += Period;
            return (float)x;
        }

        // Key that runs at cycle time t and the fraction u in [0, 1] through it.
        public DigKey KeyAt(float t, out float u)
        {
            t = math.clamp(t, 0f, Period);
            if (t < TLower) { u = t / TLower; return DigKey.Lower; }
            if (t < TPenetrate) { u = (t - TLower) / (TPenetrate - TLower); return DigKey.Penetrate; }
            if (t < TDrag) { u = (t - TPenetrate) / (TDrag - TPenetrate); return DigKey.Drag; }
            if (t < TCurl) { u = (t - TDrag) / (TCurl - TDrag); return DigKey.Curl; }
            if (t < TLift) { u = (t - TCurl) / (TLift - TCurl); return DigKey.Lift; }
            if (t < TSwing) { u = (t - TLift) / math.max(1e-4f, TSwing - TLift); return DigKey.Swing; }
            if (t < TDump) { u = (t - TSwing) / (TDump - TSwing); return DigKey.Dump; }
            if (t < TShake) { u = (t - TDump) / (TShake - TDump); return DigKey.Shake; }
            if (t < TReturn) { u = (t - TShake) / math.max(1e-4f, TReturn - TShake); return DigKey.Return; }
            u = Hold > 1e-4f ? (t - TReturn) / Hold : 1f;
            return DigKey.Hold;
        }

        // Break mode: cycle time of strike i (0 .. kBreakStrikes - 1) = the bottom of the i-th chop inside the Drag key.
        public float StrikeT(int i)
        {
            int n = math.max(1, RRWConst.kBreakStrikes);
            i = math.clamp(i, 0, n - 1);
            return TPenetrate + (i + 0.5f) * (TDrag - TPenetrate) / n;
        }

        // True when the root may move at cycle time t (outside the stationary part).
        public bool RootMayMove(float t) => t > StationaryEnd + 1e-4f;

        // Events whose time lies in (t0, t1] (machine time) on the grid origin + k * Period. Several cycles in one window (a
        // long frame / time-lapse) report each kind once (flags); `dumps` counts the Dump events (truck load increments).
        public DigEvents EventsBetween(double t0, double t1, double origin, out int dumps)
        {
            dumps = 0;
            if (!(t1 > t0)) return DigEvents.None;
            var ev = DigEvents.None;
            double k0 = math.floor((t0 - origin) / Period);
            double k1 = math.floor((t1 - origin) / Period);
            for (double k = k0; k <= k1 && k - k0 < 64; k++)
            {
                double c = origin + k * Period;
                if (In(c + BreakoutT, t0, t1)) ev |= DigEvents.Breakout;
                if (In(c + DumpT, t0, t1)) { ev |= DigEvents.Dump; dumps++; }
                if (Mode == DigMode.Break)
                    for (int i = 0; i < RRWConst.kBreakStrikes; i++)
                        if (In(c + StrikeT(i), t0, t1)) { ev |= DigEvents.Strike; break; }
            }
            return ev;
        }

        static bool In(double x, double t0, double t1) => x > t0 && x <= t1;

        // Machine time of the n-th Dump event (n >= 1) at or after machine time `from` on the grid (truck departure planning: a truck
        // that arrived empty leaves kTruckLeaveAfterShake after the shake that follows its kBucketsPerLoad-th dump).
        public double NthDumpAfter(double from, double origin, int n)
        {
            double k = math.ceil((from - origin - DumpT) / Period);
            return origin + (k + math.max(1, n) - 1) * Period + DumpT;
        }
    }

    // Truck load contract: the load goes up by exactly one bucket per Dump event that targeted this truck.
    public static class DigLoad
    {
        // Load share (0..1) after `dumps` buckets since the truck arrived empty.
        public static float Share(int dumps) => math.saturate(math.max(0, dumps) / (float)RRWConst.kBucketsPerLoad);
        // DeliveryTruck amount after `dumps` buckets (CoalTruck01 capacity; pile steps at 30 / 60 / 100 %, verified in game).
        public static int Amount(int dumps) => (int)math.round(Share(dumps) * RRWConst.kTruckCapacity);
        public static bool Full(int dumps) => dumps >= RRWConst.kBucketsPerLoad;
    }
}
