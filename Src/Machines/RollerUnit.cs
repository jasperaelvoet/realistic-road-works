using System;
using System.Collections.Generic;
using Color = UnityEngine.Color;
using Transform = UnityEngine.Transform;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Road roller units (a port of the roller prototype, verified in game).
// A roller is a plain Unity GameObject hierarchy (MeshRenderer, runtime HDRP/Lit, layer 0, shadows on), never an ECS entity, so it
// can never reach a save. Its motion is NOT simulated here: RollerRenderSystem poses it every rendered frame from its Puppet's
// MachinePlan at the machine clock (the puppets' clock), so the planner, the report, the guard and the picture agree exactly.
//   Root  (articulation joint on the ground, rear-frame heading)
//    +- Rear  (rear frame + platform + ROPS)   +- RearDrum (axle pivot, spins about X)
//    +- Front (front frame, articulates)       +- FrontDrum
// Shared meshes (RollerModel) and SHARED materials: lights switch by swapping shared on / off materials on a state change, the
// beacon uses kRollerBeaconPhases shared phase materials updated once per frame. Nothing is allocated per frame.
namespace RealisticRoadWorks.V3.Machines
{
    public static class RollerMaterials
    {
        public static readonly Color Paint = new Color(1.0f, 0.72f, 0.02f);      // BOMAG / Dynapac yellow (sRGB), verified in game
        public static readonly Color BeaconColor = new Color(1f, 0.55f, 0.05f);
        public static readonly Color LampColor = new Color(1f, 0.96f, 0.88f);
        public static readonly Color TailColor = new Color(1f, 0.05f, 0.03f);
        public static float BeaconNits = RRWConst.kRollerBeaconNits;            // dev rrw.mx.roller beacon <nits>

        // shared materials: [0] paint, [1] steel, [2] dark, [3] lamp off, [4] lamp on, [5] tail off, [6] tail on, [7] beacon off,
        // [8 .. 8 + phases) beacon phase materials
        private static Material[] s_Mats;
        private static readonly float[] s_BeaconLevel = new float[RRWConst.kRollerBeaconPhases];
        // material arrays per (part, lod, lamp, tail, beacon) - built lazily, reused (no per-frame allocation)
        private static Material[][] s_Sets;
        public static string Source = "";

        public const int kBeaconOff = RRWConst.kRollerBeaconPhases;   // beacon index of "off"

        public static bool Ready => s_Mats != null && s_Mats[0] != null;

        static Shader LitShader()
        {
            var sh = Shader.Find("HDRP/Lit");
            if (sh == null) RRWLog.Once("roller-nolit", "machines: roller shader HDRP/Lit not found - falling back to BH/SG_DefaultShader");
            if (sh == null) sh = Shader.Find("BH/SG_DefaultShader");
            return sh;
        }

        static Material MakeLit(string name, Color srgb, float metallic, float smooth)
        {
            var sh = LitShader();
            if (sh == null) return null;
            var m = new Material(sh) { name = name, hideFlags = HideFlags.DontSave, enableInstancing = true };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", srgb);
            if (m.HasProperty("_Color")) m.SetColor("_Color", srgb);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            // no decals: road markings / puddle decals never project onto the machine (verified in game)
            if (m.HasProperty("_SupportDecals")) m.SetFloat("_SupportDecals", 0f);
            // The game's own decal receiver mask (int bits stored as a float, like every vanilla vehicle material). Unset, the
            // roller receives the Terrain layer: gravel / asphalt / dirt area surfaces projected onto its body as dark streaks.
            m.SetFloat("colossal_DecalLayerMask", math.asfloat((int)Game.Rendering.DecalLayers.Vehicles));
            if (m.HasProperty("_EmissiveColor")) m.SetVector("_EmissiveColor", Vector4.zero);
            try { HDMaterial.ValidateMaterial(m); }
            catch (Exception e) { RRWLog.Once("roller-validate", "machines: roller HDMaterial.ValidateMaterial failed: " + e.Message); }
            return m;
        }

