using Game.Common;

namespace RealisticRoadWorks.V3.Props
{
    // Blink phase of an "RRW Barrier Beacon" (verified in game). EffectTransformSystem evaluates the beacon's EffectAnimation at
    //   ((frameIndex + PseudoRandomSeed(seed).GetRandom(kLightState).NextUInt(durationFrames)) % durationFrames) / durationFrames
    // so the prop's PseudoRandomSeed fixes its phase. SeedForPhase uses a 60-entry table (first seed per phase),
    // built once on first use (stops as soon as every phase has a seed, usually after a few hundred seeds).
    internal static class BeaconPhase
    {
        private static ushort[] s_Table;

        public static uint Duration => RRWConst.kBeaconPeriodFrames;

        public static uint PhaseOf(ushort seed) =>
            new PseudoRandomSeed(seed).GetRandom(PseudoRandomSeed.kLightState).NextUInt(Duration);

        public static ushort SeedForPhase(uint phase)
        {
            uint dur = Duration;
            if (dur == 0) return 1;
            if (s_Table == null || s_Table.Length != dur)
            {
                var t = new ushort[dur];
                uint found = 0;
                for (int s = 1; s < 65536 && found < dur; s++)
                {
                    uint ph = PhaseOf((ushort)s);
                    if (ph < dur && t[ph] == 0) { t[ph] = (ushort)s; found++; }
                }
                s_Table = t;
            }
            ushort r = s_Table[phase % dur];
            return r == 0 ? (ushort)1 : r;
        }

        // Closed lines: a running light across the closed end. Beacon i of the line (counted from CarriageLo) gets
        // phase (60 * 1000 - i * kBeaconChaseStepFrames) mod 60.
        public static ushort Chase(int beaconIndex)
        {
            int dur = (int)Duration;
            if (dur <= 0) return 1;
            int ph = (dur * 1000 - beaconIndex * RRWConst.kBeaconChaseStepFrames) % dur;
            if (ph < 0) ph += dur;
            return SeedForPhase((uint)ph);
        }

        // Deterministic "random" seed from the project seed and the slot key: a respawn keeps the look and the phase.
        public static ushort Random(uint projectSeed, int key)
        {
            uint h = Hash(projectSeed, (uint)key);
            ushort s = (ushort)(h ^ (h >> 16));
            return s == 0 ? (ushort)1 : s;
        }

        public static uint Hash(uint a, uint b)
        {
            uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u);
            h ^= h >> 15; h *= 0x85EBCA77u;
            h ^= h >> 13; h *= 0xC2B2AE3Du;
            h ^= h >> 16;
            return h == 0 ? 1u : h;
        }
    }
}
