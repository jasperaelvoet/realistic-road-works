using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // Road attributes that change the work rate. Filled by Tools from the prefab/composition.
    public struct RoadClassInfo
    {
        public int CarLanes;          // both directions
        public bool Highway;
        public bool Elevated;         // elevated or bridge
        public bool Tunnel;
        public float CompositionWidth;
    }

    // Durations, shift windows, ETA and money. Pure functions.
    public static class WorkTime
    {
        // In-game work hours for a project of the given chain length.
        public static float WorkHours(WorksKind kind, float chainLength, RoadClassInfo rc, RRWSetting s)
        {
            float perKm = kind == WorksKind.Construction ? s.ConstructionHoursPerKm : s.DemolitionHoursPerKm;
            float min = kind == WorksKind.Construction ? s.ConstructionMinHours : s.DemolitionMinHours;
            float mult = 1f;
            if (rc.CarLanes > 4) mult *= 1.5f;
            if (rc.Highway) mult *= 1.5f;
            if (rc.Elevated) mult *= 2f;
            if (rc.Tunnel) mult *= 3f;
            return math.max(min, chainLength / 1000f * perKm * mult);
        }

        public static uint FramesFromHours(float hours) => (uint)math.max(1.0, math.round(hours * RRWConst.kFramesPerHour));
        public static float HoursFromFrames(double frames) => (float)(frames / RRWConst.kFramesPerHour);

        // Shift window in normalised day time [start, end).
        public static void ShiftBounds(ShiftWindow w, out float start, out float end)
        {
            switch (w)
            {
                case ShiftWindow.Extended: start = 6f / 24f; end = 22f / 24f; break;
                case ShiftWindow.AllDay: start = 0f; end = 1f; break;
                default: start = 7f / 24f; end = 19f / 24f; break;
            }
        }

        // Design decision: crews work around the clock, every day - no night stops, no rest days.
        // The shift setting is kept (hidden) for a possible future option but is ignored here.
        public static ShiftWindow Effective(ShiftWindow w, bool rushed) => ShiftWindow.AllDay;

        public static bool InShift(float normalizedTime, ShiftWindow w)
        {
            ShiftBounds(w, out float a, out float b);
            float t = normalizedTime - math.floor(normalizedTime);
            return t >= a && t < b;
        }

        // Hours until the shift (re)starts; 0 when inside it.
        public static float HoursUntilShift(float normalizedTime, ShiftWindow w)
        {
            if (InShift(normalizedTime, w)) return 0f;
            ShiftBounds(w, out float a, out _);
            float t = normalizedTime - math.floor(normalizedTime);
            float d = a - t;
            if (d < 0f) d += 1f;
            return d * 24f;
        }

        // Work frames accrued per simulation frame while working.
        public static float Rate(bool rushed, float devScale) => (rushed ? RRWConst.kRushRateMultiplier : 1f) * math.max(0f, devScale);

        // Calendar (simulation) frames until `remainingWork` work frames are done, walking shift windows.
        public static double CalendarFramesToFinish(float normalizedTime, double remainingWork, ShiftWindow w, float rate)
        {
            if (remainingWork <= 0.0) return 0.0;
            if (rate <= 0f) return double.PositiveInfinity;
            ShiftBounds(w, out float a, out float b);
            double day = RRWConst.kFramesPerDay;
            double t = normalizedTime - math.floor(normalizedTime);
            double total = 0.0;
            double need = remainingWork / rate;   // working calendar frames needed
            for (int guard = 0; guard < 4000 && need > 0.0; guard++)
            {
                if (t < a) { total += (a - t) * day; t = a; continue; }
                if (t >= b) { total += (1.0 - t + a) * day; t = a; continue; }
                double avail = (b - t) * day;
                if (avail >= need) { total += need; need = 0.0; break; }
                total += avail; need -= avail; t = b;
            }
            return total;
        }

        // Rush price: RushCostPercent of what the edge cost (or a length-based estimate for demolitions), scaled by the work left.
        public static int RushCost(int paidCost, float lengthM, float progress, int rushPercent)
        {
            int baseCost = math.max(paidCost, (int)(lengthM * 40f));
            return (int)(baseCost * math.saturate(1f - progress) * rushPercent / 100f);
        }

        // Refund when cancelling a construction (unbuilt share of what was paid).
        public static int CancelRefund(int paidCost, float progress, bool instant) =>
            instant ? paidCost : (int)(paidCost * math.saturate(1f - progress));
    }
}
