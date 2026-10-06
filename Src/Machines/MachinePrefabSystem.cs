using System;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using RealisticRoadWorks.Dev;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// MachinePrefabSystem (order 55, PrefabUpdate before PrefabInitializeSystem, main menu; verified in game):
// runtime clones "RRW Road Excavator" and "RRW Road Excavator Quiet" of MiningExcavator01 (the vanilla prefab is never
// modified: real mining extractors use it):
// - the two DustcloudVFX entries are dropped (huge and offset at root scale 0.4);
// - the two VehicleWorklightSource positions are multiplied by 0.4 (effect positions are not scaled by the root bone);
// - "Road Excavator" adds the vanilla DustcloudSmallVFX on the bucket bone with a 7 s dust pulse (Props' small
//   "RRW Dust VFX" clone only when Props publishes the clone dust source, RRWPrefabRegistry.DustVanilla == false, and the
//   clone exists - it rendered black); "Quiet" has no dust.
//   Because the dusty variant showed a dark blob at the bucket in play testing, the director spawns ONLY "Quiet" (digger
//   and grader); the dusty clone stays registered for evaluation with the dev command rrw.mx.dust 1;
// - the WorkVehicle component is removed, so WorkVehicleSelectData (which queries WorkVehicleData) never picks a clone.
namespace RealisticRoadWorks.V3.Machines
{
    [RegisterSystem(SystemUpdatePhase.PrefabUpdate, Before = typeof(PrefabInitializeSystem), Order = RRWOrder.MachineRegistry)]
    public partial class MachinePrefabSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private int m_Wait;
        private int m_Polls;
        private int m_EffectPolls;
        private readonly List<string> m_Report = new List<string>();

        public static bool DustyCloneOk;        // "RRW Road Excavator" (with bucket dust) registered
        public static string DustyCloneDust = "none";
        public static bool DustyCloneUsesRrwVfx;  // the dusty clone carries Props' "RRW Dust VFX" (not the vanilla fallback)

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate()
        {
            if (RRWPrefabRegistry.MachinesDone) return;
            if (--m_Wait > 0) return;
            m_Wait = 30;
            try
            {
                m_Polls++;
                bool inGame = GameManager.instance != null && GameManager.instance.gameMode.IsGameOrEditor();
                if (!FindSource(out PrefabBase src, out Entity srcEntity))
                {
                    if (m_Polls >= 120 || inGame)
                    {
                        RRWPrefabRegistry.MachinesDone = true;
                        RRWLog.Warn("machines: MiningExcavator01 not found after " + m_Polls + " polls: no RRW Road Excavator (FrontendLoader01 digs)");
                    }
                    return;
                }
                // wait (at most 20 polls) for Props' effect clones so the bucket dust is the small RRW Dust VFX
                if (!RRWPrefabRegistry.EffectsDone && ++m_EffectPolls < 20 && !inGame) return;
                Register(src, srcEntity, "PrefabUpdate poll " + m_Polls + (inGame ? " (city running)" : ""));
            }
            catch (Exception e)
            {
                RRWPrefabRegistry.MachinesDone = true;
                RRWLog.ErrorOnce("machines prefab registry", e);
            }
        }

