using System.Globalization;
using Game.Prefabs;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // TerrainComposition override written onto the cloned compositions of a works edge (verified in game; port of the
    // terrain prototype's profile). Values are metres relative to the road GRADE that TerrainSystem uses
    // (= edge curve Y + RRWConst.kProfileGradeBias). float3 components are (left edge, middle, right edge).
    // Semantics: final terrain = clamp(natural, grade + Max, grade + Min): Min = cut cap, Max = fill floor.
    // All finite values are quantised to 1/16 m (R16 cascade step) so a changed profile always means a visible step.
    public struct TerrainProfile
    {
        public const float Big = 1000000f;

        public TerrainProfileKind Kind;
        public float3 Min, Max;
        public float2 Clip;
        // MORPH arguments this profile was built from (requested by Ground): Bed(y) = (1, Q(y)), Morph(t, y) = (t, y),
        // Natural / Vanilla / ClipOnly = (0, 0). Ground mirrors them into RoadWorksGround.m_AppliedT/Y (cancel-restore input).
        // Not part of SameAs (they never change the written TerrainComposition on their own).
        public float MorphT, MorphY;

        public static float Q(float v) => math.round(v * 16f) / 16f;

        // Clip slab far below any terrain we produce (constant, so a profile change never moves the clip).
        static float2 DeepClip(float unused) => new float2(-RRWConst.kDeepClip, -RRWConst.kDeepClip + 1f);

        public static TerrainProfile Vanilla => new TerrainProfile { Kind = TerrainProfileKind.Vanilla };

        // Original ground: no clamp, clip far underground (no hole, no imprint).
        public static TerrainProfile Natural() =>
            new TerrainProfile { Kind = TerrainProfileKind.Natural, Min = new float3(Big), Max = new float3(-Big), Clip = DeepClip(2f) };

        // Flat floor at y (negative = below grade), road edges at grade.
        public static TerrainProfile Bed(float y)
        {
            y = Q(y);
            return new TerrainProfile
            {
                Kind = TerrainProfileKind.Bed,
                Min = new float3(0f, y, 0f),
                Max = new float3(0f, y, 0f),
                Clip = DeepClip(math.max(2f, -y + 2f)),
                MorphT = 1f,
                MorphY = y,
            };
        }

        // MORPH(t, y): t = 0 -> natural brackets (no visible change as long as natural ground lies within [lo, hi]),
        // t = 1 -> BED(y). Sampled natural ground: hi = natAbove + 0.5, lo = -(natBelow + 0.5), linear in t.
        // Unknown natural ground (NaN): brackets +-kUnsampledBracket that decay with (1 - t)^4, so no pop at t = 0 and the
        // visible part of the morph is spread over t ~ 0..0.7 instead of the last 4 %.
        public static TerrainProfile Morph(float t, float y, float natAbove, float natBelow)
        {
            t = math.saturate(t);
            if (t >= 1f) return Bed(y);
            bool sampledHi = !float.IsNaN(natAbove), sampledLo = !float.IsNaN(natBelow);
            float hi = sampledHi ? math.max(0f, natAbove) + RRWConst.kNaturalMargin : RRWConst.kUnsampledBracket;
            float lo = sampledLo ? -(math.max(0f, natBelow) + RRWConst.kNaturalMargin) : -RRWConst.kUnsampledBracket;
            float kHi = sampledHi ? 1f - t : Pow4(1f - t);   // weight of the bracket (1 at t = 0, 0 at t = 1)
            float kLo = sampledLo ? 1f - t : Pow4(1f - t);
            var p = new TerrainProfile
            {
                Kind = TerrainProfileKind.Morph,
                Min = new float3(Q(hi * kHi), Q(y + (hi - y) * kHi), Q(hi * kHi)),
                Max = new float3(Q(lo * kLo), Q(y + (lo - y) * kLo), Q(lo * kLo)),
                Clip = DeepClip(math.max(2f, -lo + 2f)),
                MorphT = t,
                MorphY = y,
            };
            return p;
        }

        static float Pow4(float x) { x = x * x; return x * x; }

        // Node ends next to edges that are not dug the same way: vanilla flattening at grade, only the clip moved.
        public TerrainProfile ClipOnly() =>
            new TerrainProfile { Kind = TerrainProfileKind.ClipOnly, Min = float3.zero, Max = float3.zero, Clip = Kind == TerrainProfileKind.Vanilla ? DeepClip(2f) : Clip };

        // Middle cap / floor relative to grade (for floor heights). Natural: +-inf.
        public float MiddleCap => Kind == TerrainProfileKind.Natural ? float.PositiveInfinity : Kind == TerrainProfileKind.Vanilla ? 0f : Min.y;
        public float MiddleFloor => Kind == TerrainProfileKind.Natural ? float.NegativeInfinity : Kind == TerrainProfileKind.Vanilla ? 0f : Max.y;

        // Shallowness used for node-end arbitration (higher = shallower). Natural counts as shallowest.
        public float Shallowness => Kind == TerrainProfileKind.Natural ? Big : Kind == TerrainProfileKind.Vanilla ? Big : Min.y;

        public bool SameAs(TerrainProfile o) =>
            Kind == o.Kind && math.all(Min == o.Min) && math.all(Max == o.Max) && math.all(Clip == o.Clip);

        // vanilla (0 if the source composition has no TerrainComposition) + this override.
        public TerrainComposition Apply(TerrainComposition v) => new TerrainComposition
        {
            m_WidthOffset = v.m_WidthOffset,
            m_ClipHeightOffset = v.m_ClipHeightOffset + Clip,
            m_MinHeightOffset = v.m_MinHeightOffset + Min,
            m_MaxHeightOffset = v.m_MaxHeightOffset + Max,
        };

        static string F(float v) => math.abs(v) >= Big * 0.5f ? (v > 0 ? "+big" : "-big") : v.ToString("0.###", CultureInfo.InvariantCulture);
        public override string ToString() =>
            Kind.ToString().ToLowerInvariant() + " min=(" + F(Min.x) + "," + F(Min.y) + "," + F(Min.z) + ") max=(" + F(Max.x) + "," + F(Max.y) + "," + F(Max.z) + ") clip=(" + F(Clip.x) + "," + F(Clip.y) + ")";
    }
}
