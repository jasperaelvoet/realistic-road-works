using System;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Game.Rendering;
using RealisticRoadWorks.Dev;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

// SurfaceRegistrySystem (order 50): registers the RRW SurfacePrefab clones once per process in the main menu,
// in PrefabUpdate right before PrefabInitializeSystem. Port of the surfaces prototype (verified in game):
// RenderedArea copied from the vanilla source ONLY (never Spawnable/UIObject/Unlockable/ObsoleteIdentifiers), texture
// swap with held TextureAssets for gravel / fresh asphalt, the Ore material untouched (tinted) for dirt and topsoil.
// Always sets RRWPrefabRegistry.SurfacesDone (ok or failed). Disables itself once every clone has a batch material.
namespace RealisticRoadWorks.V3.Surfaces
{
    [RegisterSystem(SystemUpdatePhase.PrefabUpdate, Before = typeof(PrefabInitializeSystem), Order = RRWOrder.SurfaceRegistry)]
    public partial class SurfaceRegistrySystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_SurfaceQuery;
        private int m_Frames;
        private int m_Attempts;
        private int m_TexWaits;
        private int m_ReadyChecks;
        private int m_GameChecks;
        private bool m_Failed;
        private readonly HashSet<string> m_ReadyLogged = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TextureAsset> m_TexCache = new Dictionary<string, TextureAsset>(StringComparer.OrdinalIgnoreCase);

        protected override void OnCreate()
        {
            base.OnCreate();
            try
            {
                m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
                m_SurfaceQuery = GetEntityQuery(ComponentType.ReadOnly<SurfaceData>(), ComponentType.ReadOnly<PrefabData>());
            }
            catch (Exception e)
            {
                m_Failed = true;
                RRWPrefabRegistry.SurfacesDone = true;
                RRWLog.ErrorOnce("surfaces registry OnCreate", e);
            }
        }

        protected override void OnUpdate()
        {
            if (m_Failed) { RRWPrefabRegistry.SurfacesDone = true; Enabled = false; return; }
            try
            {
                m_Frames++;
                if (!SurfaceState.Registered)
                {
                    if (m_Frames % 30 != 1) return;   // twice a second at most
                    TryRegister("PrefabUpdate", force: false);
                    return;
                }
                if (m_Frames % 30 == 0) CheckReady();
            }
            catch (Exception e)
            {
                m_Failed = true;
                SurfaceState.Registered = true;
                RRWPrefabRegistry.SurfacesDone = true;
                RRWLog.ErrorOnce("surfaces registry", e);
                RRWLog.Warn("surfaces: registry disabled after an exception; works sites will have no ground textures");
            }
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            try
            {
                if (!SurfaceState.Registered && !m_Failed)
                {
                    RRWLog.Warn("surfaces: clones not registered before the game preload, registering now (fallback)");
                    TryRegister("OnGamePreload", force: true);
                }
            }
            catch (Exception e)
            {
                SurfaceState.Registered = true;
                RRWPrefabRegistry.SurfacesDone = true;
                RRWLog.ErrorOnce("surfaces registry preload", e);
            }
        }

        protected override void OnDestroy()
        {
            foreach (var t in SurfaceState.HeldTextures)
            {
                try { t.Unload(); } catch { }
            }
            SurfaceState.HeldTextures.Clear();
            base.OnDestroy();
        }

