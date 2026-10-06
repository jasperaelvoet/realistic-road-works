using System;
using System.Reflection;
using Game.Effects;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Props
{
    // Which effect the dust heaps carry (the clone can render black in game; see the open issue in DustVfx).
    internal enum DustSource : byte
    {
        Auto = 0,      // the "RRW Dust VFX" clone carriers while the clone's VFX slot is verified (or not yet checked), else vanilla
        Clone = 1,     // always the clone carriers (dev A/B)
        Vanilla = 2,   // always the carriers referencing the vanilla DustcloudSmallVFX (dev A/B)
    }

    // Runtime check of the "RRW Dust VFX" clone against VFXSystem's effect table, once per load.
    // VFXSystem.Initialize (after every PreDeserialize) creates one VisualEffect "VFX <prefab name>" per VFXData prefab and
    // writes its table index into VFXData.m_Index; instances are drawn by m_Effects[m_Index]. A clone that missed the table
    // keeps m_Index = 0 and is drawn by whatever graph sits in slot 0 (a smoke graph: dark smoky blobs). The clone is only
    // trusted when its index is in range, used by no other prefab, the slot's VisualEffect is named after the clone and
    // plays the same VisualEffectAsset as the vanilla DustcloudSmallVFX. Reflection only, read-only, never throws.
    internal static class DustVfx
    {
        // Open issue: the slot check below only rules out one cause (a clone drawn through another prefab's
        // slot); a clone that passes it may still render black. So EVERY build defaults to the
        // vanilla-effect carriers (the clone was seen rendering black in a DevTools build); the slot check still runs and
        // `rrw.props.dust auto|clone` keeps the A/B available. PropSystem publishes the choice as RRWPrefabRegistry.DustVanilla
        // (= UseVanilla) every update, so Machines' dusty excavator follows the same source.
        public const DustSource kDefaultMode = DustSource.Vanilla;
        public static DustSource Mode = kDefaultMode;
        // 0 = not checked yet, 1 = clone verified, -1 = clone invalid / missing. Mirrored into Core's
        // RRWPrefabRegistry.DustCloneState so Machines (which may not reference Props) can follow the same verdict.
        public static int State { get => RRWPrefabRegistry.DustCloneState; set => RRWPrefabRegistry.DustCloneState = value; }
        public static int Attempts;
        public static uint NextTry;
        public static string Report = "not checked";
        public const int kMaxAttempts = 60;   // every 30 updates: ~30 s of game time for VFXSystem to initialise after a load

        private static FieldInfo s_Initialized, s_Effects;

        public static void Reset()
        {
            State = 0;
            Attempts = 0;
            NextTry = 0;
            Report = "not checked (new load)";
        }

        // Use the vanilla-effect carriers?
        public static bool UseVanilla => Mode == DustSource.Vanilla || (Mode == DustSource.Auto && State < 0);

        public static string ModeName => Mode == DustSource.Auto ? "auto(" + (State > 0 ? "clone verified" : State < 0 ? "clone INVALID -> vanilla" : "unchecked -> clone") + ")" : Mode.ToString().ToLowerInvariant();

        // Returns false while VFXSystem has not built its table yet (retry later); true when State was decided.
        public static bool Check(World world, PrefabSystem ps, EntityQuery vfxQuery)
        {
            Attempts++;
            try
            {
                var em = world.EntityManager;
                Entity clone = PropClones.Resolve(ps, PropClones.DustVfx);
                Entity van = PropClones.Resolve(ps, PropClones.VanillaDust);
                if (clone == Entity.Null || !em.HasComponent<VFXData>(clone))
                {
                    State = -1;
                    Report = "clone missing (" + (PropClones.VfxSkipped ? "VFX registration skipped" : "not registered") + ")";
                    return true;
                }
                var vfx = world.GetExistingSystemManaged<VFXSystem>();
                if (vfx == null) { Report = "no VFXSystem"; return Attempts >= kMaxAttempts; }
                if (s_Initialized == null) s_Initialized = typeof(VFXSystem).GetField("m_Initialized", BindingFlags.NonPublic | BindingFlags.Instance);
                if (s_Effects == null) s_Effects = typeof(VFXSystem).GetField("m_Effects", BindingFlags.NonPublic | BindingFlags.Instance);
                if (s_Initialized == null || s_Effects == null) { Report = "VFXSystem fields not found (game update?): clone kept"; State = 1; return true; }
                if (!(s_Initialized.GetValue(vfx) is bool init) || !init)
                {
                    Report = "VFXSystem not initialised yet (attempt " + Attempts + ")";
                    return false;
                }
                var effects = s_Effects.GetValue(vfx) as Array;
                int len = effects != null ? effects.Length : 0;

                // index usage over every VFX prefab
                var all = vfxQuery.ToComponentDataArray<VFXData>(Allocator.Temp);
                int cloneIdx = em.GetComponentData<VFXData>(clone).m_Index;
                int vanIdx = van != Entity.Null && em.HasComponent<VFXData>(van) ? em.GetComponentData<VFXData>(van).m_Index : -1;
                int shared = 0;
                try { for (int i = 0; i < all.Length; i++) if (all[i].m_Index == cloneIdx) shared++; }
                finally { all.Dispose(); }

                Slot(effects, cloneIdx, out string cSlot, out string cAsset, out int cCount);
                Slot(effects, vanIdx, out string vSlot, out string vAsset, out int vCount);
                string expect = "VFX " + PropClones.DustVfx.name;
                bool ok = cloneIdx >= 0 && cloneIdx < len && shared == 1 && cSlot == expect && (vAsset == null || cAsset == vAsset);
                State = ok ? 1 : -1;
                Report = "clone idx=" + cloneIdx + "/" + len + " sharedBy=" + shared + " slot='" + cSlot + "' asset=" + (cAsset ?? "null") + " instances=" + cCount +
                         " maxCount=" + em.GetComponentData<VFXData>(clone).m_MaxCount +
                         " | vanilla idx=" + vanIdx + " slot='" + vSlot + "' asset=" + (vAsset ?? "null") + " instances=" + vCount +
                         " | " + (ok ? "VERIFIED" : "INVALID (expected slot '" + expect + "' with the vanilla asset, used by 1 prefab)");
                return true;
            }
            catch (Exception e)
            {
                Report = "check failed: " + e.GetType().Name + ": " + e.Message;
                if (Attempts >= kMaxAttempts) { State = 1; return true; }   // inconclusive: keep the clone (previous behaviour)
                return false;
            }
        }

        private static void Slot(Array effects, int idx, out string slot, out string asset, out int count)
        {
            slot = "none";
            asset = null;
            count = -1;
            if (effects == null || idx < 0 || idx >= effects.Length) return;
            object info = effects.GetValue(idx);
            if (info == null) return;
            var t = info.GetType();
            var ve = t.GetField("m_VisualEffect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(info) as UnityEngine.VFX.VisualEffect;
            if (ve != null)
            {
                slot = ve.gameObject != null ? ve.gameObject.name : ve.name;
                asset = ve.visualEffectAsset != null ? ve.visualEffectAsset.name : null;
            }
            var lc = t.GetField("m_LastCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(info);
            if (lc is int n) count = n;
        }
    }
}