        // Emission in nits (cd/m2), linear colour. SetVector, not SetColor: SetColor would gamma-convert HDR values (verified in game).
        static void SetEmission(Material m, Color srgb, float nits)
        {
            if (m == null || !m.HasProperty("_EmissiveColor")) return;
            Color lin = srgb.linear;
            m.SetVector("_EmissiveColor", new Vector4(lin.r * nits, lin.g * nits, lin.b * nits, 1f));
        }

        public static void EnsureBuilt()
        {
            if (Ready) return;
            int n = 8 + RRWConst.kRollerBeaconPhases;
            var mats = new Material[n];
            mats[0] = MakeLit("RRW Roller Paint", Paint, 0f, 0.55f);
            mats[1] = MakeLit("RRW Roller Steel", new Color(0.56f, 0.57f, 0.58f), 0.85f, 0.45f);
            mats[2] = MakeLit("RRW Roller Dark", new Color(0.07f, 0.07f, 0.075f), 0f, 0.30f);
            mats[3] = MakeLit("RRW Roller Lamp Off", new Color(0.95f, 0.95f, 0.92f), 0f, 0.9f);
            mats[4] = MakeLit("RRW Roller Lamp On", new Color(0.95f, 0.95f, 0.92f), 0f, 0.9f);
            mats[5] = MakeLit("RRW Roller Tail Off", new Color(0.7f, 0.05f, 0.03f), 0f, 0.8f);
            mats[6] = MakeLit("RRW Roller Tail On", new Color(0.7f, 0.05f, 0.03f), 0f, 0.8f);
            mats[7] = MakeLit("RRW Roller Beacon Off", new Color(1f, 0.6f, 0.15f), 0f, 0.8f);
            for (int i = 0; i < RRWConst.kRollerBeaconPhases; i++) mats[8 + i] = MakeLit("RRW Roller Beacon " + i, new Color(1f, 0.6f, 0.15f), 0f, 0.8f);
            SetEmission(mats[4], LampColor, RRWConst.kRollerLampNits);
            SetEmission(mats[6], TailColor, RRWConst.kRollerTailNits);
            for (int i = 0; i < s_BeaconLevel.Length; i++) s_BeaconLevel[i] = -1f;
            s_Mats = mats;
            s_Sets = new Material[3 * 2 * 2 * 2 * (RRWConst.kRollerBeaconPhases + 1)][];
            Source = "HDRP/Lit (runtime, ValidateMaterial)";
        }

        // Beacon double flash at ~0.8 Hz on the machine clock (frozen while paused), one shared material per phase offset. At most
        // kRollerBeaconPhases SetVector calls per frame, only when a level changes.
        public static void UpdateBeacons(double tau)
        {
            if (!Ready) return;
            for (int i = 0; i < RRWConst.kRollerBeaconPhases; i++)
            {
                double ph = tau * 0.8 + i / (double)RRWConst.kRollerBeaconPhases;
                float p = (float)(ph - math.floor(ph));
                bool on = p < 0.07f || (p > 0.15f && p < 0.22f);
                float level = BeaconNits <= 0f ? 0f : on ? BeaconNits : BeaconNits * 0.02f;
                if (level == s_BeaconLevel[i]) continue;
                s_BeaconLevel[i] = level;
                SetEmission(s_Mats[8 + i], BeaconColor, level);
            }
        }

        public static void ResetBeaconLevels()
        {
            for (int i = 0; i < s_BeaconLevel.Length; i++) s_BeaconLevel[i] = -1f;
        }