        // Fallback: register before the city deserializes if the main-menu polls never completed.
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (RRWPrefabRegistry.MachinesDone || !mode.IsGameOrEditor()) return;
            try
            {
                if (FindSource(out PrefabBase src, out Entity srcEntity)) Register(src, srcEntity, "OnGamePreload(" + purpose + ")");
                else RRWPrefabRegistry.MachinesDone = true;
            }
            catch (Exception e)
            {
                RRWPrefabRegistry.MachinesDone = true;
                RRWLog.ErrorOnce("machines prefab registry (preload)", e);
            }
        }

        private bool FindSource(out PrefabBase src, out Entity entity)
        {
            entity = Entity.Null;
            src = null;
            if (!m_PrefabSystem.TryGetPrefab(new PrefabID(PrefabNames.TCar, PrefabNames.Excavator), out src) || src == null) return false;
            if (!m_PrefabSystem.TryGetEntity(src, out entity)) return false;
            return EntityManager.HasBuffer<SubMesh>(entity);
        }

        private void Register(PrefabBase src, Entity srcEntity, string where)
        {
            m_Report.Clear();
            var em = EntityManager;
            int subMeshCount = em.HasBuffer<SubMesh>(srcEntity) ? em.GetBuffer<SubMesh>(srcEntity, true).Length : 1;

            // dust effect: the vanilla small dust cloud whenever Props publishes the vanilla dust source
            // (RRWPrefabRegistry.DustVanilla, default true in EVERY build: the "RRW Dust VFX" clone renders black); Props'
            // "RRW Dust VFX" (dust clone, own VFX index) only when Props uses the clone source (DustVanilla false) and it exists
            EffectPrefab dust = null;
            string dustName = "none";
            if (!RRWPrefabRegistry.DustVanilla && RRWPrefabRegistry.EffectsOk &&
                m_PrefabSystem.TryGetPrefab(new PrefabID(PrefabNames.TEffect, PrefabNames.DustVfx), out PrefabBase d1) && d1 is EffectPrefab e1)
            { dust = e1; dustName = PrefabNames.DustVfx; }
            else if (m_PrefabSystem.TryGetPrefab(new PrefabID(PrefabNames.TEffect, PrefabNames.DustSmallVfx), out PrefabBase d2) && d2 is EffectPrefab e2)
            { dust = e2; dustName = PrefabNames.DustSmallVfx + (RRWPrefabRegistry.DustVanilla ? " (vanilla source)" : " (fallback)"); }

            // bucket bone (EffectSource bone parent = ProceduralBone connection id, RenderingUtils.FindBoneIndex)
            BucketBone(em, srcEntity, subMeshCount, out int connection, out float3 bucketPos);

            bool okDust = MakeClone(src, PrefabNames.RoadExcavator, dust, connection, bucketPos, subMeshCount);
            DustyCloneOk = okDust && dust != null;
            DustyCloneDust = dustName;
            DustyCloneUsesRrwVfx = dustName == PrefabNames.DustVfx;
            bool okQuiet = MakeClone(src, PrefabNames.RoadExcavatorQuiet, null, connection, bucketPos, subMeshCount);
            RRWPrefabRegistry.ExcavatorOk = okDust || okQuiet;
            RRWPrefabRegistry.MachinesDone = true;
            var sb = new StringBuilder();
            sb.Append("machines: excavator clones ").Append(RRWPrefabRegistry.ExcavatorOk ? "ok" : "FAILED").Append(" in ").Append(where)
              .Append(" dust=").Append(dustName).Append(" bucketBone=").Append(connection > 0 ? connection.ToString() : "none(fixed position)")
              .Append(" effectsDone=").Append(RRWPrefabRegistry.EffectsDone).Append(" effectsOk=").Append(RRWPrefabRegistry.EffectsOk)
              .Append(" dustVanilla=").Append(RRWPrefabRegistry.DustVanilla)
              .Append(" spawned: Quiet clone for digger + grader (bucket dust off; dev rrw.mx.dust 1 to evaluate the dusty clone)");
            RRWLog.Info(sb.ToString());
            foreach (var line in m_Report) RRWLog.Info("machines:   " + line);
        }

        private void BucketBone(EntityManager em, Entity prefab, int subMeshCount, out int connection, out float3 pos)
        {
            connection = 0;
            pos = new float3(0f, 0.3f, 6.0f);   // fixed: the digging face in front of the scaled machine (world metres)
            try
            {
                var rig = Rigs.Get(em, m_PrefabSystem, prefab);
                if (!rig.Find("Arm02Bucket", out int sub, out int bone) && !rig.Find("Bucket", out sub, out bone)) return;
                var rs = rig.GetSub(sub);
                if (rs == null || !em.HasBuffer<ProceduralBone>(rs.Mesh)) return;
                var pb = em.GetBuffer<ProceduralBone>(rs.Mesh, true);
                if (bone < 0 || bone >= pb.Length) return;
                int id = pb[bone].m_ConnectionID;
                if (id > 0 && id >= subMeshCount)
                {
                    connection = id;
                    pos = pb[bone].m_ObjectPosition;   // object space of the unscaled rig: the bone matrix carries the 0.4 root scale
                }
            }
            catch (Exception e) { m_Report.Add("bucket bone lookup failed: " + e.Message); }
        }

        private bool MakeClone(PrefabBase src, string name, EffectPrefab dust, int connection, float3 bucketPos, int subMeshCount)
        {
            try
            {
                if (m_PrefabSystem.TryGetPrefab(new PrefabID(PrefabNames.TCar, name), out PrefabBase existing) && existing != null)
                {
                    m_Report.Add(name + " already registered");
                    return true;
                }
                var c = src.Clone(name);
                c.Remove<ObsoleteIdentifiers>();
                c.Remove<SpawnableObject>();
                c.Remove<UIObject>();
                c.Remove<Unlockable>();
                c.Remove<EditorAssetCategoryOverride>();
                bool hadWork = c.Has<Game.Prefabs.WorkVehicle>();
                c.Remove<Game.Prefabs.WorkVehicle>();
                int dropped = 0, scaled = 0;
                if (c.TryGet(out EffectSource es) && es.m_Effects != null)
                {
                    var list = new List<EffectSource.EffectSettings>(es.m_Effects.Count + 1);
                    foreach (var e in es.m_Effects)
                    {
                        if (e == null) continue;
                        string en = e.m_Effect != null ? e.m_Effect.name : "";
                        if (en == "DustcloudVFX") { dropped++; continue; }
                        var copy = new EffectSource.EffectSettings
                        {
                            m_Effect = e.m_Effect, m_PositionOffset = e.m_PositionOffset, m_Rotation = e.m_Rotation, m_Scale = e.m_Scale,
                            m_Intensity = e.m_Intensity, m_ParentMesh = e.m_ParentMesh, m_AnimationIndex = e.m_AnimationIndex,
                        };
                        if (en.IndexOf("Worklight", StringComparison.OrdinalIgnoreCase) >= 0 && e.m_ParentMesh < subMeshCount)
                        {
                            copy.m_PositionOffset = e.m_PositionOffset * RRWConst.kExcavatorScale;
                            scaled++;
                        }
                        list.Add(copy);
                    }
                    var curves = es.m_AnimationCurves != null ? new List<EffectSource.AnimationProperties>(es.m_AnimationCurves) : new List<EffectSource.AnimationProperties>();
                    if (dust != null)
                    {
                        curves.Add(new EffectSource.AnimationProperties { m_Duration = RRWConst.kDustPulseSeconds, m_Curve = Pulse() });
                        list.Add(new EffectSource.EffectSettings
                        {
                            m_Effect = dust,
                            m_PositionOffset = bucketPos,
                            m_Rotation = quaternion.identity,
                            m_Scale = new float3(1f, 1f, 1f),
                            m_Intensity = 1f,
                            m_ParentMesh = connection > 0 ? connection : 0,
                            m_AnimationIndex = curves.Count - 1,
                        });
                    }
                    es.m_Effects = list;
                    es.m_AnimationCurves = curves;
                }
                else m_Report.Add(name + ": no EffectSource on the source prefab (dust unchanged)");
                if (!m_PrefabSystem.AddPrefab(c))
                {
                    m_Report.Add(name + " AddPrefab returned false");
                    UnityEngine.Object.Destroy(c);
                    return false;
                }
                m_Report.Add(name + " added: droppedDust=" + dropped + " worklightsScaled=" + scaled + " workVehicleRemoved=" + hadWork + (dust != null ? " +" + dust.name + " pulse " + RRWConst.kDustPulseSeconds + "s" : " (quiet)"));
                return true;
            }
            catch (Exception e)
            {
                m_Report.Add(name + " failed: " + e.Message);
                return false;
            }
        }

        // Dust pulse: puff, hold, fade, pause (sampled into 31 points over the duration).
        private static AnimationCurve Pulse() => new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(0.15f, 1f), new Keyframe(0.45f, 1f), new Keyframe(0.65f, 0f), new Keyframe(1f, 0f));
    }
}
