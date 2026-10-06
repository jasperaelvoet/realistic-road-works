using System.Collections.Generic;
using System.Text;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;
using NetSubLane = Game.Net.SubLane;

namespace RealisticRoadWorks.V3.UI
{
    // Tooltip estimate of the road changes under the road / upgrade tool (main thread, reads Temp entities only):
    //  * every upgraded piece is classified like the apply will do it (UpgradeLayoutReader.Classify on the Temp edge and its original,
    //    following the "Road upgrades" setting);
    //  * the pieces with partial works go through the project factory as a dry run (no project id, no log): hours and windows;
    //  * the traffic primitive of every window is estimated from what is known before the apply (buildings, lane layout, gates;
    //    a detour is assumed): the weakest one over the windows that touch car lanes is shown, with "≈";
    //  * pieces that will be rebuilt in full (structural changes, or the setting) get their own line with the reason.
    // Results are cached on a key of the pieces (entity, original, prefab, curve ends) plus the settings and gates.
    public sealed class UpgradeTip
    {
        public string Text;          // "Widening ≈ 7.8 h · outer lanes closed" (null: no partial works)
        public string RebuildText;   // "Full rebuild ≈ 19 h · bridge or tunnel" (null: nothing rebuilt in full)
        public string Summary = "none";   // one-line account of the last evaluation (dev command)

        private readonly UpgradeLayoutRead m_Neu = new UpgradeLayoutRead();
        private readonly UpgradeLayoutRead m_Old = new UpgradeLayoutRead();
        private readonly List<SiteFactoryEdge> m_Edges = new List<SiteFactoryEdge>(16);
        private readonly List<UpgradeSpec> m_Specs = new List<UpgradeSpec>(16);
        private readonly List<int> m_CarMask = new List<int>(16);              // per used piece: bit j = band j holds a lane
        private readonly List<SiteFactoryResult> m_Results = new List<SiteFactoryResult>(16);
        private readonly List<SubStrip> m_Strips = new List<SubStrip>(16);
        private readonly BandTraffic[] m_Prims = new BandTraffic[RRWConst.kUwMaxWindows];
        private uint m_Key;
        private bool m_HaveKey;

        public void Reset()
        {
            Text = RebuildText = null;
            m_HaveKey = false;
        }

        // A Temp edge that changes an existing road in place (replace / upgrade tool): the original is a live road and the road
        // type or its upgrade flags differ. Same type and flags (geometry updates next to a new connection) are not upgrades.
        public static bool IsUpgradePiece(EntityManager em, Entity temp, in Temp t)
        {
            if ((t.m_Flags & (TempFlags.Modify | TempFlags.Upgrade | TempFlags.Replace)) == 0) return false;
            if ((t.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Combine)) != 0) return false;
            Entity orig = t.m_Original;
            if (orig == Entity.Null || !em.Exists(orig) || em.HasComponent<Temp>(orig)) return false;
            if (!em.HasComponent<Road>(orig) || !em.HasComponent<NetEdge>(orig) || !em.HasComponent<Curve>(orig) || em.HasComponent<Owner>(orig)) return false;
            if (!em.HasComponent<Road>(temp)) return false;
            return PrefabDiffers(em, temp, orig) || FlagsDiffer(em, temp, orig);
        }

        private static bool PrefabDiffers(EntityManager em, Entity a, Entity b) =>
            !em.HasComponent<PrefabRef>(a) || !em.HasComponent<PrefabRef>(b)
            || em.GetComponentData<PrefabRef>(a).m_Prefab != em.GetComponentData<PrefabRef>(b).m_Prefab;

        private static bool FlagsDiffer(EntityManager em, Entity a, Entity b)
        {
            var fa = em.HasComponent<Upgraded>(a) ? em.GetComponentData<Upgraded>(a).m_Flags : default;
            var fb = em.HasComponent<Upgraded>(b) ? em.GetComponentData<Upgraded>(b).m_Flags : default;
            return fa.m_General != fb.m_General || fa.m_Left != fb.m_Left || fa.m_Right != fb.m_Right;
        }

