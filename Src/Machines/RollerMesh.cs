using System.Collections.Generic;
using UnityEngine;

// Procedural tandem roller meshes - a port of the roller prototype's mesh builder (verified in game, unchanged
// geometry: ~2 k tris LOD0, ~0.3 k LOD1). Built once per session and shared by every roller unit (RollerModel.EnsureBuilt);
// destroyed in RollerRenderSystem.OnDestroy. Meshes carry HideFlags.DontSave (never part of a save or scene).
namespace RealisticRoadWorks.V3.Machines
{
    // Material slots used by every roller mesh. A built mesh only keeps the slots it uses (see RollerMeshBuilder.Build),
    // and reports which slot each of its submeshes maps to.
    public static class RollerMatSlot
    {
        public const int Paint = 0;   // body colour (rl.colour)
        public const int Steel = 1;   // drum shells
        public const int Dark = 2;    // rubber / plastic / floor / seat / roof
        public const int Beacon = 3;  // amber rotating beacon lens (per roller, flashing)
        public const int Lamp = 4;    // white work / head lights
        public const int Tail = 5;    // red tail lights
        public const int Count = 6;
    }

    public sealed class RollerBuiltMesh
    {
        public Mesh Mesh;
        public int[] Slots;      // submesh index -> RollerMatSlot
        public int Triangles;
    }

    // Small mesh builder: flat-shaded boxes and prisms, smooth-sided cylinders. Triangle winding is chosen
    // automatically from the intended outward normal, so no primitive can end up inside out.
    public sealed class RollerMeshBuilder
    {
        private readonly List<Vector3> m_V = new List<Vector3>();
        private readonly List<Vector3> m_N = new List<Vector3>();
        private readonly List<Vector2> m_UV = new List<Vector2>();
        private readonly List<int>[] m_T = new List<int>[RollerMatSlot.Count];
        private Matrix4x4 m_M = Matrix4x4.identity;
        private readonly Stack<Matrix4x4> m_Stack = new Stack<Matrix4x4>();

        public RollerMeshBuilder()
        {
            for (int i = 0; i < m_T.Length; i++) m_T[i] = new List<int>();
        }

        // Rigid transforms only (rotation + translation): normals are transformed with MultiplyVector.
        public void Push(Vector3 pos, Quaternion rot)
        {
            m_Stack.Push(m_M);
            m_M = m_M * Matrix4x4.TRS(pos, rot, Vector3.one);
        }

        public void Pop() => m_M = m_Stack.Pop();

        private int V(Vector3 p, Vector3 n, Vector2 uv)
        {
            m_V.Add(m_M.MultiplyPoint3x4(p));
            m_N.Add(m_M.MultiplyVector(n).normalized);
            m_UV.Add(uv);
            return m_V.Count - 1;
        }

        // Unity front faces: cross(b-a, c-a) points to the viewer. Flip when it disagrees with the wanted normal.
        private void Tri(int slot, int a, int b, int c, Vector3 wantedWorldNormal)
        {
            Vector3 cr = Vector3.Cross(m_V[b] - m_V[a], m_V[c] - m_V[a]);
            var t = m_T[slot];
            if (Vector3.Dot(cr, wantedWorldNormal) >= 0f) { t.Add(a); t.Add(b); t.Add(c); }
            else { t.Add(a); t.Add(c); t.Add(b); }
        }

        // Planar quad (p0..p3 in order around the edge) with one flat normal. UVs in metres along the quad's edges.
        public void Quad(int slot, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n)
        {
            Vector3 e1 = (p1 - p0).normalized;
            Vector3 e2 = Vector3.Cross(n, e1).normalized;
            int a = V(p0, n, new Vector2(0f, 0f));
            int b = V(p1, n, new Vector2(Vector3.Dot(p1 - p0, e1), Vector3.Dot(p1 - p0, e2)));
            int c = V(p2, n, new Vector2(Vector3.Dot(p2 - p0, e1), Vector3.Dot(p2 - p0, e2)));
            int d = V(p3, n, new Vector2(Vector3.Dot(p3 - p0, e1), Vector3.Dot(p3 - p0, e2)));
            Vector3 wn = m_M.MultiplyVector(n);
            Tri(slot, a, b, c, wn);
            Tri(slot, a, c, d, wn);
        }

