using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Tooling
{
    // What an apply does with one road piece whose type or upgrades changed.
    internal enum UpgradeAction : byte
    {
        Instant = 0,     // applied at once like the base game (setting, same-type wall change, unexpressible change of one type)
        Cosmetic = 1,    // decorations only: no works
        Upgrade = 2,     // partial upgrade works (mode H)
        Rebuild = 3,     // the whole road is rebuilt (SiteFlags.Replaced construction)
    }

    // The "Road upgrades" setting against the classifier's verdict. Pure; shared by the apply and the dev commands.
    internal static class UpgradeRules
    {
        // Below this lateral move (m) a re-upgraded road counts as not moved (its saved bands keep their frame).
        public const float kNoMove = 0.05f;

        public static UpgradeAction Decide(UpgradeWorksMode mode, bool prefabDiffers, UpgradeClass cls, UpgradeStructural why, bool instant)
        {
            if (mode == UpgradeWorksMode.Instant) return UpgradeAction.Instant;
            if (mode == UpgradeWorksMode.FullRebuild) return prefabDiffers ? UpgradeAction.Rebuild : UpgradeAction.Instant;
            if (instant) return UpgradeAction.Instant;
            if (cls == UpgradeClass.Cosmetic) return UpgradeAction.Cosmetic;
            if (cls == UpgradeClass.Structural)
                return prefabDiffers || why == UpgradeStructural.Track || why == UpgradeStructural.Wall ? UpgradeAction.Rebuild : UpgradeAction.Instant;
            return cls >= UpgradeClass.Remark && cls <= UpgradeClass.Mixed ? UpgradeAction.Upgrade : UpgradeAction.Cosmetic;
        }

        // Mode H works: every half-width construction counts, with or without a saved plan (fail-safe, like the gameplay rules).
        public static bool IsModeH(in RoadWorksSite s) => s.Kind == WorksKind.Construction && s.Mode == VisualMode.HalfWidth;

        public static bool PrefabDiffers(EntityManager em, Entity a, Entity b) =>
            !em.HasComponent<PrefabRef>(a) || !em.HasComponent<PrefabRef>(b)
            || em.GetComponentData<PrefabRef>(a).m_Prefab != em.GetComponentData<PrefabRef>(b).m_Prefab;

        public static bool UpgradedDiffers(EntityManager em, Entity a, Entity b)
        {
            var fa = em.HasComponent<Upgraded>(a) ? em.GetComponentData<Upgraded>(a).m_Flags : default;
            var fb = em.HasComponent<Upgraded>(b) ? em.GetComponentData<Upgraded>(b).m_Flags : default;
            return fa.m_General != fb.m_General || fa.m_Left != fb.m_Left || fa.m_Right != fb.m_Right;
        }

        // A temp that changes its original (a Modify / Upgrade temp, another road type or other upgrades), as opposed to a
        // neighbour the tool only regenerated.
        public static bool IsChange(EntityManager em, Entity temp, in Temp t) =>
            (t.m_Flags & (TempFlags.Modify | TempFlags.Upgrade)) != 0 || PrefabDiffers(em, temp, t.m_Original) || UpgradedDiffers(em, temp, t.m_Original);

        public static bool Moved(in UpgradeAlign al) => al.Reversed || !(math.abs(al.Shift) <= kNoMove);
    }

    // The upgrade part of WorksTagSystem: classifies the changed road pieces of one apply, collects the partial upgrade works
    // (one project per drag chain, through SiteFactory.CreateUpgradeProjects at Flush), hands full rebuilds back to the
    // caller's Replaced list and merges re-upgrades of running mode H works. Main thread, apply frames only.
    internal sealed class UpgradeTagger
    {
        // Outcome of a re-upgrade of a mode H site.
        public enum ReUpgrade : byte
        {
            PaidOnly = 0,    // nothing to build changed: the works continue, only the paid amount changes (caller writes it)
            Merged = 1,      // the merged bands go into a new project (Flush)
            Ended = 2,       // nothing left to build: the works end (no extra money)
            Rebuilt = 3,     // full rebuild (added to the caller's Replaced list)
        }

        private struct Pending
        {
            public SiteFactoryEdge Edge;
            public UpgradeSpec Spec;
            public Entity Prefab;        // new road type (one RoadClassInfo per factory call)
            public Entity ClassEdge;     // edge to read the new road class from (the temp)
            public bool Rushed;          // merged from rushed works: the new project starts rushed
        }

        private readonly UpgradeLayoutRead m_Neu = new UpgradeLayoutRead();
        private readonly UpgradeLayoutRead m_Old = new UpgradeLayoutRead();
        private readonly List<Pending> m_Pending = new List<Pending>();
        private readonly List<SiteFactoryEdge> m_GroupEdges = new List<SiteFactoryEdge>();
        private readonly List<UpgradeSpec> m_GroupSpecs = new List<UpgradeSpec>();
        private readonly List<SiteFactoryResult> m_Results = new List<SiteFactoryResult>();
        private readonly List<MergeBand> m_MergeOld = new List<MergeBand>(RRWConst.kUwMaxBands);
        private readonly List<UpgradeBand> m_Merged = new List<UpgradeBand>(RRWConst.kUwMaxBands + 1);
        private readonly Dictionary<uint, uint> m_EndIds = new Dictionary<uint, uint>();
        private readonly int[] m_Why = new int[8];

        // counters of the current apply (rrw.tools.last)
        public int Sites, Remark, Widen, Narrow, Mixed, Structural, Cosmetic, Instant, Rebuild, Merged, Reverted, PaidOnly, Failed;

        public UpgradeLayoutRead NewRead => m_Neu;
        public UpgradeLayoutRead OldRead => m_Old;

        public void Begin()
        {
            m_Pending.Clear();
            m_EndIds.Clear();
            Sites = Remark = Widen = Narrow = Mixed = Structural = Cosmetic = Instant = Rebuild = Merged = Reverted = PaidOnly = Failed = 0;
            Array.Clear(m_Why, 0, m_Why.Length);
        }

        public bool Any => Sites + Structural + Cosmetic + Instant + Rebuild + Merged + Reverted + PaidOnly + Failed + m_Pending.Count > 0;

        // Classifies temp (the new road) against old (the road it replaces) in the new curve's frame. A reader failure counts
        // as an unreadable layout (Structural), never as an exception that would stop the tagging of the whole apply.
        // align: where the new curve lies against the old one; alignRead is false when the reader failed before measuring it.
        public UpgradeClass Classify(EntityManager em, Entity temp, Entity old, out UpgradeSpec spec, out bool instant, out bool aligned,
                                     out UpgradeAlign align, out bool alignRead)
        {
            m_Neu.Clear();
            m_Neu.L.Readable = false;   // stays so when the reader returns before reading
            try
            {
                var cls = UpgradeLayoutReader.Classify(em, temp, old, m_Neu, m_Old, out spec, out instant, out aligned, out align);
                alignRead = true;
                return cls;
            }
            catch (Exception e)
            {
                RRWLog.ErrorOnce("tools upgrade classify", e);
                Failed++;
                spec = default;
                spec.Class = UpgradeClass.Structural;
                spec.Why = UpgradeStructural.Unreadable;
                instant = aligned = false;
                align = default;
                alignRead = false;
                m_Neu.Clear();
                m_Neu.L.Readable = false;
                return UpgradeClass.Structural;
            }
        }

        // ---------------------------------------------------------------- a road without works changes type or upgrades

        // keptOriginal: a Modify / Upgrade temp (the site goes on t.m_Original); else a Replace temp (the site goes on the temp,
        // which ApplyNetSystem creates). rebuildOut: the caller's Replaced construction list.
        public void OnPlain(EntityManager em, Entity temp, in Temp t, bool keptOriginal, RRWSetting s, List<SiteFactoryEdge> rebuildOut)
        {
            Entity orig = t.m_Original;
            bool prefabDiffers = UpgradeRules.PrefabDiffers(em, temp, orig);
            var mode = s.UpgradeMode;
            var spec = default(UpgradeSpec);
            bool instant = false;
            UpgradeClass cls = UpgradeClass.None;
            if (mode == UpgradeWorksMode.Realistic) cls = Classify(em, temp, orig, out spec, out instant, out _, out _, out _);
            var action = UpgradeRules.Decide(mode, prefabDiffers, cls, spec.Why, instant);
            bool buildings = ToolUtil.HasDependants(em, orig);
            switch (action)
            {
                case UpgradeAction.Instant:
                    Instant++;
                    if (cls == UpgradeClass.Structural) CountStructural(spec.Why);
                    RRWLog.Verbose("tools: upgrade " + RRWLog.E(temp) + " of " + RRWLog.E(orig) + " applied at once (" + Reason(mode, cls, spec, instant) + ")");
                    return;
                case UpgradeAction.Cosmetic:
                    Cosmetic++;
                    RRWLog.Verbose("tools: upgrade " + RRWLog.E(temp) + " of " + RRWLog.E(orig) + " is cosmetic: no works");
                    return;
                case UpgradeAction.Rebuild:
                    Rebuild++;
                    if (cls == UpgradeClass.Structural) CountStructural(spec.Why);
                    rebuildOut.Add(ToolUtil.FactoryEdge(em, temp, t.m_Cost, buildings, keptOriginal ? orig : Entity.Null));
                    RRWLog.Verbose("tools: upgrade " + RRWLog.E(temp) + " of " + RRWLog.E(orig) + " -> full rebuild (" + Reason(mode, cls, spec, instant) + ")");
                    return;
            }
            Add(em, temp, keptOriginal ? orig : Entity.Null, math.max(0, t.m_Cost), buildings, spec, false);
            RRWLog.Verbose("tools: upgrade " + RRWLog.E(temp) + " of " + RRWLog.E(orig) + " -> " + spec + " target=" + RRWLog.E(keptOriginal ? orig : temp));
        }

        // ---------------------------------------------------------------- re-upgrade of running mode H works

        // The original carries mode H works and is upgraded again: the old bands (moved into the new curve's frame, with their
        // progress) merge with the new change. Runs for a negative cost too (a revert upgrade refunds).
        public ReUpgrade OnUpgradeSite(EntityManager em, Entity temp, in Temp t, in RoadWorksSite site, bool keptOriginal, RRWSetting s,
                                       List<SiteFactoryEdge> rebuildOut, EntityQuery siteQuery)
        {
            Entity orig = t.m_Original;
            int paid = UpgradePlan.MergedPaid(site.m_PaidCost, t.m_Cost);
            if (!em.HasComponent<Curve>(orig) || !em.HasComponent<Curve>(temp)) { PaidOnly++; return ReUpgrade.PaidOnly; }
            bool prefabDiffers = UpgradeRules.PrefabDiffers(em, temp, orig);
            var mode = s.UpgradeMode;
            var n = default(UpgradeSpec);
            bool instant = false;
            UpgradeClass cls = UpgradeClass.None;
            var al = default(UpgradeAlign);
            bool alignRead = false;
            if (mode == UpgradeWorksMode.Realistic) cls = Classify(em, temp, orig, out n, out instant, out _, out al, out alignRead);
            else ReadNew(em, temp);
            var action = UpgradeRules.Decide(mode, prefabDiffers, cls, n.Why, instant);
            bool buildings = ToolUtil.HasDependants(em, orig);
            if (action == UpgradeAction.Rebuild)
            {
                Rebuild++;
                if (cls == UpgradeClass.Structural) CountStructural(n.Why);
                rebuildOut.Add(ToolUtil.FactoryEdge(em, temp, paid, buildings, keptOriginal ? orig : Entity.Null));
                RRWLog.Info("tools: re-upgrade of upgrade works p" + site.m_ProjectId + " on " + RRWLog.E(orig) + " -> full rebuild ("
                            + Reason(mode, cls, n, instant) + ") paid=" + paid);
                return ReUpgrade.Rebuilt;
            }
            if (action != UpgradeAction.Upgrade) { n = default; n.Class = UpgradeClass.Cosmetic; }

            // the old bands in the new curve's frame (the classifier already measured the alignment, unless it did not run)
            var oldArc = new EdgeArc(em.GetComponentData<Curve>(orig).m_Bezier);
            var newCurve = em.GetComponentData<Curve>(temp).m_Bezier;
            if (!alignRead) UpgradeDiff.Align(oldArc, newCurve, out al);
            if (n.BandCount == 0 && !UpgradeRules.Moved(al))
            {
                PaidOnly++;
                RRWLog.Verbose("tools: re-upgrade of upgrade works p" + site.m_ProjectId + " on " + RRWLog.E(orig) + ": nothing new to build, paid=" + paid);
                return ReUpgrade.PaidOnly;
            }
            if (!m_Neu.L.Readable && n.BandCount == 0)
            {
                // the new outline is unknown: clip nothing (the old bands survive as they are, moved into the new frame)
                m_Neu.L.OuterL = float.MinValue;
                m_Neu.L.OuterR = float.MaxValue;
            }
            var sched = site.Schedule;
            float p = site.Progress;
            m_MergeOld.Clear();
            int bc = math.min(site.m_BandCount, RRWConst.kUwMaxBands);
            for (int i = 0; i < bc; i++)
            {
                var b = site.Band(i);
                var st = UpgradePlan.StateOf(sched, b.Window, b.G0f, p, out float g);
                m_MergeOld.Add(new MergeBand { Band = UpgradePlan.Reframe(b, al.Shift, al.Reversed), State = st, G = g });
            }
            var outcome = UpgradePlan.Merge(m_MergeOld, n, m_Neu.L.OuterL, m_Neu.L.OuterR, m_Merged);
            string head = "tools: re-upgrade of upgrade works p" + site.m_ProjectId + " on " + RRWLog.E(orig) + " p=" + RRWLog.F(p)
                          + " shift=" + RRWLog.F(al.Shift) + (al.Reversed ? " reversed" : "") + " new=" + n;
            switch (outcome)
            {
                case MergeOutcome.End:
                    Reverted++;
                    RRWLog.Info(head + " -> nothing left to build: the works end");
                    EndWorks(em, keptOriginal ? orig : temp, site, oldArc, newCurve, paid, siteQuery);
                    return ReUpgrade.Ended;
                case MergeOutcome.Structural:
                    Rebuild++;
                    CountStructural(UpgradeStructural.Bands);
                    rebuildOut.Add(ToolUtil.FactoryEdge(em, temp, paid, buildings, keptOriginal ? orig : Entity.Null));
                    RRWLog.Info(head + " -> too many bands after the merge: full rebuild, paid=" + paid);
                    return ReUpgrade.Rebuilt;
            }
            var merged = new UpgradeSpec { Class = UpgradePlan.ClassOf(m_Merged, m_Merged.Count), BandCount = math.min(m_Merged.Count, RRWConst.kUwMaxBands) };
            for (int i = 0; i < merged.BandCount; i++) merged.SetBand(i, m_Merged[i]);
            Merged++;
            Add(em, temp, keptOriginal ? orig : Entity.Null, paid, buildings, merged, site.Has(SiteFlags.Rushed));
            RRWLog.Info(head + " -> merged " + merged + " paid=" + paid);
            return ReUpgrade.Merged;
        }

        // Ends the mode H works on target (the kept original, or the Replace temp that ApplyNetSystem creates in its place).
        // The site stays in its project with its progress; only its chain range and bands move into the new curve's frame
        // and the paid amount takes the change's cost. The Director then ends the works on this edge (EndUpgrade request:
        // p = 1, split off first when other edges of the project go on), so the lane groups the works had closed stay closed
        // until the machines have left, and the release opens them.
        private void EndWorks(EntityManager em, Entity target, in RoadWorksSite site, EdgeArc oldArc, Bezier4x3 newCurve, int paid, EntityQuery siteQuery)
        {
            var ended = site;
            ended.m_PaidCost = paid;
            ToolUtil.MapOnto(oldArc, site, newCurve, out ended.m_ChainU0, out ended.m_ChainU1);
            if (site.IsUpgrade)
            {
                int bc = math.min(math.min(site.m_BandCount, RRWConst.kUwMaxBands), m_MergeOld.Count);
                for (int i = 0; i < bc; i++) ended.SetBand(i, m_MergeOld[i].Band);
                ToolUtil.PutSite(em, target, ended);
                WorksRequests.Enqueue(new WorksRequest { Type = WorksRequestType.EndUpgrade, Edge = target, Aux = site.m_ProjectId });
                RRWLog.Verbose("tools: upgrade works on " + RRWLog.E(target) + " end in p" + site.m_ProjectId + " (the release opens the road)");
                return;
            }
            // No saved band plan: the Director does not run these as upgrade works, so they cannot be ended in their project.
            // The edge leaves into a finished project of its own (one per old project and apply).
            if (!m_EndIds.TryGetValue(site.m_ProjectId, out uint id))
            {
                ToolUtil.EnsureProjectIds(em, siteQuery);
                id = SiteRegistry.AllocateProjectId();
                m_EndIds[site.m_ProjectId] = id;
            }
            ended.m_ProjectId = id;
            ended.m_WorkDone = ended.m_WorkRequired;
            ToolUtil.PutSite(em, target, ended);
            RRWLog.Verbose("tools: upgrade works without a band plan on " + RRWLog.E(target) + " left p" + site.m_ProjectId + " as finished p" + id);
        }

        // ---------------------------------------------------------------- projects

        private void Add(EntityManager em, Entity temp, Entity target, int paid, bool buildings, in UpgradeSpec spec, bool rushed)
        {
            var fe = ToolUtil.FactoryEdge(em, temp, paid, buildings, target);
            fe.Buildings = buildings;
            fe.KeepsLanes = m_Neu.L.Readable && UpgradeLanes.EveryDirectionKeepsLane(m_Neu.L, spec);
            m_Pending.Add(new Pending
            {
                Edge = fe,
                Spec = spec,
                Prefab = em.HasComponent<PrefabRef>(temp) ? em.GetComponentData<PrefabRef>(temp).m_Prefab : Entity.Null,
                ClassEdge = temp,
                Rushed = rushed,
            });
        }

        // Creates the collected upgrade works: one factory call per new road type (its class sets the band shares) and rush
        // state, one project per drag chain inside it. Returns the sites written.
        public int Flush(EntityManager em, RRWSetting s, EntityQuery siteQuery)
        {
            if (m_Pending.Count == 0) return 0;
            ToolUtil.EnsureProjectIds(em, siteQuery);
            int n = 0;
            var done = new bool[m_Pending.Count];
            for (int i = 0; i < m_Pending.Count; i++)
            {
                if (done[i]) continue;
                var head = m_Pending[i];
                m_GroupEdges.Clear();
                m_GroupSpecs.Clear();
                for (int j = i; j < m_Pending.Count; j++)
                {
                    if (done[j] || m_Pending[j].Prefab != head.Prefab || m_Pending[j].Rushed != head.Rushed) continue;
                    done[j] = true;
                    m_GroupEdges.Add(m_Pending[j].Edge);
                    m_GroupSpecs.Add(m_Pending[j].Spec);
                }
                m_Results.Clear();
                SiteFactory.CreateUpgradeProjects(m_GroupEdges, m_GroupSpecs, EcsUtil.RoadClass(em, head.ClassEdge), s,
                                                  head.Rushed ? SiteFlags.Rushed : SiteFlags.None, m_Results);
                for (int k = 0; k < m_Results.Count; k++)
                {
                    var r = m_Results[k];
                    if (!EcsUtil.Alive(em, r.Edge)) continue;
                    ToolUtil.PutSite(em, r.Edge, r.Site);
                    n++;
                    CountClass(r.Site.UpClass);
                }
            }
            Sites += n;
            m_Pending.Clear();
            return n;
        }

        // ---------------------------------------------------------------- summary

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(" upgrade=").Append(Sites);
            if (Sites > 0)
                sb.Append(" (remark=").Append(Remark).Append(" widen=").Append(Widen).Append(" narrow=").Append(Narrow).Append(" mixed=").Append(Mixed).Append(')');
            sb.Append(" structural=").Append(Structural);
            if (Structural > 0) sb.Append(" (").Append(WhyList()).Append(')');
            sb.Append(" rebuild=").Append(Rebuild).Append(" cosmetic=").Append(Cosmetic).Append(" instantUpgrade=").Append(Instant)
              .Append(" merged=").Append(Merged).Append(" revert=").Append(Reverted).Append(" upgradePaidOnly=").Append(PaidOnly);
            if (Failed > 0) sb.Append(" classifyFailed=").Append(Failed);
            return sb.ToString();
        }

        private string WhyList()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < m_Why.Length; i++)
            {
                if (m_Why[i] == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append((UpgradeStructural)i).Append('=').Append(m_Why[i]);
            }
            return sb.ToString();
        }

        private void CountStructural(UpgradeStructural why)
        {
            Structural++;
            int i = (int)why;
            if (i >= 0 && i < m_Why.Length) m_Why[i]++;
        }

        private void CountClass(UpgradeClass c)
        {
            switch (c)
            {
                case UpgradeClass.Remark: Remark++; break;
                case UpgradeClass.Widen: Widen++; break;
                case UpgradeClass.Narrow: Narrow++; break;
                case UpgradeClass.Mixed: Mixed++; break;
            }
        }

        // The new layout only (the outline clips the old bands) when the setting skips the classification.
        private void ReadNew(EntityManager em, Entity temp)
        {
            try
            {
                var curve = em.GetComponentData<Curve>(temp).m_Bezier;
                UpgradeLayoutReader.Read(em, temp, new EdgeArc(curve), m_Neu);
            }
            catch (Exception e)
            {
                RRWLog.ErrorOnce("tools upgrade read", e);
                m_Neu.Clear();
                m_Neu.L.Readable = false;
            }
        }

        public static string Reason(UpgradeWorksMode mode, UpgradeClass cls, in UpgradeSpec spec, bool instant)
        {
            if (mode != UpgradeWorksMode.Realistic) return "setting " + mode;
            if (instant) return "same road type, wall change";
            if (cls == UpgradeClass.Structural) return "structural " + spec.Why;
            return cls.ToString();
        }
    }
}