        // Material array of one part (0 rear, 1 front, 2 drum) at one LOD for the light state (cached).
        public static Material[] Set(int part, int lod, bool lamps, bool tail, int beacon, RollerBuiltMesh bm)
        {
            int key = ((((part * 2 + lod) * 2 + (lamps ? 1 : 0)) * 2 + (tail ? 1 : 0)) * (RRWConst.kRollerBeaconPhases + 1)) + math.clamp(beacon, 0, kBeaconOff);
            var arr = s_Sets[key];
            if (arr != null) return arr;
            arr = new Material[bm.Slots.Length];
            for (int k = 0; k < arr.Length; k++)
            {
                switch (bm.Slots[k])
                {
                    case RollerMatSlot.Paint: arr[k] = s_Mats[0]; break;
                    case RollerMatSlot.Steel: arr[k] = s_Mats[1]; break;
                    case RollerMatSlot.Dark: arr[k] = s_Mats[2]; break;
                    case RollerMatSlot.Lamp: arr[k] = s_Mats[lamps ? 4 : 3]; break;
                    case RollerMatSlot.Tail: arr[k] = s_Mats[tail ? 6 : 5]; break;
                    default: arr[k] = beacon >= kBeaconOff ? s_Mats[7] : s_Mats[8 + beacon]; break;   // beacon lens
                }
            }
            s_Sets[key] = arr;
            return arr;
        }

        public static void Destroy()
        {
            if (s_Mats != null)
                foreach (var m in s_Mats) if (m != null) UnityEngine.Object.Destroy(m);
            s_Mats = null;
            s_Sets = null;
        }
    }

    // One roller's GameObjects. Poses are written by RollerRenderSystem (Apply); the unit holds no motion state of its own.
    public sealed class RollerUnit
    {
        public int Id;
        public GameObject Root, Rear, Front, RearDrum, FrontDrum;
        private MeshFilter[] m_Filters;      // rear, front, rearDrum, frontDrum
        private MeshRenderer[] m_Renderers;
        public int Lod = -1;
        public int MatKey = -1;
        public int BeaconPhase;              // shared beacon material (seeded)
        public float ArticulationDeg, DrumDeg, CamDist;
        public bool Vibrating;
        public double LastTau = double.NaN;
        public Bounds Bounds;                // last world bounds (dev)
        private uint m_Rand = 0x9E3779B9u;

        public const int Layer = 0;          // Unity layer 0 "Default": what ManagedBatchSystem uses for MeshLayer.Default (verified in game)

        public static RollerUnit Create(int id, int seed)
        {
            RollerModel.EnsureBuilt();
            RollerMaterials.EnsureBuilt();
            var u = new RollerUnit { Id = id, BeaconPhase = (int)((uint)seed % (uint)RRWConst.kRollerBeaconPhases), m_Rand = 0x9E3779B9u ^ (uint)(id * 7919 + 1) };
            u.Root = new GameObject("RRW_Roller_" + id) { hideFlags = HideFlags.DontSave, layer = Layer };
            RollerWorld.Created.Add(u.Root);
            u.Rear = u.Part("Rear", u.Root.transform, Vector3.zero);
            u.Front = u.Part("Front", u.Root.transform, Vector3.zero);
            u.RearDrum = u.Part("RearDrum", u.Rear.transform, new Vector3(0f, RollerModel.DrumR, -RollerModel.WheelbaseHalf));
            u.FrontDrum = u.Part("FrontDrum", u.Front.transform, new Vector3(0f, RollerModel.DrumR, RollerModel.WheelbaseHalf));
            u.m_Filters = new[] { u.Rear.GetComponent<MeshFilter>(), u.Front.GetComponent<MeshFilter>(), u.RearDrum.GetComponent<MeshFilter>(), u.FrontDrum.GetComponent<MeshFilter>() };
            u.m_Renderers = new[] { u.Rear.GetComponent<MeshRenderer>(), u.Front.GetComponent<MeshRenderer>(), u.RearDrum.GetComponent<MeshRenderer>(), u.FrontDrum.GetComponent<MeshRenderer>() };
            for (int i = 0; i < 4; i++) u.m_Renderers[i].enabled = false;   // nothing drawn before the first pose
            return u;
        }

        private GameObject Part(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave, layer = Layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;   // moving object: no TAA smearing
            r.renderingLayerMask = uint.MaxValue;                               // same as the game's BRG draws
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            r.allowOcclusionWhenDynamic = true;
            return go;
        }

        static RollerBuiltMesh MeshFor(int part, int lod)
        {
            switch (part)
            {
                case 0: return RollerModel.Rear[lod];
                case 1: return RollerModel.Front[lod];
                default: return RollerModel.Drum[lod];
            }
        }

