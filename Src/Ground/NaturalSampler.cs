using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Ground
{
    // Natural ground estimates for the MORPH brackets.
    // natAbove = max(h - grade), natBelow = max(grade - h) over the edge, grade = curve Y + grade offset.
    // - Centre estimate: CPU heights (TerrainSystem cascade readback, ~3.5 m texels) at lateral 0 and +-HalfWidth.
    //   Only meaningful while the edge's profile is NATURAL (no flattening, clip deep): construction C0.
    // - Verge estimate: CPU heights at +-(HalfWidth + terrain smoothing width + 1), outside every road influence; the
    //   natural ground under the road is taken as the linear interpolation between the two verges. Works under a
    //   flattened (visible) road too: demolition D0.
    // - GPU estimate: async readback of the BASE heightmap (natural incl. terraforming, without road flattening),
    //   ported from the terrain prototype's GPU probe. Demolition D0, retried at D1 start.
    public static class NaturalSampler
    {
        public const float kVergeExtra = 1f;
        public const float kDefaultSmoothing = 8f;
        public const int kMaxSamplesPerEdge = 400;   // 4 m step -> 1.6 km; longer edges use a coarser step
        public const int kMaxGpuInFlight = 4;
        public static int GpuInFlight;

        public struct Geometry
        {
            public EdgeArc Arc;
            public float HalfWidth;
            public float GradeOffset;
            public float VergeLateral;
        }

        public static bool TryGeometry(EntityManager em, Entity edge, EdgeRecord rec, float gradeOffset, out Geometry g)
        {
            g = default;
            if (!EcsUtil.Alive(em, edge) || !em.HasComponent<Curve>(edge)) return false;
            EdgeArc arc = rec != null && rec.Arc != null ? rec.Arc : new EdgeArc(em.GetComponentData<Curve>(edge).m_Bezier);
            float hw = rec != null && rec.Section.HalfWidth > 0.5f ? rec.Section.HalfWidth : EcsUtil.CompositionWidth(em, edge) * 0.5f;
            float smooth = kDefaultSmoothing;
            if (em.HasComponent<PrefabRef>(edge))
            {
                Entity prefab = em.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (em.HasComponent<NetData>(prefab))
                {
                    float w = NetUtils.GetTerrainSmoothingWidth(em.GetComponentData<NetData>(prefab));
                    if (!float.IsNaN(w) && w >= 0f && w < 200f) smooth = w;
                }
            }
            g = new Geometry
            {
                Arc = arc,
                HalfWidth = math.max(1f, hw),
                GradeOffset = float.IsNaN(gradeOffset) || math.abs(gradeOffset) > 5f ? RRWConst.kProfileGradeBias : gradeOffset,
                VergeLateral = math.max(1f, hw) + smooth + kVergeExtra,
            };
            return true;
        }

        private static int SampleCount(float length)
        {
            int n = (int)math.ceil(length / RRWConst.kNaturalSampleStep);
            return math.clamp(n, 1, kMaxSamplesPerEdge);
        }

        private static void Acc(float h, float grade, ref float above, ref float below, ref int n)
        {
            if (float.IsNaN(h) || float.IsInfinity(h)) return;
            above = math.max(above, h - grade);
            below = math.max(below, grade - h);
            n++;
        }

        // CPU estimate. centre = include the centre/edge samples (only valid in NATURAL), verge = include the verge
        // interpolation. Returns false when no sample could be taken.
        public static bool SampleCpu(TerrainSystem ts, in Geometry g, bool centre, bool verge, out float above, out float below, out int count)
        {
            above = float.MinValue; below = float.MinValue; count = 0;
            if (ts == null || g.Arc == null) return false;
            var hd = ts.GetHeightData();
            if (!hd.isCreated || hd.resolution.x <= 2) return false;
            float len = g.Arc.Length;
            int n = SampleCount(len);
            for (int i = 0; i <= n; i++)
            {
                float s = len * i / n;
                float3 p = g.Arc.Position(s);
                float3 r = g.Arc.Right(s);
                float grade = p.y + g.GradeOffset;
                if (centre)
                {
                    Acc(TerrainUtils.SampleHeight(ref hd, p - r * g.HalfWidth), grade, ref above, ref below, ref count);
                    Acc(TerrainUtils.SampleHeight(ref hd, p), grade, ref above, ref below, ref count);
                    Acc(TerrainUtils.SampleHeight(ref hd, p + r * g.HalfWidth), grade, ref above, ref below, ref count);
                }
                if (verge)
                {
                    float hl = TerrainUtils.SampleHeight(ref hd, p - r * g.VergeLateral);
                    float hr = TerrainUtils.SampleHeight(ref hd, p + r * g.VergeLateral);
                    if (float.IsNaN(hl) || float.IsNaN(hr)) continue;
                    float span = 2f * g.VergeLateral;
                    Acc(math.lerp(hl, hr, (g.VergeLateral - g.HalfWidth) / span), grade, ref above, ref below, ref count);
                    Acc(math.lerp(hl, hr, 0.5f), grade, ref above, ref below, ref count);
                    Acc(math.lerp(hl, hr, (g.VergeLateral + g.HalfWidth) / span), grade, ref above, ref below, ref count);
                }
            }
            if (count == 0) return false;
            above = math.max(0f, above);
            below = math.max(0f, below);
            return true;
        }

        // ---------------------------------------------------------------- GPU base heightmap

        // A rectangle of an R16 height texture read back from the GPU.
        public sealed class HeightGrid
        {
            public ushort[] Data;
            public int X0, Z0, W, H;
            public float4 Range;
            public float Sx, Sz;
            public float2 HeightScaleOffset;

            public float Sample(float3 p)
            {
                float u = (p.x - Range.x) * Sx - 0.5f - X0;
                float v = (p.z - Range.y) * Sz - 0.5f - Z0;
                int i = (int)math.floor(u), j = (int)math.floor(v);
                float fu = math.saturate(u - i), fv = math.saturate(v - j);
                int i0 = math.clamp(i, 0, W - 1), i1 = math.clamp(i + 1, 0, W - 1);
                int j0 = math.clamp(j, 0, H - 1), j1 = math.clamp(j + 1, 0, H - 1);
                float a = Data[j0 * W + i0], b = Data[j0 * W + i1], c = Data[j1 * W + i0], d = Data[j1 * W + i1];
                float raw = math.lerp(math.lerp(a, b, fu), math.lerp(c, d, fu), fv);
                return raw / 65535f * HeightScaleOffset.x + HeightScaleOffset.y;
            }

            public bool Contains(float3 p) => p.x >= Range.x && p.x <= Range.z && p.z >= Range.y && p.z <= Range.w;
        }

        public static float4 BaseRange(TerrainSystem ts) => new float4(ts.playableOffset, ts.playableOffset + ts.playableArea);

        public static float4 CascadeRange(TerrainSystem ts, int slice)
        {
            ts.GetCascadeInfo(out _, out _, out float4x4 areas, out _, out _);
            return new float4(areas.c0[slice], areas.c1[slice], areas.c2[slice], areas.c3[slice]);
        }

        // Finest cascade slice whose range contains the whole area (-1 if none).
        public static int FinestSlice(TerrainSystem ts, Bounds2 area)
        {
            ts.GetCascadeInfo(out int lodCount, out int baseLod, out float4x4 areas, out _, out _);
            for (int s = math.min(3, lodCount - 1); s >= baseLod; s--)
            {
                var r = new float4(areas.c0[s], areas.c1[s], areas.c2[s], areas.c3[s]);
                if (r.z <= r.x || r.w <= r.y) continue;
                if (area.min.x >= r.x && area.min.y >= r.y && area.max.x <= r.z && area.max.y <= r.w) return s;
            }
            return -1;
        }

        // Async readback of a texture rectangle covering `area`. Callbacks run on the main thread (Unity player loop),
        // outside the ECS update: they must not touch the EntityManager.
        public static bool Request(UnityEngine.Texture tex, int slice, float4 range, Bounds2 area, float2 hso, Action<HeightGrid> done, Action<string> fail)
        {
            if (tex == null) { fail("texture null"); return false; }
            if (!UnityEngine.SystemInfo.supportsAsyncGPUReadback) { fail("async readback unsupported"); return false; }
            int W = tex.width, H = tex.height;
            float sx = W / math.max(1e-3f, range.z - range.x), sz = H / math.max(1e-3f, range.w - range.y);
            int x0 = math.clamp((int)math.floor((area.min.x - range.x) * sx - 0.5f) - 1, 0, W - 1);
            int x1 = math.clamp((int)math.ceil((area.max.x - range.x) * sx - 0.5f) + 1, 0, W - 1);
            int z0 = math.clamp((int)math.floor((area.min.y - range.y) * sz - 0.5f) - 1, 0, H - 1);
            int z1 = math.clamp((int)math.ceil((area.max.y - range.y) * sz - 0.5f) + 1, 0, H - 1);
            int w = x1 - x0 + 1, h = z1 - z0 + 1;
            if (w <= 0 || h <= 0 || w * h > 1 << 20) { fail("bad rect " + w + "x" + h); return false; }
            UnityEngine.Rendering.AsyncGPUReadback.Request(tex, 0, x0, w, z0, h, slice, 1, req =>
            {
                try
                {
                    if (req.hasError) { fail("readback error"); return; }
                    var data = req.GetData<ushort>();
                    if (data.Length < w * h) { fail("short data " + data.Length); return; }
                    done(new HeightGrid
                    {
                        Data = data.ToArray(), X0 = x0, Z0 = z0, W = w, H = h, Range = range, Sx = sx, Sz = sz, HeightScaleOffset = hso,
                    });
                }
                catch (Exception e) { RRWLog.ErrorOnce("ground gpu readback", e); try { fail("exception"); } catch { } }
            });
            return true;
        }

        // GPU base-heightmap estimate of the natural ground under the road (lateral 0 and +-HalfWidth every 4 m).
        // done(above, below, samples) / fail(message) run on the main thread outside the ECS update.
        public static bool RequestGpuEstimate(TerrainSystem ts, in Geometry g, Action<float, float, int> done, Action<string> fail)
        {
            if (ts == null || g.Arc == null) { fail("no terrain/geometry"); return false; }
            UnityEngine.Texture tex;
            try { tex = ts.heightmap; } catch { tex = null; }
            if (tex == null) { fail("base heightmap not available"); return false; }
            float len = g.Arc.Length;
            int n = SampleCount(len);
            var pts = new float3[(n + 1) * 3];
            var grades = new float[(n + 1) * 3];
            var area = new Bounds2(new float2(float.MaxValue), new float2(float.MinValue));
            for (int i = 0; i <= n; i++)
            {
                float s = len * i / n;
                float3 p = g.Arc.Position(s);
                float3 r = g.Arc.Right(s);
                float grade = p.y + g.GradeOffset;
                for (int k = -1; k <= 1; k++)
                {
                    float3 q = p + r * (k * g.HalfWidth);
                    int idx = i * 3 + k + 1;
                    pts[idx] = q;
                    grades[idx] = grade;
                    area = area | new Bounds2(q.xz, q.xz);
                }
            }
            return Request(tex, 0, BaseRange(ts), area, ts.heightScaleOffset, grid =>
            {
                float above = float.MinValue, below = float.MinValue;
                int cnt = 0;
                for (int i = 0; i < pts.Length; i++)
                {
                    if (!grid.Contains(pts[i])) continue;
                    Acc(grid.Sample(pts[i]), grades[i], ref above, ref below, ref cnt);
                }
                if (cnt == 0) { fail("no sample inside the heightmap"); return; }
                done(math.max(0f, above), math.max(0f, below), cnt);
            }, fail);
        }

        // Product path: the result is queued in GroundTable.NaturalResults; ExcavationSystem applies it next update.
        public static bool RequestGpu(TerrainSystem ts, Entity edge, in Geometry g, WorksPhase phase)
        {
            if (GpuInFlight >= kMaxGpuInFlight) return false;
            byte ph = (byte)phase;
            GpuInFlight++;
            GroundTable.GpuRequests++;
            return RequestGpuEstimate(ts, g, (above, below, cnt) =>
            {
                GpuInFlight = math.max(0, GpuInFlight - 1);
                lock (GroundTable.NaturalLock)
                    GroundTable.NaturalResults.Add(new NaturalResult { Edge = edge, Phase = ph, Source = "gpu base n=" + cnt, Above = above, Below = below });
            }, msg =>
            {
                GpuInFlight = math.max(0, GpuInFlight - 1);
                GroundTable.GpuFailures++;
                RRWLog.Verbose("ground gpu natural readback failed edge=" + RRWLog.E(edge) + ": " + msg);
                lock (GroundTable.NaturalLock)
                    GroundTable.NaturalResults.Add(new NaturalResult { Edge = edge, Phase = ph, Source = "gpu-failed", Above = float.NaN, Below = float.NaN });
            });
        }
    }
}