        public void Box(int slot, Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Vector3 c = center;
            // +X / -X
            Quad(slot, c + new Vector3(h.x, -h.y, -h.z), c + new Vector3(h.x, -h.y, h.z), c + new Vector3(h.x, h.y, h.z), c + new Vector3(h.x, h.y, -h.z), Vector3.right);
            Quad(slot, c + new Vector3(-h.x, -h.y, h.z), c + new Vector3(-h.x, -h.y, -h.z), c + new Vector3(-h.x, h.y, -h.z), c + new Vector3(-h.x, h.y, h.z), Vector3.left);
            // +Y / -Y
            Quad(slot, c + new Vector3(-h.x, h.y, -h.z), c + new Vector3(h.x, h.y, -h.z), c + new Vector3(h.x, h.y, h.z), c + new Vector3(-h.x, h.y, h.z), Vector3.up);
            Quad(slot, c + new Vector3(-h.x, -h.y, h.z), c + new Vector3(h.x, -h.y, h.z), c + new Vector3(h.x, -h.y, -h.z), c + new Vector3(-h.x, -h.y, -h.z), Vector3.down);
            // +Z / -Z
            Quad(slot, c + new Vector3(h.x, -h.y, h.z), c + new Vector3(-h.x, -h.y, h.z), c + new Vector3(-h.x, h.y, h.z), c + new Vector3(h.x, h.y, h.z), Vector3.forward);
            Quad(slot, c + new Vector3(-h.x, -h.y, -h.z), c + new Vector3(h.x, -h.y, -h.z), c + new Vector3(h.x, h.y, -h.z), c + new Vector3(-h.x, h.y, -h.z), Vector3.back);
        }

        // Box rotated about its own centre.
        public void Box(int slot, Vector3 center, Vector3 size, Quaternion rot)
        {
            Push(center, rot);
            Box(slot, Vector3.zero, size);
            Pop();
        }

        // Convex profile in the (z, y) plane extruded across x0..x1 (flat-shaded prism: hoods, tanks, yoke plates).
        public void ExtrudeX(int slot, Vector2[] zy, float x0, float x1)
        {
            int n = zy.Length;
            Vector2 cen = Vector2.zero;
            for (int i = 0; i < n; i++) cen += zy[i];
            cen /= n;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = zy[i], b = zy[(i + 1) % n];
                Vector2 mid = (a + b) * 0.5f;
                Vector2 d = b - a;
                Vector2 nn = new Vector2(d.y, -d.x).normalized;           // perpendicular in (z,y)
                if (Vector2.Dot(nn, mid - cen) < 0f) nn = -nn;              // point away from the centroid
                Vector3 normal = new Vector3(0f, nn.y, nn.x);
                Quad(slot, new Vector3(x0, a.y, a.x), new Vector3(x1, a.y, a.x), new Vector3(x1, b.y, b.x), new Vector3(x0, b.y, b.x), normal);
            }
            Cap(slot, zy, cen, x0, Vector3.left);
            Cap(slot, zy, cen, x1, Vector3.right);
        }

        private void Cap(int slot, Vector2[] zy, Vector2 cen, float x, Vector3 n)
        {
            int c = V(new Vector3(x, cen.y, cen.x), n, new Vector2(cen.x, cen.y));
            int first = m_V.Count;
            for (int i = 0; i < zy.Length; i++) V(new Vector3(x, zy[i].y, zy[i].x), n, new Vector2(zy[i].x, zy[i].y));
            Vector3 wn = m_M.MultiplyVector(n);
            for (int i = 0; i < zy.Length; i++) Tri(slot, c, first + i, first + (i + 1) % zy.Length, wn);
        }