        // LOD 0 / 1 / 2 (2 = renderers disabled) and the light state (shared material swap only on a change).
        public void SetLodAndLights(int lod, bool lamps, bool tail, int beacon)
        {
            int key = lod >= 2 ? -2 : ((lod * 2 + (lamps ? 1 : 0)) * 2 + (tail ? 1 : 0)) * (RRWConst.kRollerBeaconPhases + 1) + beacon;
            if (lod == Lod && key == MatKey) return;
            bool meshChange = lod != Lod;
            Lod = lod;
            MatKey = key;
            for (int i = 0; i < 4; i++)
            {
                var r = m_Renderers[i];
                if (lod >= 2) { r.enabled = false; continue; }
                int part = i < 2 ? i : 2;
                var bm = MeshFor(part, lod);
                if (meshChange || m_Filters[i].sharedMesh != bm.Mesh) m_Filters[i].sharedMesh = bm.Mesh;
                r.sharedMaterials = RollerMaterials.Set(part, lod, lamps, tail, beacon, bm);
                r.enabled = true;
            }
        }

        public float Rand()
        {
            m_Rand ^= m_Rand << 13; m_Rand ^= m_Rand >> 17; m_Rand ^= m_Rand << 5;
            return (m_Rand & 0xFFFFFF) / (float)0xFFFFFF * 2f - 1f;
        }

        // Writes the pose: root (joint on the ground), front-frame articulation, drum spin, vibration shimmer.
        public void Apply(Vector3 rootPos, Quaternion rootRot, Quaternion frontLocal, float drumDeg, float vibAmp)
        {
            Root.transform.SetPositionAndRotation(rootPos, rootRot);
            Front.transform.localRotation = frontLocal;
            // the drums carry the exciter; the frames sit on rubber buffers and move far less (verified in game)
            float a = vibAmp;
            if (a > 0f)
            {
                RearDrum.transform.localPosition = new Vector3(0f, RollerModel.DrumR + a * Rand(), -RollerModel.WheelbaseHalf);
                FrontDrum.transform.localPosition = new Vector3(0f, RollerModel.DrumR + a * Rand(), RollerModel.WheelbaseHalf);
                Rear.transform.localPosition = new Vector3(0f, 0.2f * a * Rand(), 0f);
                Front.transform.localPosition = new Vector3(0f, 0.2f * a * Rand(), 0f);
            }
            else if (Vibrating)
            {
                RearDrum.transform.localPosition = new Vector3(0f, RollerModel.DrumR, -RollerModel.WheelbaseHalf);
                FrontDrum.transform.localPosition = new Vector3(0f, RollerModel.DrumR, RollerModel.WheelbaseHalf);
                Rear.transform.localPosition = Vector3.zero;
                Front.transform.localPosition = Vector3.zero;
            }
            Vibrating = a > 0f;
            var q = Quaternion.Euler(drumDeg, 0f, 0f);
            RearDrum.transform.localRotation = q;
            FrontDrum.transform.localRotation = q;
            DrumDeg = drumDeg;
        }

        public bool AnyRendererEnabled()
        {
            if (m_Renderers == null) return false;
            for (int i = 0; i < m_Renderers.Length; i++) if (m_Renderers[i] != null && m_Renderers[i].enabled) return true;
            return false;
        }

