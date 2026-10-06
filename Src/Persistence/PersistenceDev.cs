#if DEVTOOLS
using System.Collections.Generic;
using RealisticRoadWorks.Dev;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Persistence
{
    // rrw.save.scan - dry run of the save sanitizer: what would be kept out of a save right now, plus the
    // save-safety lane counters: the snapshot taken right after the last load BEFORE Traffic re-applied,
    // the last pre-serialize audit (after the Traffic save guard), and with "now" a fresh scan.
    public sealed class SaveScanCommand : IDevCommand
    {
        public string Name => "rrw.save.scan";
        public string Help => "rrw.save.scan [now] - dry run of SaveSanitize (icons, RRWDerived / puppet descendants without LivePath, v2 leftovers) + "
                              + "lane counters: load snapshot (Forbidden / blockage / RRW restriction refs before Traffic re-applies), last save audit, modes; now = scan lanes now; "
                              + "dust puffs (live, oldest age, LivePath, Owner) and RRW_Roller_* GameObjects now / at the last load";
        public void Run(DevContext ctx, string[] args)
        {
            var scan = SaveSanitizeSystem.Scan;
            if (scan == null) { ctx.Log("rrw save scan: sanitizer not created"); return; }
            var c = scan.Run(null, Entity.Null, Entity.Null, false);
            ctx.Log("rrw save scan derivedNoLivePath=" + c.Derived + " descendantsNoLivePath=" + c.Descendants
                    + " v2Left=" + LegacyMigrationSystem.LegacyLeft + " legacyPausedBit=" + LegacyMigrationSystem.LegacyPausedLeft
                    + " guardPending=" + SaveGuard.Pending.Count
                    + (c.Derived + c.Descendants == 0 ? " ok" : " FAIL"));

            // Dust puffs and roller GameObjects (nothing new is saved)
            if (LegacyMigrationSystem.R5AuditQuery != default) R5Audit.Sample(ctx.EntityManager, LegacyMigrationSystem.R5AuditQuery);
            ctx.Log("rrw save scan r5 " + R5Audit.Text());

            if (LaneScan.LoadTaken)
            {
                ctx.Log("rrw save scan load@" + LaneScan.LoadUpdate + " works " + LaneScan.LoadWorks.Text());
                ctx.Log("rrw save scan load@" + LaneScan.LoadUpdate + " city  " + LaneScan.LoadCity.Text()
                        + (LaneScan.LoadCity.RrwRefs + LaneScan.LoadWorks.RrwRefs == 0 ? " rrwRefs=0 ok" : " RRW REFS SURVIVED THE SAVE (FAIL)"));
                ctx.Log("rrw save scan load modes sites=" + LaneScan.LoadSites + " A=" + LaneScan.LoadModeA + " H=" + LaneScan.LoadModeH
                        + "(reserved mode, run as D) D=" + LaneScan.LoadModeD + " unknown=" + LaneScan.LoadModeUnknown + " incompatible=" + LaneScan.LoadIncompatible);
            }
            else ctx.Log("rrw save scan load: no snapshot since the last load (taken in the first game update after a load)");

            if (LaneScan.AuditTaken)
                ctx.Log("rrw save scan last save audit (" + LaneScan.AuditWhen + ") works " + LaneScan.AuditWorks.Text()
                        + " | registry closedB=" + LaneScan.AuditClosedBEdges + " soft=" + LaneScan.AuditSoftEdges + " halfOpen=" + LaneScan.AuditOpenEdges
                        + (LaneScan.AuditWorks.RrwRefs == 0 && (LaneScan.AuditWorks.Blocked == 0 || LaneScan.AuditClosedBEdges == 0) ? " ok" : " FAIL"));
            else ctx.Log("rrw save scan last save audit: no save since the last load");

            bool now = args.Length > 0 && args[0].Equals("now", System.StringComparison.OrdinalIgnoreCase);
            if (!now) return;
            var em = ctx.EntityManager;
            var we = new List<Entity>();
            var ce = new List<Entity>();
            var w = LaneScan.Works(em, we);
            var cc = LaneScan.City(em, ce);
            ctx.Log("rrw save scan now works " + w.Text() + " (live state: Traffic's closures included) edges " + LaneScan.Edges(we));
            ctx.Log("rrw save scan now city  " + cc.Text() + " edges " + LaneScan.Edges(ce));
        }
    }
}
#endif