        // Cylinder from a to b (any axis). Smooth sides, flat caps. capSlot < 0 = same slot as the side.
        public void Cylinder(int slot, Vector3 a, Vector3 b, float r, int seg, bool capA, bool capB, int capSlot = -1)
        {
            if (capSlot < 0) capSlot = slot;
            Vector3 axis = b - a;
            float len = axis.magnitude;
            if (len < 1e-5f) return;
            Vector3 ax = axis / len;
            Vector3 u = Vector3.Cross(ax, Mathf.Abs(ax.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(ax, u).normalized;
            float circ = 2f * Mathf.PI * r;
            int baseIdx = m_V.Count;
            for (int i = 0; i <= seg; i++)
            {
                float ang = 2f * Mathf.PI * i / seg;
                Vector3 n = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                float uu = circ * i / seg;
                V(a + n * r, n, new Vector2(uu, 0f));
                V(b + n * r, n, new Vector2(uu, len));
            }
            for (int i = 0; i < seg; i++)
            {
                int a0 = baseIdx + i * 2, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
                float angMid = 2f * Mathf.PI * (i + 0.5f) / seg;
                Vector3 nMid = m_M.MultiplyVector(u * Mathf.Cos(angMid) + w * Mathf.Sin(angMid));
                Tri(slot, a0, a1, b1, nMid);
                Tri(slot, a0, b1, b0, nMid);
            }
            if (capA) Disc(capSlot, a, -ax, u, w, r, seg);
            if (capB) Disc(capSlot, b, ax, u, w, r, seg);
        }

        private void Disc(int slot, Vector3 c, Vector3 n, Vector3 u, Vector3 w, float r, int seg)
        {
            int ci = V(c, n, new Vector2(0.5f, 0.5f));
            int first = m_V.Count;
            for (int i = 0; i < seg; i++)
            {
                float ang = 2f * Mathf.PI * i / seg;
                float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
                V(c + (u * cs + w * sn) * r, n, new Vector2(0.5f + 0.5f * cs, 0.5f + 0.5f * sn));
            }
            Vector3 wn = m_M.MultiplyVector(n);
            for (int i = 0; i < seg; i++) Tri(slot, ci, first + i, first + (i + 1) % seg, wn);
        }

        // Only the slots that received triangles become submeshes. Slots[i] tells which material submesh i takes.
        public RollerBuiltMesh Build(string name)
        {
            var used = new List<int>();
            for (int s = 0; s < m_T.Length; s++) if (m_T[s].Count > 0) used.Add(s);
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(m_V);
            mesh.SetNormals(m_N);
            mesh.SetUVs(0, m_UV);
            mesh.subMeshCount = used.Count;
            int tris = 0;
            for (int i = 0; i < used.Count; i++)
            {
                mesh.SetTriangles(m_T[used[i]], i, false);
                tris += m_T[used[i]].Count / 3;
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            mesh.UploadMeshData(false);
            return new RollerBuiltMesh { Mesh = mesh, Slots = used.ToArray(), Triangles = tris };
        }
    }

    // The tandem roller model (HAMM HD+ 110 / BOMAG BW 154 class, ~10 t): wheelbase 3.30 m, drums 1.20 m x 1.68 m,
    // length 4.72 m, width 1.83 m (hub covers 1.90 m), height 2.93 m at the ROPS roof, 3.08 m at the beacon.
    // Local frame: +Z forward, +Y up, origin = articulation joint on the ground. Drums have their own pivot at the axle.
    public static class RollerModel
    {
        public const float WheelbaseHalf = 1.65f;
        public const float DrumR = 0.60f;
        public const float DrumHalfW = 0.84f;
        public const float PlateIn = 0.865f, PlateOut = 0.915f;
        public const float ChassisLo = 1.27f, ChassisHi = 1.50f;
        // Joint clearance for +/-38 deg articulation (RollerUnit.MaxArticulationDeg). A rear-frame point (x, z) lands at
        // front-frame z = |x|*sin(a) + z*cos(a); at 38 deg the front ROPS posts (0.84, 0.18, r 0.045) reach z 0.70 and
        // the platform corner (0.92, 0.18) z 0.71, so the tank starts at 0.75. The step hand-bars (0.915, -0.025)
        // reach 0.55 at chassis height, so the front chassis starts at 0.60. Real tandem rollers keep the same gap.
        public const float TankZ0 = 0.75f, FrontChassisZ0 = 0.60f;

        // LOD 0/1 meshes: rear frame (incl. platform + ROPS), front frame, drum. Built once, shared by every roller.
        public static RollerBuiltMesh[] Rear = new RollerBuiltMesh[2], Front = new RollerBuiltMesh[2], Drum = new RollerBuiltMesh[2];

        public static bool Built => Rear[0] != null && Rear[0].Mesh != null;

        public static void EnsureBuilt()
        {
            if (Built) return;
            Rear[0] = BuildRear0(); Rear[1] = BuildRear1();
            Front[0] = BuildFront0(); Front[1] = BuildFront1();
            Drum[0] = BuildDrum(32, true); Drum[1] = BuildDrum(10, false);
        }

        public static int Triangles(int lod) => Rear[lod].Triangles + Front[lod].Triangles + 2 * Drum[lod].Triangles;

        public static void Destroy()
        {
            foreach (var arr in new[] { Rear, Front, Drum })
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] != null && arr[i].Mesh != null) Object.Destroy(arr[i].Mesh);
                    arr[i] = null;
                }
        }