        // Evaluates the pieces (at most kUwTooltipMaxEdges). Pieces whose original is already under works are skipped (the works
        // continue there; no new estimate).
        public void Evaluate(EntityManager em, List<Entity> pieces, RRWSetting s)
        {
            if (s == null || pieces.Count == 0 || s.UpgradeMode == UpgradeWorksMode.Instant)
            {
                Text = RebuildText = null;
                m_HaveKey = false;
                Summary = pieces.Count == 0 ? "none" : "instant (setting)";
                return;
            }
            uint key = KeyOf(em, pieces, s);
            if (m_HaveKey && key == m_Key) return;
            m_Key = key;
            m_HaveKey = true;
            Text = RebuildText = null;

            m_Edges.Clear();
            m_Specs.Clear();
            m_CarMask.Clear();
            float rebuildLen = 0f;
            Entity rebuildClass = Entity.Null;
            UpgradeStructural why = UpgradeStructural.None;
            int cosmetic = 0, works = 0, skipped = 0;
            var sb = new StringBuilder();
            int n = math.min(pieces.Count, RRWConst.kUwTooltipMaxEdges);
            for (int i = 0; i < n; i++)
            {
                Entity e = pieces[i];
                var t = em.GetComponentData<Temp>(e);
                Entity orig = t.m_Original;
                if (em.HasComponent<RoadWorksSite>(orig)) { skipped++; continue; }
                float len = em.GetComponentData<Curve>(e).m_Length;
                bool prefabDiffers = PrefabDiffers(em, e, orig);
                if (s.UpgradeMode == UpgradeWorksMode.FullRebuild)
                {
                    if (!prefabDiffers) { cosmetic++; continue; }
                    rebuildLen += len;
                    if (rebuildClass == Entity.Null) rebuildClass = e;
                    why = UpgradeStructural.Policy;
                    continue;
                }
                var cls = UpgradeLayoutReader.Classify(em, e, orig, m_Neu, m_Old, out UpgradeSpec spec, out bool instant, out _);
                if (instant || cls == UpgradeClass.Cosmetic) { cosmetic++; continue; }
                if (cls == UpgradeClass.Structural || !SiteFactory.UpgradeSpecUsable(spec))
                {
                    // the apply rebuilds a structural change when the road type changes, or when tracks or walls change
                    if (prefabDiffers || spec.Why == UpgradeStructural.Track || spec.Why == UpgradeStructural.Wall)
                    {
                        rebuildLen += len;
                        if (rebuildClass == Entity.Null) rebuildClass = e;
                        if (why == UpgradeStructural.None) why = spec.Why == UpgradeStructural.None ? UpgradeStructural.Unreadable : spec.Why;
                    }
                    else cosmetic++;
                    continue;
                }
                bool buildings = HasBuildings(em, orig);
                var fe = new SiteFactoryEdge
                {
                    Edge = e,
                    Length = len,
                    PaidCost = math.max(0, t.m_Cost),
                    Dependants = buildings,
                    Buildings = buildings,
                    KeepsLanes = UpgradeLanes.EveryDirectionKeepsLane(m_Neu.L, spec),
                };
                if (em.HasComponent<NetEdge>(e))
                {
                    var ed = em.GetComponentData<NetEdge>(e);
                    fe.StartNode = ed.m_Start;
                    fe.EndNode = ed.m_End;
                }
                int mask = 0;
                for (int j = 0; j < spec.BandCount; j++)
                {
                    var b = spec.Band(j);
                    if (b.Kind != BandKind.Build && b.Kind != BandKind.Rebuild) continue;
                    m_Strips.Clear();
                    UpgradeDiff.SubStrips(m_Neu.L, b, m_Strips);
                    if (UpgradeLanes.HasCar(m_Strips)) mask |= 1 << j;
                }
                // a direction group cannot close on a one-way road or a road with tram tracks (no detour search before the apply)
                var block = m_Neu.L.TrackLanes > 0 ? StageBlockReason.Track
                          : m_Neu.L.CarLanesF == 0 || m_Neu.L.CarLanesB == 0 ? StageBlockReason.OneWay : StageBlockReason.None;
                m_Edges.Add(fe);
                m_Specs.Add(spec);
                m_CarMask.Add(mask | ((int)block << 8));
                works++;
            }

            sb.Append("pieces=").Append(pieces.Count).Append(n < pieces.Count ? " (first " + n + ")" : "")
              .Append(" works=").Append(works).Append(" cosmetic=").Append(cosmetic).Append(" underWorks=").Append(skipped)
              .Append(" mode=").Append(s.UpgradeMode);

            if (works > 0) EstimateWorks(em, s, sb);
            if (rebuildLen > 0.5f && rebuildClass != Entity.Null)
            {
                float hours = WorkTime.WorkHours(WorksKind.Construction, rebuildLen, EcsUtil.RoadClass(em, rebuildClass), s);
                RebuildText = RRWText.Format(RRWText.UpgradeTipHoursFullRebuild, UiFormat.Hours(hours), RRWText.Get(RRWText.StructuralKey(why)));
                sb.Append(" rebuild=").Append(RRWLog.F(rebuildLen)).Append("m(").Append(why).Append(")");
            }
            if (Text != null) sb.Append(" text='").Append(Text).Append("'");
            if (RebuildText != null) sb.Append(" rebuildText='").Append(RebuildText).Append("'");
            Summary = sb.ToString();
        }

