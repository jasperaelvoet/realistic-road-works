using System;
using System.Collections.Generic;
using System.Globalization;
using Game.SceneFlow;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.UI
{
    // Text formatting for the info section and the tool tooltips. All strings the .mjs shows are made
    // here, ready to display. Called only when the panel or a tooltip is (re)built, never per frame.
    public static class UiFormat
    {
        // UI strings (the keys live in Core's RRWText / RRWLocaleEN; these aliases keep the call sites short).
        public const string KFinishToday = RRWText.FinishToday;
        public const string KFinishTomorrow = RRWText.FinishTomorrow;
        public const string KFinishDays = RRWText.FinishDays;
        public const string KFinishUnknown = RRWText.FinishUnknown;
        public const string KConfirmCancel = RRWText.ConfirmCancel;
        public const string KTipDemolitionOpen = RRWText.TipDemolitionOpen;
        public const string KTipCancelBuild = RRWText.TipCancelBuild;
        public const string KCompleteText = RRWText.CompleteText;

        // Active-language text with the English default from RRWText.
        public static string Get(string key) => RRWText.Get(key);

        public static string Format(string key, params object[] args)
        {
            string fmt = Get(key);
            try { return string.Format(CultureInfo.InvariantCulture, fmt, args); }
            catch { return fmt; }
        }

        // "¢48,200" (the game's money sign; negative amounts never shown).
        public static string Money(long amount)
        {
            if (amount < 0) amount = 0;
            return "¢" + amount.ToString("N0", CultureInfo.InvariantCulture);
        }

        // Crew work time: "40 min", "7 h 41 min", "21 h", "112 h".
        public static string Duration(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0.0) hours = 0.0;
            long minutes = (long)Math.Round(hours * 60.0);
            if (minutes < 1) minutes = 1;
            if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + " min";
            long h = minutes / 60, m = minutes % 60;
            if (h >= 100 || m == 0) return h.ToString(CultureInfo.InvariantCulture) + " h";
            return h.ToString(CultureInfo.InvariantCulture) + " h " + m.ToString("00", CultureInfo.InvariantCulture) + " min";
        }

        // Tooltip hours: "0.5", "7.7", "48".
        public static string Hours(float hours)
        {
            if (float.IsNaN(hours) || float.IsInfinity(hours) || hours < 0f) hours = 0f;
            return hours < 10f ? hours.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(hours).ToString("0", CultureInfo.InvariantCulture);
        }

        // Clock text of a normalised day time: "07:00".
        public static string Clock(double normalizedTime)
        {
            double t = normalizedTime - Math.Floor(normalizedTime);
            int minutes = (int)Math.Floor(t * 1440.0 + 1e-6) % 1440;
            return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        // Calendar finish relative to now: "14:30", "tomorrow 07:41", "in 3 days, 11:20".
        // CS2 days are not weekdays (a game "day" also advances the month), so no weekday names.
        public static string Finish(double nowNormalized, double calendarFrames)
        {
            if (double.IsNaN(calendarFrames) || double.IsInfinity(calendarFrames) || calendarFrames < 0.0) return Get(KFinishUnknown);
            double now = nowNormalized - Math.Floor(nowNormalized);
            double end = now + calendarFrames / RRWConst.kFramesPerDay;
            int days = (int)Math.Floor(end);
            string clock = Clock(end);
            if (days <= 0) return Format(KFinishToday, clock);
            if (days == 1) return Format(KFinishTomorrow, clock);
            return Format(KFinishDays, days.ToString(CultureInfo.InvariantCulture), clock);
        }

        // Percentage within a phase: floor, so "100 %" never shows before the phase actually ends.
        public static int PhasePercent(float f) => math.clamp((int)math.floor(f * 100f), 0, 99);

        public static string PhaseName(WorksPhase ph) => RRWText.Get(PhasePlan.PhaseKey(ph));
    }
}