        // Yoke side plate: tapered arm from just above the axle up to the chassis, both sides.
        private static void Yokes(RollerMeshBuilder b, float zc)
        {
            var prof = new[]
            {
                new Vector2(zc - 0.22f, 0.40f), new Vector2(zc + 0.22f, 0.40f),
                new Vector2(zc + 0.52f, ChassisLo + 0.02f), new Vector2(zc - 0.52f, ChassisLo + 0.02f),
            };
            b.ExtrudeX(RollerMatSlot.Paint, prof, PlateIn, PlateOut);
            b.ExtrudeX(RollerMatSlot.Paint, prof, -PlateOut, -PlateIn);
            // Drive / bearing hubs on the plates' outer faces.
            b.Cylinder(RollerMatSlot.Dark, new Vector3(PlateOut - 0.005f, DrumR, zc), new Vector3(0.95f, DrumR, zc), 0.17f, 16, false, true);
            b.Cylinder(RollerMatSlot.Dark, new Vector3(-PlateOut + 0.005f, DrumR, zc), new Vector3(-0.95f, DrumR, zc), 0.17f, 16, false, true);
        }

        // Scraper bar + water spray bar on the outer side of a drum (sign = -1 rear, +1 front).
        private static void DrumBars(RollerMeshBuilder b, float zc, float sign)
        {
            float zs = zc + sign * (DrumR + 0.035f);
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 0.62f, zs), new Vector3(1.64f, 0.10f, 0.03f));
            b.Box(RollerMatSlot.Dark, new Vector3(0.78f, 0.95f, zs), new Vector3(0.04f, 0.70f, 0.03f));   // scraper hangers
            b.Box(RollerMatSlot.Dark, new Vector3(-0.78f, 0.95f, zs), new Vector3(0.04f, 0.70f, 0.03f));
            float zp = zc + sign * (DrumR + 0.03f);
            b.Cylinder(RollerMatSlot.Dark, new Vector3(-0.80f, 0.98f, zp), new Vector3(0.80f, 0.98f, zp), 0.025f, 6, true, true);   // below the bumper
        }

        private static RollerBuiltMesh BuildRear0()
        {
            var b = new RollerMeshBuilder();
            float zc = -WheelbaseHalf;
            // Chassis over the rear drum, yokes, bars.
            b.Box(RollerMatSlot.Paint, new Vector3(0f, (ChassisLo + ChassisHi) * 0.5f, -1.25f), new Vector3(1.80f, ChassisHi - ChassisLo, 1.90f));
            Yokes(b, zc);
            DrumBars(b, zc, -1f);
            // Engine hood with chamfered top edges.
            b.ExtrudeX(RollerMatSlot.Paint, new[]
            {
                new Vector2(-2.25f, ChassisHi), new Vector2(-0.80f, ChassisHi), new Vector2(-0.80f, 1.95f),
                new Vector2(-0.96f, 2.12f), new Vector2(-2.05f, 2.12f), new Vector2(-2.25f, 1.90f),
            }, -0.84f, 0.84f);
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.72f, -2.262f), new Vector3(1.20f, 0.30f, 0.03f));             // rear grille
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 2.128f, -1.50f), new Vector3(0.90f, 0.02f, 0.60f));             // hood air intake
            // Rear bumper / counterweight + tail lights.
            b.Box(RollerMatSlot.Paint, new Vector3(0f, 1.25f, -2.275f), new Vector3(1.76f, 0.40f, 0.17f));
            b.Box(RollerMatSlot.Tail, new Vector3(0.70f, 1.35f, -2.365f), new Vector3(0.14f, 0.08f, 0.02f));
            b.Box(RollerMatSlot.Tail, new Vector3(-0.70f, 1.35f, -2.365f), new Vector3(0.14f, 0.08f, 0.02f));
            // Exhaust stack.
            b.Cylinder(RollerMatSlot.Dark, new Vector3(0.55f, 2.10f, -1.70f), new Vector3(0.55f, 2.42f, -1.70f), 0.045f, 8, false, true);
            // Articulation joint housing + access steps hanging in the joint gap.
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.15f, -0.12f), new Vector3(0.60f, 0.60f, 0.50f));
            for (int s = -1; s <= 1; s += 2)
            {
                b.Box(RollerMatSlot.Dark, new Vector3(s * 0.70f, 1.15f, -0.05f), new Vector3(0.36f, 0.04f, 0.22f));
                b.Box(RollerMatSlot.Dark, new Vector3(s * 0.70f, 0.80f, -0.05f), new Vector3(0.36f, 0.04f, 0.22f));
                b.Box(RollerMatSlot.Dark, new Vector3(s * 0.90f, 1.15f, -0.05f), new Vector3(0.03f, 0.75f, 0.05f));
            }
            // Operator platform floor, seat, dashboard, steering.
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.53f, -0.295f), new Vector3(1.84f, 0.05f, 0.95f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.68f, -0.42f), new Vector3(0.46f, 0.26f, 0.40f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.86f, -0.44f), new Vector3(0.54f, 0.10f, 0.48f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 2.17f, -0.70f), new Vector3(0.52f, 0.55f, 0.10f), Quaternion.Euler(-10f, 0f, 0f));
            b.Box(RollerMatSlot.Dark, new Vector3(0.30f, 2.00f, -0.45f), new Vector3(0.06f, 0.06f, 0.36f));            // armrests
            b.Box(RollerMatSlot.Dark, new Vector3(-0.30f, 2.00f, -0.45f), new Vector3(0.06f, 0.06f, 0.36f));
            b.Box(RollerMatSlot.Paint, new Vector3(0f, 1.73f, 0.14f), new Vector3(0.70f, 0.40f, 0.18f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.94f, 0.14f), new Vector3(0.72f, 0.03f, 0.20f));                // dash top
            Vector3 colA = new Vector3(0f, 1.95f, 0.12f), colB = new Vector3(0f, 2.18f, -0.02f);
            b.Cylinder(RollerMatSlot.Dark, colA, colB, 0.04f, 8, false, false);
            Vector3 colDir = (colB - colA).normalized;
            b.Cylinder(RollerMatSlot.Dark, colB, colB + colDir * 0.035f, 0.19f, 16, true, true);                       // steering wheel
            // ROPS: 4 posts + roof, work lights, beacon.
            float[] pz = { -0.74f, 0.18f };
            foreach (float z in pz)
                for (int s = -1; s <= 1; s += 2)
                    b.Cylinder(RollerMatSlot.Paint, new Vector3(s * 0.84f, ChassisHi + 0.02f, z), new Vector3(s * 0.84f, 2.87f, z), 0.045f, 8, false, false);
            b.Box(RollerMatSlot.Paint, new Vector3(0.84f, 2.82f, -0.28f), new Vector3(0.08f, 0.08f, 1.02f));           // ROPS side rails
            b.Box(RollerMatSlot.Paint, new Vector3(-0.84f, 2.82f, -0.28f), new Vector3(0.08f, 0.08f, 1.02f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 2.89f, -0.28f), new Vector3(1.86f, 0.07f, 1.30f));
            for (int s = -1; s <= 1; s += 2)
            {
                b.Box(RollerMatSlot.Dark, new Vector3(s * 0.60f, 2.80f, 0.33f), new Vector3(0.16f, 0.10f, 0.08f));
                b.Box(RollerMatSlot.Lamp, new Vector3(s * 0.60f, 2.80f, 0.375f), new Vector3(0.13f, 0.07f, 0.012f));
                b.Box(RollerMatSlot.Dark, new Vector3(s * 0.60f, 2.80f, -0.89f), new Vector3(0.16f, 0.10f, 0.08f));
                b.Box(RollerMatSlot.Lamp, new Vector3(s * 0.60f, 2.80f, -0.935f), new Vector3(0.13f, 0.07f, 0.012f));
            }
            b.Cylinder(RollerMatSlot.Dark, new Vector3(0.60f, 2.92f, -0.70f), new Vector3(0.60f, 2.96f, -0.70f), 0.08f, 12, false, true);
            b.Cylinder(RollerMatSlot.Beacon, new Vector3(0.60f, 2.96f, -0.70f), new Vector3(0.60f, 3.08f, -0.70f), 0.065f, 12, false, true);
            return b.Build("RRW_Roller_Rear_LOD0");
        }

        private static RollerBuiltMesh BuildRear1()
        {
            var b = new RollerMeshBuilder();
            b.Box(RollerMatSlot.Paint, new Vector3(0f, (ChassisLo + ChassisHi) * 0.5f, -1.25f), new Vector3(1.80f, ChassisHi - ChassisLo, 1.90f));
            b.Box(RollerMatSlot.Paint, new Vector3(0f, 1.81f, -1.52f), new Vector3(1.68f, 0.62f, 1.45f));
            b.Box(RollerMatSlot.Paint, new Vector3(0.89f, 0.85f, -WheelbaseHalf), new Vector3(0.05f, 0.85f, 0.70f));
            b.Box(RollerMatSlot.Paint, new Vector3(-0.89f, 0.85f, -WheelbaseHalf), new Vector3(0.05f, 0.85f, 0.70f));
            b.Box(RollerMatSlot.Paint, new Vector3(0f, 1.25f, -2.275f), new Vector3(1.76f, 0.40f, 0.17f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.53f, -0.295f), new Vector3(1.84f, 0.05f, 0.95f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.85f, -0.45f), new Vector3(0.54f, 0.65f, 0.48f));
            foreach (float z in new[] { -0.74f, 0.18f })
                for (int s = -1; s <= 1; s += 2)
                    b.Box(RollerMatSlot.Paint, new Vector3(s * 0.84f, 2.19f, z), new Vector3(0.08f, 1.35f, 0.08f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 2.89f, -0.28f), new Vector3(1.86f, 0.07f, 1.30f));
            b.Box(RollerMatSlot.Beacon, new Vector3(0.60f, 3.0f, -0.70f), new Vector3(0.13f, 0.16f, 0.13f));
            return b.Build("RRW_Roller_Rear_LOD1");
        }

        private static RollerBuiltMesh BuildFront0()
        {
            var b = new RollerMeshBuilder();
            float zc = WheelbaseHalf;
            b.Box(RollerMatSlot.Paint, new Vector3(0f, (ChassisLo + ChassisHi) * 0.5f, FrontChassisZ0 + (2.17f - FrontChassisZ0) * 0.5f), new Vector3(1.80f, ChassisHi - ChassisLo, 2.17f - FrontChassisZ0));
            Yokes(b, zc);
            DrumBars(b, zc, 1f);
            // Water tank with a sloped front so the operator sees the drum edge.
            b.ExtrudeX(RollerMatSlot.Paint, new[]
            {
                new Vector2(TankZ0, ChassisHi), new Vector2(2.22f, ChassisHi), new Vector2(2.22f, 1.80f),
                new Vector2(2.05f, 1.98f), new Vector2(TankZ0 + 0.22f, 1.98f), new Vector2(TankZ0, 1.85f),
            }, -0.84f, 0.84f);
            b.Cylinder(RollerMatSlot.Dark, new Vector3(-0.45f, 1.98f, 1.30f), new Vector3(-0.45f, 2.03f, 1.30f), 0.12f, 12, false, true); // filler cap
            // Front bumper + head lights, knuckle.
            b.Box(RollerMatSlot.Paint, new Vector3(0f, 1.25f, 2.275f), new Vector3(1.76f, 0.40f, 0.17f));
            b.Box(RollerMatSlot.Lamp, new Vector3(0.65f, 1.36f, 2.365f), new Vector3(0.14f, 0.08f, 0.02f));
            b.Box(RollerMatSlot.Lamp, new Vector3(-0.65f, 1.36f, 2.365f), new Vector3(0.14f, 0.08f, 0.02f));
            b.Box(RollerMatSlot.Dark, new Vector3(0f, 1.12f, (0.20f + FrontChassisZ0 + 0.02f) * 0.5f), new Vector3(0.50f, 0.46f, FrontChassisZ0 + 0.02f - 0.20f));   // knuckle up to the chassis
            return b.Build("RRW_Roller_Front_LOD0");
        }

        private static RollerBuiltMesh BuildFront1()
        {
            var b = new RollerMeshBuilder();
            b.Box(RollerMatSlot.Paint, new Vector3(0f, (ChassisLo + ChassisHi) * 0.5f, FrontChassisZ0 + (2.17f - FrontChassisZ0) * 0.5f), new Vector3(1.80f, ChassisHi - ChassisLo, 2.17f - FrontChassisZ0));
            b.Box(RollerMatSlot.Paint, new Vector3(0f, 1.74f, (TankZ0 + 2.22f) * 0.5f), new Vector3(1.68f, 0.48f, 2.22f - TankZ0));
            b.Box(RollerMatSlot.Paint, new Vector3(0.89f, 0.85f, WheelbaseHalf), new Vector3(0.05f, 0.85f, 0.70f));
            b.Box(RollerMatSlot.Paint, new Vector3(-0.89f, 0.85f, WheelbaseHalf), new Vector3(0.05f, 0.85f, 0.70f));
            b.Box(RollerMatSlot.Paint, new Vector3(0f, 1.25f, 2.275f), new Vector3(1.76f, 0.40f, 0.17f));
            return b.Build("RRW_Roller_Front_LOD1");
        }

        // Drum: steel shell with end plates, hub and bolt circle on each end (the bolts make the rotation visible).
        private static RollerBuiltMesh BuildDrum(int seg, bool detail)
        {
            var b = new RollerMeshBuilder();
            b.Cylinder(RollerMatSlot.Steel, new Vector3(-DrumHalfW, 0f, 0f), new Vector3(DrumHalfW, 0f, 0f), DrumR, seg, true, true);
            if (detail)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    float x0 = s * DrumHalfW, xh = s * (DrumHalfW + 0.018f), xb = s * (DrumHalfW + 0.012f);
                    b.Cylinder(RollerMatSlot.Dark, new Vector3(x0 - s * 0.002f, 0f, 0f), new Vector3(xh, 0f, 0f), 0.24f, 16, false, true);
                    for (int k = 0; k < 6; k++)
                    {
                        float ang = k * Mathf.PI / 3f;
                        var c = new Vector3(0f, Mathf.Cos(ang) * 0.42f, Mathf.Sin(ang) * 0.42f);
                        b.Cylinder(RollerMatSlot.Dark, new Vector3(x0 - s * 0.002f, c.y, c.z), new Vector3(xb, c.y, c.z), 0.045f, 6, false, true);
                    }
                    // One wide paint stripe segment on the end plate: a clear rotation marker even from far.
                    // 8 mm proud, so its face never coincides with the bolt heads (12 mm) or the drum end plate.
                    b.Box(RollerMatSlot.Paint, new Vector3(s * (DrumHalfW + 0.004f), 0.40f, 0f), new Vector3(0.008f, 0.30f, 0.08f));
                }
            }
            return b.Build(detail ? "RRW_Roller_Drum_LOD0" : "RRW_Roller_Drum_LOD1");
        }
    }
}