        // Dry run of the factory over the pieces with partial works, then the estimate of every window's primitive.
        private void EstimateWorks(EntityManager em, RRWSetting s, StringBuilder sb)
        {
            var rc = EcsUtil.RoadClass(em, m_Edges[0].Edge);   // the new road (the Temp edge carries its composition)
            m_Results.Clear();
            SiteFactory.CreateUpgradeProjects(m_Edges, m_Specs, rc, UpgradeRates.From(s), SiteFlags.None, m_Results, null, true);
            if (m_Results.Count == 0) { sb.Append(" factory=none"); return; }

            uint required = 0;
            int windows = 1;
            bool buildings = false, keepsLanes = true;
            var block = StageBlockReason.None;
            UpgradeClass cls = UpgradeClass.None;
            bool sameClass = true;
            // per window: re-marking, build band, a build band holding a lane, build sides (chain frame)
            int remark = 0, build = 0, car = 0, left = 0, right = 0, middle = 0;
            for (int r = 0; r < m_Results.Count; r++)
            {
                var site = m_Results[r].Site;
                int k = IndexOf(m_Results[r].Edge);
                if (k < 0) continue;
                required = math.max(required, site.m_WorkRequired);
                windows = math.max(windows, site.m_WindowCount);
                var fe = m_Edges[k];
                buildings |= fe.Buildings;
                keepsLanes &= fe.KeepsLanes;
                var edgeBlock = (StageBlockReason)(m_CarMask[k] >> 8);
                if (block == StageBlockReason.None) block = edgeBlock;
                var ec = m_Specs[k].Class;
                if (cls == UpgradeClass.None) cls = ec; else if (ec != cls) sameClass = false;
                bool reversed = site.m_ChainU1 < site.m_ChainU0;
                for (int j = 0; j < site.m_BandCount; j++)
                {
                    var b = UpgradePlan.ToChain(site.Band(j), reversed);
                    int bit = 1 << math.clamp(b.Window, 0, RRWConst.kUwMaxWindows - 1);
                    if (b.Kind == BandKind.Remark) { remark |= bit; continue; }
                    if (b.Kind != BandKind.Build && b.Kind != BandKind.Rebuild) continue;
                    build |= bit;
                    if ((m_CarMask[k] & (1 << j)) != 0) car |= bit;
                    if (b.Side == BandSide.Left) left |= bit;
                    else if (b.Side == BandSide.Right) right |= bit;
                    else middle |= bit;
                }
            }
            if (!sameClass) cls = UpgradeClass.Mixed;

            // weakest primitive over the windows that touch car lanes (none: the works leave the cars alone)
            BandTraffic shown = BandTraffic.None;
            bool any = false;
            for (int w = 0; w < windows && w < m_Prims.Length; w++)
            {
                int bit = 1 << w;
                var input = new WindowPrimitiveInput
                {
                    RemarkWindow = (remark & bit) != 0,
                    HasBuild = (build & bit) != 0,
                    CarSubStrips = (car & bit) != 0,
                    BothSides = ((left & bit) != 0 && (right & bit) != 0) || (middle & bit) != 0,
                    EveryDirectionKeepsLane = keepsLanes,
                    Buildings = buildings,
                    SideBuildings = buildings,
                    CarHalfBlock = block,
                    DetourExists = true,
                    CorridorConflict = false,
                    Policy = s.Policy,
                    DropGate = RRWGates.UpgradeDrop,
                    ClosedBGate = RRWGates.ClosedB,
                    Saved = BandTraffic.Undecided,
                    CutEdge = false,
                };
                var prim = PhasePlan.WindowPrimitive(input, out _);
                m_Prims[w] = prim;
                bool cars = input.RemarkWindow || input.CarSubStrips;
                if (!cars) continue;
                if (!any || PhasePlan.ClosureStrength(prim) < PhasePlan.ClosureStrength(shown)) shown = prim;
                any = true;
            }

            // under the Visual only setting the works never touch traffic, whatever the windows would close
            if (s.Policy == ClosurePolicy.VisualOnly) shown = BandTraffic.None;

            float hours = WorkTime.HoursFromFrames(required);
            Text = TipText(cls, hours, shown);
            sb.Append(" class=").Append(cls).Append(" hours=").Append(RRWLog.F(hours)).Append(" windows=").Append(windows).Append(" prims=");
            for (int w = 0; w < windows && w < m_Prims.Length; w++) sb.Append(w > 0 ? "," : "").Append(m_Prims[w]);
            sb.Append(" shown=").Append(any ? shown.ToString() : "-").Append(buildings ? " buildings" : "").Append(keepsLanes ? " keepsLanes" : "")
              .Append(block != StageBlockReason.None ? " halfBlock=" + block : "");
        }