        private Dictionary<string, SurfacePrefab> SourceSurfaces()
        {
            var d = new Dictionary<string, SurfacePrefab>(StringComparer.Ordinal);
            if (m_SurfaceQuery.IsEmptyIgnoreFilter) return d;
            var arr = m_SurfaceQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                    if (m_PrefabSystem.TryGetPrefab<SurfacePrefab>(arr[i], out var p) && p != null && !d.ContainsKey(p.name))
                        d.Add(p.name, p);
            }
            finally { arr.Dispose(); }
            return d;
        }

        private void TryRegister(string when, bool force)
        {
            m_Attempts++;
            var sources = SourceSurfaces();
            var missing = new List<string>();
            foreach (var s in SurfaceState.Specs)
                if (!sources.ContainsKey(s.Source) && !missing.Contains(s.Source)) missing.Add(s.Source);
            if (missing.Count > 0 && !force && m_Attempts < 240)
            {
                if (m_Attempts == 1 || m_Attempts % 40 == 0)
                    RRWLog.Info("surfaces: registry waiting for vanilla surfaces (" + sources.Count + " known), missing: " + string.Join(", ", missing));
                return;
            }
            // Textures: if not a single wanted texture resolves yet, the AssetDatabase may still be mounting (~10 s max).
            int wanted = 0, found = 0;
            var probe = new List<string>();
            foreach (var spec in SurfaceState.Specs)
            {
                if (spec.BaseTex == null) continue;
                wanted++;
                if (FindTexture(spec.BaseTex, probe) != null) found++;
            }
            if (wanted > 0 && found == 0 && !force && ++m_TexWaits < 20)
            {
                foreach (var k in new List<string>(m_TexCache.Keys)) if (m_TexCache[k] == null) m_TexCache.Remove(k);
                if (m_TexWaits == 1) RRWLog.Info("surfaces: registry waiting for textures: " + string.Join("; ", probe));
                return;
            }
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            int ok = 0, fail = 0;
            foreach (var spec in SurfaceState.Specs)
            {
                if (SurfaceState.Clones.ContainsKey(spec.Name)) continue;
                if (!sources.TryGetValue(spec.Source, out var src))
                {
                    fail++;
                    RRWLog.Warn("surfaces: clone \"" + spec.Name + "\" FAILED: source \"" + spec.Source + "\" not found");
                    continue;
                }
                try { if (Register(spec, src)) ok++; else fail++; }
                catch (Exception e)
                {
                    fail++;
                    RRWLog.ErrorOnce("surfaces registry clone " + spec.Name, e);
                }
            }
            SurfaceState.Registered = true;
            RRWPrefabRegistry.SurfacesDone = true;
            SurfaceState.RegistrationInfo = when + " attempts=" + m_Attempts + " ok=" + ok + " fail=" + fail + " ms=" + sw.ElapsedMilliseconds;
            RRWLog.Info("surfaces: registry done when=" + when + " ok=" + ok + " fail=" + fail + " ms=" + sw.ElapsedMilliseconds
                        + " gameMode=" + (Game.SceneFlow.GameManager.instance != null ? Game.SceneFlow.GameManager.instance.gameMode.ToString() : "?"));
        }

        private bool Register(SurfaceCloneSpec spec, SurfacePrefab src)
        {
            var srcRA = src.GetComponent<RenderedArea>();
            if (srcRA == null)
            {
                RRWLog.Warn("surfaces: clone \"" + spec.Name + "\" FAILED: source has no RenderedArea");
                return false;
            }
            var p = ScriptableObject.CreateInstance<SurfacePrefab>();
            p.name = spec.Name;
            p.m_Color = src.m_Color;
            p.m_EdgeColor = src.m_EdgeColor;
            p.m_SelectionColor = src.m_SelectionColor;
            p.m_SelectionEdgeColor = src.m_SelectionEdgeColor;
            // RenderedArea ONLY (found in testing): no SpawnableArea / UIObject / Unlockable / ServiceObject / ObsoleteIdentifiers,
            // so placeholders never pick the clone and it never shows in menus.
            var ra = (RenderedArea)p.AddComponentFrom(srcRA);
            ra.m_DecalLayerMask = spec.Layers;
            ra.m_RendererPriority = spec.Priority;
            ra.m_Metallic = 0f;
            ra.m_BaseColor = SurfaceState.ColorOf(spec);
            if (spec.Smoothness >= 0f) ra.m_Smoothness = spec.Smoothness;
            // Roundness / edge noise / LOD bias of the line clones (experimental switches). AreaBatchSystem grows every
            // area by clamp(m_Roundness, .01, .99) * 0.75 / 2 per side (RenderedAreaData.m_ExpandAmount)
            // and AreaInitializeSystem copies m_LodBias into AreaGeometryData.
            string srcStyle = "srcRound=" + RRWLog.F(srcRA.m_Roundness) + " srcNoise=(" + RRWLog.F(srcRA.m_EdgeNoise.x) + "," + RRWLog.F(srcRA.m_EdgeNoise.y)
                              + ") srcFade=(" + RRWLog.F(srcRA.m_EdgeFadeRange.x) + "," + RRWLog.F(srcRA.m_EdgeFadeRange.y) + ") srcLod=" + RRWLog.F(srcRA.m_LodBias);
            if (spec.Roundness >= 0f) ra.m_Roundness = Unity.Mathematics.math.clamp(spec.Roundness, 0f, 1f);
            if (!float.IsNaN(spec.EdgeNoise.x)) ra.m_EdgeNoise = spec.EdgeNoise;
            if (!float.IsNaN(spec.LodBias)) ra.m_LodBias = spec.LodBias;

            string mode;
            var info = new List<string>();
            TextureAsset baseT = spec.BaseTex != null ? FindTexture(spec.BaseTex, info) : null;
            if (baseT != null)
            {
                TextureAsset normT = spec.NormalTex != null ? FindTexture(spec.NormalTex, info) : null;
                TextureAsset maskT = spec.MaskTex != null ? FindTexture(spec.MaskTex, info) : null;
                ra.m_Material = null;   // AreaBatchSystem uses the area material with these maps (OverrideMaterial)
                ra.m_Version = RenderedArea.Version.OverrideMaterial;
                ra.m_BaseColorMap = baseT;
                if (normT != null) ra.m_NormalMap = normT; else ra.m_NormalMap = srcRA.m_NormalMap;
                if (maskT != null) ra.m_MaskMap = maskT; else ra.m_MaskMap = srcRA.m_MaskMap;
                mode = (spec.NormalTex != null && normT == null) || (spec.MaskTex != null && maskT == null) ? "swap-partial" : "swap";
            }
            else
            {
                // Verified in game ("Src" recipe): the source surface's own material/textures with a tint
                if (srcRA.m_Material != null)
                {
                    ra.m_Material = srcRA.m_Material;
                    ra.m_Version = RenderedArea.Version.OverrideMaterial;
                    mode = "source-material";
                }
                else
                {
                    ra.m_Material = null;
                    ra.m_BaseColorMap = srcRA.m_BaseColorMap;
                    ra.m_NormalMap = srcRA.m_NormalMap;
                    ra.m_MaskMap = srcRA.m_MaskMap;
                    mode = "source";
                }
                if (spec.BaseTex != null) info.Add("WANTED " + spec.BaseTex + " NOT FOUND -> source textures");
            }
            if (!m_PrefabSystem.AddPrefab(p))
            {
                RRWLog.Warn("surfaces: clone \"" + spec.Name + "\" FAILED: AddPrefab returned false");
                UnityEngine.Object.Destroy(p);
                return false;
            }
            var entity = m_PrefabSystem.GetEntity(p);
            var clone = new SurfaceClone
            {
                Spec = spec, Prefab = p, Entity = entity, TexMode = mode, SourceStyle = srcStyle,
                AppliedRoundness = ra.m_Roundness, AppliedLodBias = ra.m_LodBias,
            };
            SurfaceState.Clones[spec.Name] = clone;
            SurfaceState.CloneEntities.Add(entity);
            SurfaceState.ByEntity[entity] = clone;
            RRWLog.Info("surfaces: clone \"" + spec.Name + "\" entity=" + RRWLog.E(entity) + " source=\"" + spec.Source + "\" tex=" + mode
                        + " prio=" + spec.Priority + " alpha=" + RRWLog.F(spec.Alpha) + " smooth=" + RRWLog.F(ra.m_Smoothness)
                        + (spec.Roundness >= 0f || spec.TempSource >= 0 || spec.TestLine
                           ? " round=" + RRWLog.F(ra.m_Roundness) + " lod=" + RRWLog.F(ra.m_LodBias) + " queueRaise=+" + SurfaceState.QueueRaiseOf(spec) + " " + srcStyle : "")
                        + (info.Count > 0 ? " " + string.Join("; ", info) : ""));
            return true;
        }

        // Finds a non-dummy Texture2D TextureAsset whose name starts with prefix, Load()s and holds it.
        private TextureAsset FindTexture(string prefix, List<string> info)
        {
            if (m_TexCache.TryGetValue(prefix, out var cached)) return cached;
            TextureAsset best = null;
            string why = "none matched";
            foreach (var t in AssetDatabase.global.GetAssets<TextureAsset>(SearchFilter<TextureAsset>.ByCondition(x => x != null && x.name != null && x.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
            {
                if (t.isDummy) { why = "only dummies"; continue; }
                if (!TryHeader(t, out var dim)) { why = "header unreadable"; continue; }
                if (dim != TextureDimension.Tex2D) { why = "not Tex2D"; continue; }
                if (best == null || (t.isBuiltin && !best.isBuiltin)) best = t;
            }
            if (best != null)
            {
                Texture tex = null;
                try { tex = best.Load(); } catch (Exception e) { why = "Load threw " + e.Message; }
                if (tex is Texture2D)
                {
                    SurfaceState.HeldTextures.Add(best);
                    m_TexCache[prefix] = best;
                    info.Add(prefix + "->" + best.name);
                    return best;
                }
                if (tex != null) { try { best.Unload(); } catch { } why = "loaded as " + tex.GetType().Name; }
            }
            info.Add(prefix + "->MISSING(" + why + ")");
            m_TexCache[prefix] = null;
            return null;
        }

        // TextureAsset header (same layout as TextureAsset.ReadHeader) without touching the asset's state.
        private static bool TryHeader(TextureAsset t, out TextureDimension dim)
        {
            dim = TextureDimension.Tex2D;
            if (t == null || t.isDummy) return false;
            try
            {
                using (var s = t.GetReadStream())
                {
                    if (s == null) return false;
                    using (var br = new global::System.IO.BinaryReader(s))
                    {
                        int version = br.ReadUInt16();
                        br.ReadUInt16(); br.ReadUInt16();
                        if (version >= 4) br.ReadUInt16();
                        br.ReadByte();
                        if (version >= 3) br.ReadByte(); else br.ReadString();
                        if (version >= 4) dim = (TextureDimension)br.ReadByte();
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // Every 30 frames until every clone has a valid archetype and an "Area batch (<name>)" material; then disables itself.
        private void CheckReady()
        {
            m_ReadyChecks++;
            var gm = Game.SceneFlow.GameManager.instance;
            if (gm != null && gm.gameMode == GameMode.Game) m_GameChecks++;
            bool giveUp = m_GameChecks >= 40;
            bool all = true;
            var abs = World.GetExistingSystemManaged<AreaBatchSystem>();
            foreach (var c in SurfaceState.Clones.Values)
            {
                if (m_ReadyLogged.Contains(c.Spec.Name)) continue;
                var em = EntityManager;
                bool exists = em.Exists(c.Entity);
                bool arch = exists && em.HasComponent<AreaData>(c.Entity) && em.GetComponentData<AreaData>(c.Entity).m_Archetype.Valid;
                bool haveMat = SurfaceBatch.Find(abs, em, c, out var entry);
                if (arch && haveMat)
                {
                    m_ReadyLogged.Add(c.Spec.Name);
                    RRWLog.Verbose("surfaces: clone ready \"" + c.Spec.Name + "\" batch=" + entry.Index + " queue=" + entry.Material.renderQueue + " prio=" + entry.Priority);
                }
                else
                {
                    all = false;
                    if (giveUp) RRWLog.Warn("surfaces: clone \"" + c.Spec.Name + "\" NOT READY after " + m_GameChecks + " in-game polls: exists=" + exists + " archetype=" + arch + " batch=" + haveMat);
                }
            }
            if (all || giveUp)
            {
                RRWLog.Info("surfaces: " + m_ReadyLogged.Count + "/" + SurfaceState.Clones.Count + " clones ready (" + SurfaceState.RegistrationInfo + ")");
                Enabled = false;
            }
        }
    }
}
