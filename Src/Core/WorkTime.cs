using System.Collections.Generic;
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
            return math.max(min, chainLength / 1000f * perKm * ClassMult(rc));
        }

        // Road class multiplier of the work rate.
        public static float ClassMult(RoadClassInfo rc)
        {
            float mult = 1f;
            if (rc.CarLanes > 4) mult *= 1.5f;
            if (rc.Highway) mult *= 1.5f;
            if (rc.Elevated) mult *= 2f;
            if (rc.Tunnel) mult *= 3f;
            return mult;
        }

        // ---- upgrade works (mode H)

        // Hours of a partial upgrade. Every window costs its widest band (parallel bands run at the same time):
        //   raw   = chainLength / 1000 x hours per km x class multiplier
        //   share = sum over windows of max over its bands (kind factor x width x (1 - G0)) / new road width
        //   hours = raw x share x (1 + kUwStagedOverhead if traffic keeps running beside the works) + kUwSwitchHours x (windows - 1)
        // clamped to [kUwMinHours (kUwMinHoursRemark for a pure re-marking), the full rebuild's hours]. bands must carry their
        // windows (UpgradePlan.Windows). demolitionRatio = demolition hours per km / construction hours per km.
        public static float UpgradeHours(float chainLength, RoadClassInfo rc, float constructionHoursPerKm, float constructionMinHours,
                                         float demolitionRatio, IList<ChainBand> bands, int windows, float newWidth, UpgradeClass cls,
                                         bool keepsTraffic)
        {
            float full = math.max(constructionMinHours, chainLength / 1000f * constructionHoursPerKm * ClassMult(rc));
            float raw = chainLength / 1000f * constructionHoursPerKm * ClassMult(rc);
            float share = 0f;
            if (bands != null && newWidth > 0.1f)
            {
                bool remark = UpgradePlan.HasRemark(bands);
                for (int w = 0; w < math.max(1, windows); w++)
                {
                    float best = 0f;
                    for (int i = 0; i < bands.Count; i++)
                    {
                        if (bands[i].Window != w) continue;
                        bool follows = remark && bands[i].Kind != BandKind.Remark && UpgradePlan.RemarkFollows(bands, w);
                        best = math.max(best, UpgradePlan.BandWeight(bands[i], follows, demolitionRatio));
                    }
                    share += best / newWidth;
                }
            }
            float h = raw * share * (keepsTraffic ? 1f + RRWConst.kUwStagedOverhead : 1f) + RRWConst.kUwSwitchHours * math.max(0, windows - 1);
            float min = cls == UpgradeClass.Remark ? RRWConst.kUwMinHoursRemark : RRWConst.kUwMinHours;
            return math.min(math.max(h, min), full);   // never longer than a full rebuild
        }

        public static float UpgradeHours(float chainLength, RoadClassInfo rc, RRWSetting s, IList<ChainBand> bands, int windows,
                                         float newWidth, UpgradeClass cls, bool keepsTraffic) =>
            UpgradeHours(chainLength, rc, s.ConstructionHoursPerKm, s.ConstructionMinHours, DemolitionRatio(s), bands, windows, newWidth, cls, keepsTraffic);

        public static float DemolitionRatio(RRWSetting s) => s.ConstructionHoursPerKm > 0 ? s.DemolitionHoursPerKm / (float)s.ConstructionHoursPerKm : 0.5f;

        // Rush price floor share of an upgrade: its work frames over the full rebuild's (1 for every other site).
        public static float FloorShare(uint upgradeRequired, uint fullRebuildRequired) =>
            fullRebuildRequired == 0 ? 1f : math.saturate(upgradeRequired / (float)fullRebuildRequired);

        // Work frames a full rebuild (construction) of the chain would need: the reference of an upgrade's floor share.
        public static uint FullRebuildFrames(float chainLength, RoadClassInfo rc, float constructionHoursPerKm, float constructionMinHours) =>
            FramesFromHours(math.max(constructionMinHours, chainLength / 1000f * constructionHoursPerKm * ClassMult(rc)));

        public static uint FullRebuildFrames(float chainLength, RoadClassInfo rc, RRWSetting s) =>
            FullRebuildFrames(chainLength, rc, s.ConstructionHoursPerKm, s.ConstructionMinHours);

        // The floor share of a site: an upgrade site's required frames over the full rebuild's (deterministic: both derive from
        // the saved chain length and the road class), 1 for every other site.
        public static float RushFloorShare(in RoadWorksSite site, uint fullRebuildRequired) =>
            site.IsUpgrade ? FloorShare(site.m_WorkRequired, fullRebuildRequired) : 1f;

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

        // Same, with the length-based floor scaled by floorShare (upgrade works: FloorShare; 1 = the overload above).
        public static int RushCost(int paidCost, float lengthM, float progress, int rushPercent, float floorShare)
        {
            int baseCost = math.max(paidCost, (int)(lengthM * 40f * math.saturate(floorShare)));
            return (int)(baseCost * math.saturate(1f - progress) * rushPercent / 100f);
        }

        // Rush price of one edge of a site (UI panel, Director Rush request): an upgrade site's floor is scaled by its share of a
        // full rebuild (fullRebuildRequired = FullRebuildFrames of its chain with the NEW road's class); every other site pays the
        // plain RushCost (bit for bit).
        public static int RushCost(in RoadWorksSite site, float edgeLengthM, float progress, int rushPercent, uint fullRebuildRequired)
        {
            if (!site.IsUpgrade) return RushCost(site.m_PaidCost, edgeLengthM, progress, rushPercent);
            return RushCost(math.max(0, site.m_PaidCost), edgeLengthM, progress, rushPercent, RushFloorShare(site, fullRebuildRequired));
        }

        // Refund when cancelling a construction (unbuilt share of what was paid).
        public static int CancelRefund(int paidCost, float progress, bool instant) =>
            instant ? paidCost : (int)(paidCost * math.saturate(1f - progress));
    }
}