        public void Destroy()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root);
            Root = null;
        }
    }

    // Registry side of the roller units (the units themselves hang on their Puppets in MachineRegistry.All).
    public static class RollerWorld
    {
        // every root GameObject ever created (pruned once Unity has destroyed it): the stray sweep does not rely on scene membership
        public static readonly HashSet<GameObject> Created = new HashSet<GameObject>();
        private static readonly List<GameObject> s_Strays = new List<GameObject>(8);
        public static int NextId = 1;
        public static int Created_, Destroyed, StraysSwept, InViewSpawns;

        private static bool LooksOurs(GameObject go) =>
            go != null && go.transform.parent == null && go.name.StartsWith("RRW_Roller_", StringComparison.Ordinal) &&
            (go.hideFlags & HideFlags.DontSave) == HideFlags.DontSave;

        public static RollerUnit Create(Puppet p, int seed)
        {
            int id = NextId++;
            var u = RollerUnit.Create(id, seed);
            p.Unit = u;
            p.UnitId = id;
            Created_++;
            return u;
        }

        public static void Destroy(Puppet p)
        {
            if (p == null || p.Unit == null) return;
            try { p.Unit.Destroy(); }
            catch (Exception e) { RRWLog.ErrorOnce("machines roller destroy", e); }
            p.Unit = null;
            Destroyed++;
        }

        // Destroys every roller unit of the registry and sweeps strays (RRW_Roller_* DontSave roots): preload, menu, layer off,
        // shutdown, a faulted roller pass. Allocates (FindObjectsOfTypeAll): never per frame.
        public static void ClearAll(string why)
        {
            int n = 0;
            var all = MachineRegistry.All;
            for (int i = 0; i < all.Count; i++)
                if (all[i].IsRoller && all[i].Unit != null) { Destroy(all[i]); n++; }
            int stray = SweepStrays();
            if (n > 0 || stray > 0) RRWLog.Info("machines: cleared " + n + " roller unit(s), stray roller GameObjects " + stray + " (" + why + ")");
        }

        public static int SweepStrays()
        {
            s_Strays.Clear();
            var live = new HashSet<GameObject>();
            var all = MachineRegistry.All;
            for (int i = 0; i < all.Count; i++) if (all[i].IsRoller && all[i].Unit != null && all[i].Unit.Root != null) live.Add(all[i].Unit.Root);
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (LooksOurs(go) && !live.Contains(go)) s_Strays.Add(go);
            foreach (var go in Created)
                if (go != null && !live.Contains(go) && !s_Strays.Contains(go)) s_Strays.Add(go);
            foreach (var go in s_Strays) UnityEngine.Object.Destroy(go);
            Created.RemoveWhere(go => go == null || !live.Contains(go));
            int k = s_Strays.Count;
            s_Strays.Clear();
            StraysSwept += k;
            return k;
        }

        // Root GameObjects named like ours that are alive right now (Destroy takes effect at the end of the frame). Allocates: dev /
        // rrw.check only.
        public static int LiveRootObjects()
        {
            Created.RemoveWhere(go => go == null);
            var live = new HashSet<GameObject>(Created);
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (LooksOurs(go)) live.Add(go);
            return live.Count;
        }
    }

    // "Out of view" for rollers (they are invisible to vanilla culling): the camera frustum planes, computed once per Director
    // update, and the camera position. Renderer.isVisible is NOT used (shadow-cascade culling sets it).
    public static class RollerView
    {
        private static readonly Plane[] s_Planes = new Plane[6];
        public static bool Have;
        public static Vector3 CamPos;
        public static uint Update;

        public static Camera Cam(Unity.Entities.World world)
        {
            Camera c = null;
            try
            {
                var cus = world.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
                c = cus != null ? cus.activeCamera : null;
            }
            catch { c = null; }
            return c != null ? c : Camera.main;
        }

        public static void Refresh(Unity.Entities.World world)
        {
            if (Update == RRWClock.UpdateIndex && Have) return;
            Update = RRWClock.UpdateIndex;
            var cam = Cam(world);
            Have = cam != null;
            if (!Have) return;
            GeometryUtility.CalculateFrustumPlanes(cam, s_Planes);
            CamPos = cam.transform.position;
        }

        // A roller box at the pivot (world) is inside the frustum. Unknown camera: true (keeps rollers; never a pop on a bad frame).
        public static bool InFrustum(float3 pivot)
        {
            if (!Have) return true;
            var b = new Bounds(new Vector3(pivot.x, pivot.y + 0.5f * RRWConst.kRollerHeight, pivot.z),
                               new Vector3(2f * MxConst.kRollerViewPad, RRWConst.kRollerHeight + 1f, 2f * MxConst.kRollerViewPad));
            return GeometryUtility.TestPlanesAABB(s_Planes, b);
        }

        public static float CamDistance(float3 p) => Have ? Vector3.Distance(CamPos, new Vector3(p.x, p.y, p.z)) : 0f;
    }
}
