#if DEVTOOLS
using System;
using Game.Net;
using Unity.Mathematics;

// rrw.surf.line (look tests for the yellow temporary lines): test lines on any road, available in the product dev
// build. The dev command only queues a request (SurfaceState.DevLineRequests); the areas are spawned here, at Modification1,
// through the same CreationDefinition path as every product strip (LivePath at Mod4, never saved). Test lines use their own
// DEVTOOLS clones "RRW Line Test <Y1|Y2|Y3|BLK> <r50|r01>" (registered in the main menu next to the product clones), so the
// tests never change the product's yellow lines: round= picks the r50 (0.5, the old look) or r01 (0.01) clone, any other value
// restyles the r50 clone at runtime; queue= / prio= override the whole test clone (SurfaceMaterialSystem).
// m_Site = Null (DerivedGc never collects them); `rrw.surf.line clear [tag]` deletes them.
namespace RealisticRoadWorks.V3.Surfaces
{
    public partial class SurfaceAreaSystem
    {
        partial void ProcessDevLines()
        {
            if (SurfaceState.DevLineClear != null)
            {
                string tag = SurfaceState.DevLineClear;
                SurfaceState.DevLineClear = null;
                int n = 0, lines = 0;
                for (int i = SurfaceState.DevLines.Count - 1; i >= 0; i--)
                {
                    var dl = SurfaceState.DevLines[i];
                    if (tag.Length > 0 && dl.Tag != tag) continue;
                    foreach (var t in dl.Pieces) { DeleteArea(t); n++; }
                    SurfaceState.DevLines.RemoveAt(i);
                    lines++;
                }
                RRWLog.Info("surfaces: rrw.surf.line clear" + (tag.Length > 0 ? " tag=" + tag : "") + ": " + lines + " test line(s), " + n + " area(s) removed");
            }
            if (SurfaceState.DevLineRequests.Count == 0) return;
            for (int i = 0; i < SurfaceState.DevLineRequests.Count; i++)
            {
                try { SpawnDevLine(SurfaceState.DevLineRequests[i]); }
                catch (Exception e) { RRWLog.ErrorOnce("surfaces dev line", e); }
            }
            SurfaceState.DevLineRequests.Clear();
        }

        private void SpawnDevLine(DevLineRequest q)
        {
            if (!m_Em.Exists(q.Edge) || !m_Em.HasComponent<Curve>(q.Edge))
            {
                RRWLog.Info("surfaces: rrw.surf.line FAILED: edge " + RRWLog.E(q.Edge) + " has no curve");
                return;
            }
            // clone: r01 for the product default / round=0.01, r50 for round=0.5, r50 restyled for any other value
            bool r50 = false;
            float round = float.IsNaN(q.Round) ? RRWGates.TempLineRoundness : q.Round;
            if (math.abs(round - 0.5f) < 0.003f) r50 = true;
            else if (math.abs(round - 0.01f) >= 0.003f) r50 = true;
            string name = SurfaceState.DevLineClone(q.Kind, r50);
            if (!SurfaceState.Clones.TryGetValue(name, out var clone) || clone.Entity == Unity.Entities.Entity.Null)
            {
                RRWLog.Info("surfaces: rrw.surf.line FAILED: test clone \"" + name + "\" not registered (DEVTOOLS clones are registered in the main menu)");
                return;
            }
            if (r50)
            {
                if (math.abs(round - 0.5f) < 0.003f) SurfaceState.DevRound.Remove(name);
                else SurfaceState.DevRound[name] = math.clamp(round, 0f, 1f);
            }
            if (q.Queue != int.MinValue) SurfaceState.DevQueue[name] = q.Queue;
            if (q.Prio != int.MinValue) SurfaceState.DevPrio[name] = q.Prio;

            var arc = new EdgeArc(m_Em.GetComponentData<Curve>(q.Edge).m_Bezier);
            float L = arc.Length;
            float s0 = math.saturate(math.min(q.T0, q.T1)) * L, s1 = math.saturate(math.max(q.T0, q.T1)) * L;
            float w = math.max(0.01f, q.Width);
            var lat = new BandLat { Left = q.Lat - w * 0.5f, Right = q.Lat + w * 0.5f, Margin = 0f, MinWidth = 0.01f };
            var dl = new DevLine
            {
                Tag = q.Tag ?? "", Clone = name, Edge = q.Edge,
                What = SurfaceState.DevLineKindName(q.Kind) + " lat=" + RRWLog.F(q.Lat) + " w=" + RRWLog.F(w) + " s=[" + RRWLog.F(s0) + "," + RRWLog.F(s1) + "]"
                       + (q.Dash > 0f ? " dash=" + RRWLog.F(q.Dash) + "," + RRWLog.F(q.Gap) : " solid"),
            };
            int n = 0;
            if (q.Dash > 0f)
            {
                float period = q.Dash + math.max(0f, q.Gap);
                for (float a = s0; a < s1 - 0.05f && n < 256; a += period)
                    if (SpawnDevPiece(dl, clone.Entity, arc, a, math.min(s1, a + q.Dash), lat)) n++;
            }
            else if (SpawnDevPiece(dl, clone.Entity, arc, s0, s1, lat)) n++;
            SurfaceState.DevLines.Add(dl);
            RRWLog.Info("surfaces: rrw.surf.line tag=" + dl.Tag + " edge=" + RRWLog.E(q.Edge) + " L=" + RRWLog.F(L) + " clone=\"" + name + "\" " + dl.What
                        + " areas=" + n + " round=" + RRWLog.F(SurfaceState.RoundnessOf(clone.Spec)) + " queue=+" + SurfaceState.QueueRaiseOf(clone.Spec)
                        + " prio=" + (SurfaceState.PriorityOverrideOf(clone.Spec) != int.MinValue ? SurfaceState.PriorityOverrideOf(clone.Spec) : clone.Spec.Priority)
                        + " (rendered width = w + 2 x " + RRWLog.F(SurfaceMaterialSystem.ParamOf(SurfaceState.RoundnessOf(clone.Spec)) * 0.5f) + " m roundness growth)");
        }

        private bool SpawnDevPiece(DevLine dl, Unity.Entities.Entity prefab, EdgeArc arc, float a, float b, in BandLat lat)
        {
            if (b - a < SurfaceGeom.kMinGeomLength) return false;
            SurfaceGeom.Strip(arc, a, b, lat, float3.zero, float3.zero, false, false, m_Poly);   // Y = road curve (visible road surface)
            if (m_Poly.Count < 3) return false;
            var t = new TrackedArea
            {
                Layer = SurfaceLayer.TempMarking, Band = 0, BandKind = SurfaceBand.TempLines, Prefab = prefab,
                Site = Unity.Entities.Entity.Null, ProjectId = 0u, Group = DerivedGroup.Area, LS0 = a, LS1 = b, GS0 = a, GS1 = b,
            };
            if (!SpawnArea(t, m_Poly)) return false;
            dl.Pieces.Add(t);
            return true;
        }
    }
}
#endif