        // The class line with the hours and the traffic word of the weakest expected primitive ("Widening ≈ 7.8 h · outer lanes
        // closed"; "traffic not affected" when no window touches the cars). Null for a class without partial works.
        public static string TipText(UpgradeClass cls, float hours, BandTraffic shown)
        {
            switch (cls)
            {
                case UpgradeClass.Widen:
                case UpgradeClass.Narrow:
                case UpgradeClass.Remark:
                case UpgradeClass.Mixed:
                    return RRWText.Format(RRWText.UpgradeTipHoursKey(cls), UiFormat.Hours(hours), RRWText.Get(RRWText.UpgradeTipTrafficKey(shown)));
                default:
                    return null;
            }
        }

        private int IndexOf(Entity edge)
        {
            for (int i = 0; i < m_Edges.Count; i++) if (m_Edges[i].Edge == edge) return i;
            return -1;
        }

        private static bool HasBuildings(EntityManager em, Entity edge) =>
            em.HasBuffer<ConnectedBuilding>(edge) && em.GetBuffer<ConnectedBuilding>(edge, true).Length > 0;

        // Change key of the pieces and of everything the estimate reads besides them (setting, policy, gates).
        private static uint KeyOf(EntityManager em, List<Entity> pieces, RRWSetting s)
        {
            uint h = (uint)pieces.Count;
            h = h * 31u + (uint)s.UpgradeMode;
            h = h * 31u + (uint)s.Policy;
            h = h * 31u + (uint)math.round(s.ConstructionHoursPerKm * 10f) + ((uint)math.round(s.ConstructionMinHours * 10f) << 16);
            h = h * 31u + (RRWGates.UpgradeDrop ? 1u : 0u) + (RRWGates.ClosedB ? 2u : 0u) + (RRWGates.UpgradePreCover ? 4u : 0u);
            for (int i = 0; i < pieces.Count; i++)
            {
                Entity e = pieces[i];
                var t = em.GetComponentData<Temp>(e);
                h = h * 31u + (uint)e.Index + ((uint)e.Version << 20);
                h = h * 31u + (uint)t.m_Original.Index;
                h = h * 31u + (uint)t.m_Cost;
                if (em.HasComponent<PrefabRef>(e)) h = h * 31u + (uint)em.GetComponentData<PrefabRef>(e).m_Prefab.Index;
                if (em.HasComponent<Upgraded>(e))
                {
                    var f = em.GetComponentData<Upgraded>(e).m_Flags;
                    h = h * 31u + (uint)f.m_General + (uint)f.m_Left * 7u + (uint)f.m_Right * 13u;
                }
                var c = em.GetComponentData<Curve>(e).m_Bezier;
                h = h * 31u + math.hash(math.round(new float4(c.a.x, c.a.z, c.d.x, c.d.z) * 10f));
                // the temporary lanes may arrive a frame after the edge: re-classify when they do
                if (em.HasBuffer<NetSubLane>(e)) h = h * 31u + (uint)em.GetBuffer<NetSubLane>(e, true).Length;
                if (em.HasBuffer<ConnectedBuilding>(t.m_Original)) h = h * 31u + (uint)em.GetBuffer<ConnectedBuilding>(t.m_Original, true).Length;
            }
            return h;
        }
    }
}
