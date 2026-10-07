using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;

namespace RealisticRoadWorks.V3
{
    // Car lanes that mode H moved sideways onto temporary lanes (Traffic: TrafficLaneShift is the only writer). Every module
    // measures lanes with their ORIGINAL curve (MeasureCurve: EcsUtil.ProbeLane / LaneCentre / LaneSignature, the layout
    // reader), so bands, drops, sub-strips and machine safety keep referring to the lanes of the road layout; only code that
    // needs where cars actually drive (marker clearance, vehicles) reads the live Curve.
    public static class LaneShiftRegistry
    {
        public struct Entry
        {
            public Bezier4x3 Original;   // the curve the game generated
            public Bezier4x3 Applied;    // the curve written last (while it is still on the lane the lane counts as moved)
        }

        private static readonly Dictionary<Entity, Entry> s_Lanes = new Dictionary<Entity, Entry>();
        private static readonly HashSet<Entity> s_Relocated = new HashSet<Entity>();

        // A lane of a closed half that drives on a temporary lane of the open half: the closure treats it as open, the drain
        // check does not count its cars (they are on the other half).
        public static bool Relocated(Entity lane) => s_Relocated.Count > 0 && s_Relocated.Contains(lane);
        public static void SetRelocated(Entity lane, bool on) { if (on) s_Relocated.Add(lane); else s_Relocated.Remove(lane); }

        // A lane of the open half that has no temporary lane while the other half is resurfaced: closed like a closed-half lane.
        private static readonly HashSet<Entity> s_Retired = new HashSet<Entity>();
        public static bool Retired(Entity lane) => s_Retired.Count > 0 && s_Retired.Contains(lane);
        public static void SetRetired(Entity lane, bool on) { if (on) s_Retired.Add(lane); else s_Retired.Remove(lane); }

        public static int Count => s_Lanes.Count;
        public static IEnumerable<KeyValuePair<Entity, Entry>> All => s_Lanes;

        public static void Set(Entity lane, Bezier4x3 original, Bezier4x3 applied) =>
            s_Lanes[lane] = new Entry { Original = original, Applied = applied };

        public static bool TryGet(Entity lane, out Entry e) => s_Lanes.TryGetValue(lane, out e);
        public static void Remove(Entity lane) { s_Lanes.Remove(lane); s_Relocated.Remove(lane); }
        public static void Clear() { s_Lanes.Clear(); s_Relocated.Clear(); s_Retired.Clear(); }

        // The lane's curve as the road layout has it: the original while our moved curve is on the lane, else the live curve
        // (the game regenerated the lane: that is the new original).
        public static Bezier4x3 MeasureCurve(EntityManager em, Entity lane)
        {
            var live = em.GetComponentData<Curve>(lane).m_Bezier;
            if (s_Lanes.Count == 0 || !s_Lanes.TryGetValue(lane, out var e)) return live;
            return Same(live, e.Applied) ? e.Original : live;
        }

        public static bool Same(in Bezier4x3 a, in Bezier4x3 b) =>
            Unity.Mathematics.math.distancesq(a.a, b.a) < 1e-4f && Unity.Mathematics.math.distancesq(a.b, b.b) < 1e-4f
            && Unity.Mathematics.math.distancesq(a.c, b.c) < 1e-4f && Unity.Mathematics.math.distancesq(a.d, b.d) < 1e-4f;
    }
}
