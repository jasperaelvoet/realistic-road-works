#if DEVTOOLS
using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using Game.SceneFlow;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Persistence
{
    // Save audit for dust puffs and road rollers, DEVTOOLS only. Neither is ever saved; this only VERIFIES it:
    //  * dust puffs (RRWDerived group DerivedGroup.DustPuff, Machines) carry LivePath (the generic SaveScan rule keeps any without
    //    it out of a save), have no Owner (by design) and never live longer than kPuffMaxAge machine seconds (Machines deletes
    //    them after kDustPuffLifeSeconds). Ages come from a first-seen stamp sampled every kTickEvery updates (LegacyMigration, Mod1);
    //  * road rollers are plain GameObjects ("RRW_Roller_<id>" roots, HideFlags.DontSave; Machines): none may survive a load or
    //    exist outside game mode. Counted with Resources.FindObjectsOfTypeAll (dev only, a few ms: at load complete and on demand).
    //  * the snapshot taken at OnGameLoadingComplete: puffs and roller objects that came through the load (both must be 0).
    internal static class R5Audit
    {
        public const float kPuffMaxAge = 10f;   // machine s (none may be older than 10 s; Machines deletes at 3.6 s)
        const int kTickEvery = 15;
        public const string kRollerPrefix = "RRW_Roller_";

        static int s_Tick;
        static readonly Dictionary<Entity, uint> s_FirstSeen = new Dictionary<Entity, uint>();
        static readonly HashSet<Entity> s_Now = new HashSet<Entity>();
        static readonly List<Entity> s_Gone = new List<Entity>();

        // last sample
        public static int Puffs, NoLivePath, Owned, Visible, NoSite, OverAge;
        public static float OldestAge, MaxAgeSeen;
        public static long SeenTotal;
        // load snapshot (-1 = none since the process started)
        public static int LoadPuffs = -1, LoadRollerObjects = -1, LoadRollerNoDontSave;
        public static string LoadWhen = "never";

        static float MachineSeconds(uint frames) => frames / 60f * math.max(0.01f, RRWDebug.MachineClockScale);

        public static void ResetForLoad()
        {
            s_FirstSeen.Clear();
            Puffs = NoLivePath = Owned = Visible = NoSite = OverAge = 0;
            OldestAge = 0f;
            s_Tick = 0;
        }

        // Mod1, every update in game mode (cheap counter); the scan runs every kTickEvery updates.
        public static void Tick(EntityManager em, EntityQuery derived)
        {
            if (++s_Tick < kTickEvery) return;
            s_Tick = 0;
            Sample(em, derived);
        }

        public static void Sample(EntityManager em, EntityQuery derived)
        {
            uint frame = RRWClock.RenderFrame;
            s_Now.Clear();
            int puffs = 0, noLive = 0, owned = 0, visible = 0, noSite = 0, over = 0;
            float oldest = 0f;
            if (!derived.IsEmptyIgnoreFilter)
            {
                var ents = derived.ToEntityArray(Allocator.Temp);
                var data = derived.ToComponentDataArray<RRWDerived>(Allocator.Temp);
                try
                {
                    for (int i = 0; i < ents.Length; i++)
                    {
                        if (data[i].m_Group != (byte)DerivedGroup.DustPuff) continue;
                        Entity e = ents[i];
                        puffs++;
                        s_Now.Add(e);
                        if (!s_FirstSeen.TryGetValue(e, out uint born)) { s_FirstSeen[e] = born = frame; SeenTotal++; }
                        float age = MachineSeconds(unchecked(frame - born));
                        if (age > oldest) oldest = age;
                        if (age > kPuffMaxAge) over++;
                        if (!em.HasComponent<LivePath>(e)) noLive++;
                        if (em.HasComponent<Owner>(e)) owned++;
                        if (!em.HasComponent<Hidden>(e)) visible++;
                        if (data[i].m_Site == Entity.Null || !SiteRegistry.Edges.ContainsKey(data[i].m_Site)) noSite++;
                    }
                }
                finally { ents.Dispose(); data.Dispose(); }
            }
            if (s_FirstSeen.Count > s_Now.Count)
            {
                s_Gone.Clear();
                foreach (var kv in s_FirstSeen) if (!s_Now.Contains(kv.Key)) s_Gone.Add(kv.Key);
                foreach (var e in s_Gone) s_FirstSeen.Remove(e);
            }
            Puffs = puffs; NoLivePath = noLive; Owned = owned; Visible = visible; NoSite = noSite; OverAge = over;
            OldestAge = oldest;
            if (oldest > MaxAgeSeen) MaxAgeSeen = oldest;
        }

        // Root GameObjects named like Machines' rollers (Destroy takes effect at the end of the frame). noDontSave: roots without
        // HideFlags.DontSave (rollers must always carry it).
        public static int RollerRoots(out int noDontSave)
        {
            noDontSave = 0;
            int n = 0;
            foreach (var go in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.GameObject>())
            {
                if (go == null || go.transform.parent != null || !go.name.StartsWith(kRollerPrefix, StringComparison.Ordinal)) continue;
                n++;
                if ((go.hideFlags & UnityEngine.HideFlags.DontSave) != UnityEngine.HideFlags.DontSave) noDontSave++;
            }
            return n;
        }

        // Live roller units summed over the projects whose machine report is fresh (-1 when a report is stale).
        public static int ReportedRollers()
        {
            uint now = RRWClock.UpdateIndex;
            int sum = 0;
            foreach (var p in SiteRegistry.Projects.Values)
            {
                if (p.RollersSpawned == 0) continue;
                if (!p.MachinesReportFresh(now)) return -1;
                sum += p.RollersSpawned;
            }
            return sum;
        }

        public static void OnLoaded(EntityManager em, EntityQuery derived, Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            try
            {
                ResetForLoad();
                int puffs = 0;
                if (!derived.IsEmptyIgnoreFilter)
                {
                    var data = derived.ToComponentDataArray<RRWDerived>(Allocator.Temp);
                    for (int i = 0; i < data.Length; i++) if (data[i].m_Group == (byte)DerivedGroup.DustPuff) puffs++;
                    data.Dispose();
                }
                LoadPuffs = puffs;
                LoadRollerObjects = RollerRoots(out LoadRollerNoDontSave);
                LoadWhen = purpose + " " + mode + " @update " + RRWClock.UpdateIndex;
                string msg = "persistence: load audit " + LoadWhen + ": dust puffs=" + LoadPuffs + " " + kRollerPrefix + "* GameObjects=" + LoadRollerObjects;
                if (LoadPuffs + LoadRollerObjects > 0) RRWLog.Warn(msg + " (must be 0: puffs carry LivePath, Machines destroys rollers in OnGamePreload)");
                else RRWLog.Info(msg + " ok");
            }
            catch (Exception e) { RRWLog.ErrorOnce("persistence load audit", e); }
        }

        public static void Check(EntityManager em, EntityQuery derived, List<string> problems)
        {
            Sample(em, derived);
            if (OverAge > 0) problems.Add("persistence: " + OverAge + " dust puff(s) older than " + kPuffMaxAge + " machine s (oldest " + RRWLog.F(OldestAge)
                                          + " s; Machines deletes them after kDustPuffLifeSeconds " + RRWConst.kDustPuffLifeSeconds + ")");
            if (NoLivePath > 0) problems.Add("persistence: " + NoLivePath + " dust puff(s) without LivePath (would be saved; SaveSanitize keeps them out)");
            if (Owned > 0) problems.Add("persistence: " + Owned + " dust puff(s) with an Owner (dust puffs must have no Owner)");
            if (LoadPuffs > 0) problems.Add("persistence: the last load (" + LoadWhen + ") brought " + LoadPuffs + " dust puff(s) out of the save");
            if (LoadRollerObjects > 0) problems.Add("persistence: " + LoadRollerObjects + " " + kRollerPrefix + "* GameObject(s) survived the last load (" + LoadWhen + "; Machines must destroy them in OnGamePreload)");
            var gm = GameManager.instance;
            bool inGame = gm != null && gm.gameMode.IsGame();
            int roots = RollerRoots(out int noDontSave);
            if (!inGame && roots > 0) problems.Add("persistence: " + roots + " " + kRollerPrefix + "* GameObject(s) outside game mode (" + (gm != null ? gm.gameMode.ToString() : "null") + "; Machines cleanup)");
            if (noDontSave > 0) problems.Add("persistence: " + noDontSave + " " + kRollerPrefix + "* root(s) without HideFlags.DontSave");
        }

        public static string Text()
        {
            int roots = RollerRoots(out int noDontSave);
            int reported = ReportedRollers();
            return "puffs live=" + Puffs + " oldest=" + RRWLog.F(OldestAge) + "s (max seen " + RRWLog.F(MaxAgeSeen) + "s, limit " + kPuffMaxAge + ") seen=" + SeenTotal
                   + " noLivePath=" + NoLivePath + " owned=" + Owned + " notHidden=" + Visible + " noSite=" + NoSite
                   + " | rollerObjects now=" + roots + (noDontSave > 0 ? "(" + noDontSave + " without DontSave)" : "")
                   + " units(fresh reports)=" + (reported < 0 ? "stale" : reported.ToString())
                   + " | load snapshot " + LoadWhen + ": puffs=" + LoadPuffs + " rollerObjects=" + LoadRollerObjects
                   + (OverAge + NoLivePath + Owned + math.max(0, LoadPuffs) + math.max(0, LoadRollerObjects) == 0 ? " ok" : " FAIL");
        }
    }
}
#endif
