using System;
using System.Collections.Generic;
using System.Text;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

// The planar 2-link + bucket IK of the "RRW Road Excavator" rig - a port of the dig animation prototype's IK
// (verified: reachedErr 0.000, limits 0, unreachable 0). Joint senses are measured from the rig (flipped bone axes
// are handled); a Gauss-Newton refinement through the full rig FK runs only above a 5 mm residual; the floor guard raises the target
// so the tip never ends below floorH. Main thread only (managed arrays): MachineBoneSystem's IK pass and the Director's cycle builds.
// Bone angles (degrees) are written as rest * axis(angle): Body about local Z (slew), Arm01 / Arm02 / Arm02Bucket about local X -
// the same convention as the fixed keyframes (MachinePoses), so both can be blended.
namespace RealisticRoadWorks.V3.Machines
{
    public struct DigPose
    {
        public float Slew, Boom, Stick, Bucket;
        public float4 F4 => new float4(Slew, Boom, Stick, Bucket);
        public DigPose(float4 v) { Slew = v.x; Boom = v.y; Stick = v.z; Bucket = v.w; }
        public override string ToString() => "slew=" + RRWLog.F(Slew) + " boom=" + RRWLog.F(Boom) + " stick=" + RRWLog.F(Stick) + " bucket=" + RRWLog.F(Bucket);
    }

    public struct DigIkResult
    {
        public DigPose Pose;
        public float3 TargetObj, TipObj;      // object space (machine: +x right, +y up, +z forward, origin on the ground)
        public float Err;                     // |FK tip - target| (m), full 3-D forward kinematics of the rig
        public bool Unreachable, FloorRaised, Refined, TooClose;
        public byte Limits;                   // 1 boom, 2 stick, 4 bucket, 8 slew
        public float BoomElev, StickRel, BucketRel;   // physical planar angles (deg) after limits
        public float2 PlanarTip;              // (r, h) of the planar model tip
        public float SlewDeg, R, H;           // arm-space target actually solved (after the floor guard)

        public string LimitsText() => Limits == 0 ? "none" :
            ((Limits & 1) != 0 ? "boom," : "") + ((Limits & 2) != 0 ? "stick," : "") + ((Limits & 4) != 0 ? "bucket," : "") + ((Limits & 8) != 0 ? "slew," : "");
    }

    // Joint limits in PHYSICAL planar terms (degrees), independent of the rig's rest pose:
    //  boomElev  = elevation of boom pivot -> stick pivot above horizontal;
    //  stickRel  = stick direction (stick pivot -> bucket pivot) relative to the boom (negative = folded down/in);
    //  bucketRel = bucket direction (bucket pivot -> tip) relative to the stick (negative = curled, positive = opened);
    //  slew      = |slew| about the swing axis.
    // Defaults are those of a 20 t class road excavator; dg.limits changes them live.
    public static class DigLimits
    {
        public static float BoomMin = -50f, BoomMax = 65f;
        public static float StickMin = -165f, StickMax = -25f;
        public static float BucketMin = -175f, BucketMax = 60f;
        public static float SlewMax = 180f;
        public static bool Refine = true;
        // Hydraulic stroke check (passes in the cycle simulation): each piston pair's anchor distance / rest distance stays in [ExtMin, ExtMax].
        // The defaults are the loose envelope of a single-stage cylinder (closed:open ~1:1.75) around an unknown rest point,
        // NOT the mesh's visible rod length: tune them in game (dg.limits ext <min> <max>). StrokeMax only adds an
        // informational flag when a pair's longest / shortest length over the cycle exceeds it (dg.limits stroke <r>).
        public static float ExtMin = 0.60f, ExtMax = 1.75f, StrokeMax = 1.75f;

        public static string Describe() =>
            "boom=[" + RRWLog.F(BoomMin) + "," + RRWLog.F(BoomMax) + "] stick=[" + RRWLog.F(StickMin) + "," + RRWLog.F(StickMax) + "] bucket=[" + RRWLog.F(BucketMin) + "," +
            RRWLog.F(BucketMax) + "] slew=+-" + RRWLog.F(SlewMax) + " refine=" + (Refine ? 1 : 0) + " ext=[" + RRWLog.F(ExtMin) + "," + RRWLog.F(ExtMax) + "] stroke<=" + RRWLog.F(StrokeMax);
    }

    // IK model of one excavator prefab at one root scale, built from the rig's rest data (ProceduralBone): the same matrix
    // composition the bone job and ProceduralSkeletonSystem use (object = parent * TRS(pos, rest * axis(angle), scale)).
    //
    // Geometry (all object space, slew 0 = rest):
    //   C  = Body origin (swing pivot), S = swing axis (flipped to point up);
    //   A0 = boom joint axis (projected perpendicular to S), F0 = the arm plane's outward direction (A0 x S, towards the bucket);
    //   E  = lateral offset of the arm plane from the swing axis (along A0);
    //   planar coordinates (r, h) = (dot(p - C, F0), dot(p, S)); P1 boom pivot, P2 stick pivot, P3 bucket pivot, P4 bucket tip.
    // The three arm joints rotate about axes parallel to A0; Sig1..3 = measured planar degrees per bone degree (sign and
    // planarity check, ~+-1), Sig0 the same for the swing.
    public sealed class DigArm
    {
        public RigSub Rs;
        public int IBody, IBoom, IStick, IBkt, IBucket;
        public float Scale;
        private int n;
        private float3[] m_Pos, m_Scl;
        private quaternion[] m_Rest;
        private int[] m_Par;
        private float4x4[] m_Tmp, m_Tmp2, m_Tmp3, m_Tmp4;
        private int[] m_Chain;   // perf: the bones the tip / bucket points depend on (ancestors of Arm02Bucket and Bucket), ascending
        // hydraulic pairs (by piston bone name) and their rest anchor distances (rig units incl. root scale; only ratios are used)
        public List<int2> Pairs = new List<int2>();
        public List<string> PairNames = new List<string>();
        public float[] PairRest = new float[0];

        public float3 C, S, F0, A0, SxF0;
        public float E, K;
        public float Sig0, Sig1, Sig2, Sig3;
        public float2 P1, P2, P3, P4;
        public float L0, L1, L2, Lb;               // L0 = |swing pivot -> boom pivot| (3-D)
        public float A1, A2, A3;                   // rest planar absolute angles (deg) of boom, stick, bucket vectors
        public float3 TipLocal;                    // bucket tip in the Arm02Bucket bone frame (homogeneous point)
        public float3 TipObjRest;
        public string TipSource = "default";
        public float FrontClear;                   // planar r of the chassis front (tyres) at slew 0
        public float SlewAxisDotUp, ArmAxisDotSlew, StickAxisDot, BucketAxisDot;

        public static DigArm Build(RigSub rs, float scale, out string why)
        {
            why = null;
            var a = new DigArm { Rs = rs, Scale = scale };
            a.IBody = rs.Find("Body");
            a.IBoom = rs.Find("Arm01");
            a.IStick = rs.Find("Arm02");
            a.IBkt = rs.Find("Arm02Bucket");
            a.IBucket = rs.Find("Bucket");
            if (a.IBody < 0 || a.IBoom < 0 || a.IStick < 0 || a.IBkt < 0) { why = "missing Body/Arm01/Arm02/Arm02Bucket"; return null; }
            if (!rs.IsDescendant(a.IBoom, a.IBody) || !rs.IsDescendant(a.IStick, a.IBoom) || !rs.IsDescendant(a.IBkt, a.IStick))
            { why = "chain Body>Arm01>Arm02>Arm02Bucket broken"; return null; }
            a.n = rs.Names.Length;
            a.m_Pos = (float3[])rs.Pos.Clone();
            a.m_Rest = (quaternion[])rs.Rest.Clone();
            a.m_Par = (int[])rs.Parent.Clone();
            a.m_Scl = new float3[a.n];
            for (int i = 0; i < a.n; i++) a.m_Scl[i] = rs.Parent[i] < 0 ? rs.RestScale[i] * scale : rs.RawScale[i];
            a.m_Tmp = new float4x4[a.n];
            a.m_Tmp2 = new float4x4[a.n];
            a.m_Tmp3 = new float4x4[a.n];
            a.m_Tmp4 = new float4x4[a.n];
            a.m_Chain = a.ChainOf(a.IBkt, a.IBucket);
            try { a.Geometry(); }
            catch (Exception e) { why = "geometry: " + e.Message; return null; }
            if (!(a.L1 > 0.05f) || !(a.L2 > 0.05f)) { why = "degenerate links L1=" + a.L1 + " L2=" + a.L2; return null; }
            try
            {
                a.Pairs = PairsOf(rs, a.PairNames);
                var m0 = a.Fk(float4.zero, a.m_Tmp3);
                a.PairRest = new float[a.Pairs.Count];
                for (int k = 0; k < a.Pairs.Count; k++) a.PairRest[k] = math.distance(m0[a.Pairs[k].x].c3.xyz, m0[a.Pairs[k].y].c3.xyz);
            }
            catch (Exception) { a.Pairs = new List<int2>(); a.PairNames = new List<string>(); a.PairRest = new float[0]; }
            return a;
        }

        // Piston-pair extension (anchor distance / rest distance) for a pose, from the model FK. The anchors' positions depend
        // only on their parents, so the piston bones' own (aimed) rotations do not matter. Returns the pair count.
        public int PistonExt(float4 deg, float[] ext)
        {
            if (Pairs.Count == 0 || ext == null) return 0;
            var m = Fk(deg, m_Tmp3);
            int c = math.min(Pairs.Count, ext.Length);
            for (int k = 0; k < c; k++)
                ext[k] = PairRest[k] > 1e-5f ? math.distance(m[Pairs[k].x].c3.xyz, m[Pairs[k].y].c3.xyz) / PairRest[k] : 1f;
            return c;
        }

        // Bucket sample points in object space for clearance checks: tip, pin (Arm02Bucket), pin-tip midpoint, Bucket bone (bowl).
        public int BucketPoints(float4 deg, float3[] pts)
        {
            var m = FkChain(deg, m_Tmp4);
            float3 tip = TipOf(m), pin = Origin(m, IBkt);
            pts[0] = tip;
            pts[1] = pin;
            pts[2] = 0.5f * (tip + pin);
            if (IBucket >= 0 && pts.Length > 3) { pts[3] = Origin(m, IBucket); return 4; }
            return 3;
        }

        // ------------------------------------------------------------ forward kinematics

        public float4x4[] Fk(float4 deg, float4x4[] mats)
        {
            if (mats == null || mats.Length != n) mats = new float4x4[n];
            for (int i = 0; i < n; i++)
            {
                quaternion r = m_Rest[i];
                if (i == IBody) r = math.mul(r, quaternion.RotateZ(math.radians(deg.x)));
                else if (i == IBoom) r = math.mul(r, quaternion.RotateX(math.radians(deg.y)));
                else if (i == IStick) r = math.mul(r, quaternion.RotateX(math.radians(deg.z)));
                else if (i == IBkt) r = math.mul(r, quaternion.RotateX(math.radians(deg.w)));
                var local = float4x4.TRS(m_Pos[i], r, m_Scl[i]);
                int par = m_Par[i];
                mats[i] = par >= 0 && par < i ? math.mul(mats[par], local) : local;
            }
            return mats;
        }

        public float3 ToObj(float3 rigPoint) => Rs.SubPos + math.rotate(Rs.SubRot, rigPoint);
        public float3 DirToObj(float3 v) => math.rotate(Rs.SubRot, v);
        public float3 RigFromObj(float3 p) => math.rotate(math.inverse(Rs.SubRot), p - Rs.SubPos);
        public float3 Origin(float4x4[] m, int i) => ToObj(m[i].c3.xyz);
        public float3 TipOf(float4x4[] m) => ToObj(math.transform(m[IBkt], TipLocal));
        public float3 FkTip(float4 deg) => TipOf(FkChain(deg, m_Tmp4));

        // Perf: bones whose object matrix the tip / bucket points need - the two bones and all their ancestors, ascending
        // (parents before children, as Fk assumes). The full rig has 16 bones; the chain ~6.
        int[] ChainOf(int a, int b)
        {
            var set = new SortedSet<int>();
            foreach (int start in new[] { a, b })
            {
                int i = start, guard = 0;
                while (i >= 0 && i < n && guard++ < 64) { set.Add(i); i = m_Par[i]; }
            }
            var arr = new int[set.Count];
            set.CopyTo(arr);
            return arr;
        }

        // Fk of the chain bones only (other matrices of `mats` are stale). Same composition as Fk.
        public float4x4[] FkChain(float4 deg, float4x4[] mats)
        {
            if (m_Chain == null) return Fk(deg, mats);
            for (int c = 0; c < m_Chain.Length; c++)
            {
                int i = m_Chain[c];
                quaternion r = m_Rest[i];
                if (i == IBody) r = math.mul(r, quaternion.RotateZ(math.radians(deg.x)));
                else if (i == IBoom) r = math.mul(r, quaternion.RotateX(math.radians(deg.y)));
                else if (i == IStick) r = math.mul(r, quaternion.RotateX(math.radians(deg.z)));
                else if (i == IBkt) r = math.mul(r, quaternion.RotateX(math.radians(deg.w)));
                var local = float4x4.TRS(m_Pos[i], r, m_Scl[i]);
                int par = m_Par[i];
                mats[i] = par >= 0 && par < i ? math.mul(mats[par], local) : local;
            }
            return mats;
        }

        private float3 AxisObj(float4x4[] m, int i, float3 local)
        {
            int par = m_Par[i];
            float3x3 pm = par >= 0 ? new float3x3(m[par]) : float3x3.identity;
            return math.normalizesafe(DirToObj(math.mul(pm, math.rotate(m_Rest[i], local))));
        }

        public float2 Planar(float3 p) => new float2(math.dot(p - C, F0), math.dot(p, S));

        // arm space (slew deg, planar r, height h) <-> object space
        public float3 ArmToObj(float slewDeg, float r, float h)
        {
            var q = quaternion.AxisAngle(S, math.radians(slewDeg));
            float3 f = math.rotate(q, F0), a = math.rotate(q, A0);
            float3 ch = C - S * math.dot(C, S);
            return ch + f * r + a * E + S * h;
        }

        public bool ObjToArm(float3 t, out float slewDeg, out float r, out float h)
        {
            float3 q = t - C;
            float3 qh = q - S * math.dot(q, S);
            float rt = math.length(qh);
            float psi = math.atan2(math.dot(qh, SxF0), math.dot(qh, F0));
            bool ok = rt >= math.abs(E);
            r = math.sqrt(math.max(0f, rt * rt - E * E));
            slewDeg = Wrap180(math.degrees(psi - math.atan2(K * E, r)));
            h = math.dot(t, S);
            return ok;
        }

        // ------------------------------------------------------------ geometry from the rest pose

        private void Geometry()
        {
            var m0 = Fk(float4.zero, m_Tmp);
            C = Origin(m0, IBody);
            float3 sAxis = AxisObj(m0, IBody, new float3(0f, 0f, 1f));
            S = sAxis.y >= 0f ? sAxis : -sAxis;
            SlewAxisDotUp = S.y;
            float3 aRaw = AxisObj(m0, IBoom, new float3(1f, 0f, 0f));
            ArmAxisDotSlew = math.dot(aRaw, S);
            A0 = math.normalizesafe(aRaw - S * math.dot(aRaw, S), new float3(1f, 0f, 0f));
            F0 = math.normalizesafe(math.cross(A0, S), new float3(0f, 0f, 1f));
            float3 p3o = Origin(m0, IBkt);
            float3 refDir = p3o - C;
            refDir -= S * math.dot(refDir, S);
            if (math.lengthsq(refDir) < 0.01f) refDir = new float3(0f, 0f, 1f);
            if (math.dot(F0, refDir) < 0f) F0 = -F0;
            SxF0 = math.cross(S, F0);
            K = math.dot(A0, SxF0) >= 0f ? 1f : -1f;
            float3 p1o = Origin(m0, IBoom);
            E = math.dot(p1o - C, A0);
            L0 = math.distance(p1o, C);
            StickAxisDot = math.dot(AxisObj(m0, IStick, new float3(1f, 0f, 0f)), math.normalizesafe(aRaw));
            BucketAxisDot = math.dot(AxisObj(m0, IBkt, new float3(1f, 0f, 0f)), math.normalizesafe(aRaw));
            P1 = Planar(p1o);
            P2 = Planar(Origin(m0, IStick));
            P3 = Planar(p3o);
            L1 = math.distance(P1, P2);
            L2 = math.distance(P2, P3);
            A1 = Deg(P2 - P1);
            A2 = Deg(P3 - P2);
            // swing + joint senses, measured numerically (+5 deg on each joint)
            Sig0 = Wrap180(HorizAngle(Origin(Fk(new float4(5f, 0f, 0f, 0f), m_Tmp2), IBkt)) - HorizAngle(p3o)) / 5f;
            Sig1 = JointSigma(new float4(0f, 5f, 0f, 0f), IBoom, IStick, P1, P2);
            Sig2 = JointSigma(new float4(0f, 0f, 5f, 0f), IStick, IBkt, P2, P3);
            // a joint whose axis is (nearly) in the arm plane would give |sig| ~ 0: fall back to +-1 (logged by dg.rig as sig)
            if (math.abs(Sig0) < 0.2f) Sig0 = 1f;
            if (math.abs(Sig1) < 0.2f) Sig1 = 1f;
            if (math.abs(Sig2) < 0.2f) Sig2 = Sig1 >= 0f ? 1f : -1f;
            SetTipDefault();
            // chassis front: the farthest tyre (planar r + radius), else the boom pivot
            FrontClear = P1.x + 0.5f;
            bool tyre = false;
            for (int i = 0; i < n; i++)
            {
                if (Rs.Type[i] != BoneType.RollingTire && Rs.Type[i] != BoneType.SteeringTire) continue;
                float2 w = Planar(Origin(m0, i));
                float front = w.x + math.max(0.1f, w.y);
                FrontClear = tyre ? math.max(FrontClear, front) : front;
                tyre = true;
            }
        }

        private float HorizAngle(float3 p)
        {
            float3 q = p - C;
            return math.degrees(math.atan2(math.dot(q, SxF0), math.dot(q, F0)));
        }

        private float JointSigma(float4 deg, int joint, int child, float2 j0, float2 c0)
        {
            var m = Fk(deg, m_Tmp2);
            float2 j1 = Planar(Origin(m, joint)), c1 = Planar(Origin(m, child));
            return Wrap180(Deg(c1 - j1) - Deg(c0 - j0)) / 5f;
        }

        private void MeasureSig3()
        {
            var m = Fk(new float4(0f, 0f, 0f, 5f), m_Tmp2);
            float2 j1 = Planar(Origin(m, IBkt)), c1 = Planar(TipOf(m));
            Sig3 = Wrap180(Deg(c1 - j1) - Deg(P4 - P3)) / 5f;
            if (math.abs(Sig3) < 0.2f) Sig3 = Sig2 != 0f ? math.sign(Sig2) : 1f;   // tip on the joint axis: fall back to the stick sense
        }

        // ------------------------------------------------------------ bucket tip (not a bone: estimated, calibratable with dg.tip)

        // Default: continue the stick line from the bucket pivot by half the stick length (20 t machines: bucket radius ~0.5 stick).
        public void SetTipDefault()
        {
            float2 dir = math.normalizesafe(P3 - P2, new float2(0f, -1f));
            SetTipPlanar(P3 + dir * (0.5f * L2), "default(stick line, 0.5*stick)");
        }

        // len (m, scaled machine) from the bucket pivot, rotated deg (ccw in the r/h plane, + = up/outward) from the stick line.
        public void SetTip(float len, float deg)
        {
            float a = math.radians(Deg(P3 - P2) + deg);
            SetTipPlanar(P3 + new float2(math.cos(a), math.sin(a)) * math.max(0.05f, len), "manual(len=" + RRWLog.F(len) + " deg=" + RRWLog.F(deg) + ")");
        }

        public bool SetTipBone()
        {
            if (IBucket < 0) return false;
            var m0 = Fk(float4.zero, m_Tmp);
            float3 p = Origin(m0, IBucket);
            if (math.distance(Planar(p), P3) < 0.05f) return false;
            SetTipObj(p, "bone(Bucket origin)");
            return true;
        }

        private void SetTipPlanar(float2 tip, string src) => SetTipObj(ArmToObj(0f, tip.x, tip.y), src);

        private void SetTipObj(float3 tipObj, string src)
        {
            var m0 = Fk(float4.zero, m_Tmp);
            TipLocal = math.transform(math.inverse(m0[IBkt]), RigFromObj(tipObj));
            TipObjRest = tipObj;
            P4 = Planar(tipObj);
            Lb = math.distance(P3, P4);
            A3 = Deg(P4 - P3);
            TipSource = src;
            MeasureSig3();
        }

        // ------------------------------------------------------------ solve

        // Planar 2-link + bucket angle. attack = bucket (pivot -> tip) angle from straight down, + towards the machine (curl).
        private void SolvePlanar(float2 t, float attack, out float4 bone, ref DigIkResult res)
        {
            float phi3d = -90f - attack;
            float2 w = t - Dir(phi3d) * Lb;
            float2 d = w - P1;
            float D = math.length(d);
            float dMax = (L1 + L2) * 0.998f, dMin = math.abs(L1 - L2) * 1.002f + 1e-3f;
            if (D > dMax || D < dMin) res.Unreachable = true;
            float dc = math.clamp(D, dMin, dMax);
            float gam = D > 1e-5f ? Deg(d) : 0f;
            float beta = math.degrees(math.acos(math.clamp((L1 * L1 + dc * dc - L2 * L2) / (2f * L1 * dc), -1f, 1f)));
            float2 eA = P1 + Dir(gam + beta) * L1, eB = P1 + Dir(gam - beta) * L1;
            float phi1 = eA.y >= eB.y ? gam + beta : gam - beta;          // elbow up: the stick pivot stays high
            float boom = WrapAround(phi1, 0.5f * (DigLimits.BoomMin + DigLimits.BoomMax));
            if (boom < DigLimits.BoomMin) { boom = DigLimits.BoomMin; res.Limits |= 1; }
            else if (boom > DigLimits.BoomMax) { boom = DigLimits.BoomMax; res.Limits |= 1; }
            float2 pe = P1 + Dir(boom) * L1;
            float2 wc = P1 + Dir(gam) * dc;
            float phi2 = Deg(wc - pe);
            float stick = WrapAround(phi2 - boom, 0.5f * (DigLimits.StickMin + DigLimits.StickMax));
            if (stick < DigLimits.StickMin) { stick = DigLimits.StickMin; res.Limits |= 2; }
            else if (stick > DigLimits.StickMax) { stick = DigLimits.StickMax; res.Limits |= 2; }
            phi2 = boom + stick;
            float2 pw = pe + Dir(phi2) * L2;
            float bucket = WrapAround(phi3d - phi2, 0.5f * (DigLimits.BucketMin + DigLimits.BucketMax));
            if (bucket < DigLimits.BucketMin) { bucket = DigLimits.BucketMin; res.Limits |= 4; }
            else if (bucket > DigLimits.BucketMax) { bucket = DigLimits.BucketMax; res.Limits |= 4; }
            float phi3 = phi2 + bucket;
            res.PlanarTip = pw + Dir(phi3) * Lb;
            res.BoomElev = boom;
            res.StickRel = stick;
            res.BucketRel = bucket;
            bone = new float4(0f, Wrap180(boom - A1) / Sig1, Wrap180(stick - (A2 - A1)) / Sig2, Wrap180(bucket - (A3 - A2)) / Sig3);
        }

        // Bucket bone angle that keeps the absolute bucket attitude for given boom/stick bone angles (refinement helper).
        private float BucketFor(float boomDeg, float stickDeg, float attack)
        {
            float phi2 = A1 + Sig1 * boomDeg + (A2 - A1) + Sig2 * stickDeg;
            float bucket = math.clamp(WrapAround(-90f - attack - phi2, 0.5f * (DigLimits.BucketMin + DigLimits.BucketMax)), DigLimits.BucketMin, DigLimits.BucketMax);
            return Wrap180(bucket - (A3 - A2)) / Sig3;
        }

        private bool PhysicalOk(float4 deg)
        {
            float boom = WrapAround(A1 + Sig1 * deg.y, 0.5f * (DigLimits.BoomMin + DigLimits.BoomMax));
            float stick = WrapAround((A2 - A1) + Sig2 * deg.z, 0.5f * (DigLimits.StickMin + DigLimits.StickMax));
            return boom >= DigLimits.BoomMin - 0.01f && boom <= DigLimits.BoomMax + 0.01f && stick >= DigLimits.StickMin - 0.01f && stick <= DigLimits.StickMax + 0.01f;
        }

        public DigIkResult Solve(float3 targetObj, float attack, float floorH)
        {
            bool ok = ObjToArm(targetObj, out float slew, out float r, out float h);
            var res = SolveArm(slew, r, h, attack, floorH, targetObj);
            res.TooClose = !ok;
            if (!ok) res.Unreachable = true;
            return res;
        }

        // Target in arm space (slew deg, planar r, h). floorH: the tip never ends below it (target raised; -inf = off).
        public DigIkResult SolveArm(float slewDeg, float r, float h, float attack, float floorH, float3? targetObj = null)
        {
            var res = new DigIkResult();
            slewDeg = Wrap180(slewDeg);
            if (math.abs(slewDeg) > DigLimits.SlewMax) { slewDeg = math.sign(slewDeg) * DigLimits.SlewMax; res.Limits |= 8; }
            float hSolve = h;
            float4 bone = float4.zero;
            for (int it = 0; it < 3; it++)
            {
                var tmp = res;
                SolvePlanar(new float2(r, hSolve), attack, out bone, ref tmp);
                res = tmp;
                if (float.IsNegativeInfinity(floorH) || res.PlanarTip.y >= floorH - 0.001f) break;
                hSolve += floorH - res.PlanarTip.y + 0.001f;
                res.FloorRaised = true;
                res.Limits = (byte)(res.Limits & 8);
                res.Unreachable = false;
            }
            bone.x = slewDeg / Sig0;
            res.SlewDeg = slewDeg;
            res.R = r;
            res.H = hSolve;
            res.TargetObj = targetObj ?? ArmToObj(slewDeg, r, h);
            float3 aim = res.FloorRaised ? ArmToObj(slewDeg, r, hSolve) : res.TargetObj;
            float3 tip = FkTip(bone);
            float err = math.distance(tip, aim);
            // Gauss-Newton on (slew, boom, stick) through the full rig FK: removes the residual of a non-ideal rig
            // (arm axes not exactly parallel / perpendicular to the swing axis). Only for clean, reachable solutions.
            if (DigLimits.Refine && err > 0.005f && !res.Unreachable && (res.Limits & 7) == 0)
            {
                float4 x = bone;
                for (int it = 0; it < 4 && err > 0.002f; it++)
                {
                    float3 f0 = tip;
                    var J = new float3x3();
                    for (int k = 0; k < 3; k++)
                    {
                        float4 xp = x;
                        xp[k] += 0.05f;
                        xp.w = BucketFor(xp.y, xp.z, attack);
                        float3 d = (FkTip(xp) - f0) / 0.05f;
                        if (k == 0) J.c0 = d; else if (k == 1) J.c1 = d; else J.c2 = d;
                    }
                    float3x3 jt = math.transpose(J);
                    float3x3 jtj = math.mul(jt, J) + float3x3.identity * 1e-3f;
                    float3 step = math.mul(math.inverse(jtj), math.mul(jt, aim - f0));
                    float4 xn = x + new float4(step, 0f);
                    xn.w = BucketFor(xn.y, xn.z, attack);
                    if (!PhysicalOk(xn)) break;
                    float3 tn = FkTip(xn);
                    float en = math.distance(tn, aim);
                    if (!(en < err)) break;
                    x = xn; tip = tn; err = en; res.Refined = true;
                }
                bone = x;
            }
            res.Pose = new DigPose(bone);
            res.TipObj = tip;
            res.Err = math.distance(tip, res.TargetObj);
            return res;
        }

        // Largest planar r at which the tip reaches height h with this attack, without limits (scan from outside in).
        public float ReachAt(float h, float attack)
        {
            float rMax = math.abs(P1.x) + L1 + L2 + Lb + 1f;
            for (float r = rMax; r > 0.2f; r -= 0.05f)
            {
                var res = new DigIkResult();
                SolvePlanar(new float2(r, h), attack, out _, ref res);
                if (!res.Unreachable && res.Limits == 0 && math.distance(res.PlanarTip, new float2(r, h)) < 0.01f) return r;
            }
            return 0f;
        }

        // Smallest planar r >= rStart (up to rEnd) that is clean.
        public float MinReachAt(float h, float attack, float rStart, float rEnd)
        {
            for (float r = rStart; r <= rEnd; r += 0.05f)
            {
                var res = new DigIkResult();
                SolvePlanar(new float2(r, h), attack, out _, ref res);
                if (!res.Unreachable && res.Limits == 0 && math.distance(res.PlanarTip, new float2(r, h)) < 0.01f) return r;
            }
            return rEnd;
        }

        // ------------------------------------------------------------ report

        public string GeometryText()
        {
            var sb = new StringBuilder();
            sb.Append("swingPivot=").Append(V3(C)).Append(" swingAxis=").Append(V3(S)).Append(" swingAxisDotUp=").Append(RRWLog.F(SlewAxisDotUp))
              .Append(" armPlaneFwd=").Append(V3(F0)).Append(" armAxis=").Append(V3(A0)).Append(" armAxisDotSwing=").Append(RRWLog.F(ArmAxisDotSlew))
              .Append(" stickAxisDot=").Append(RRWLog.F(StickAxisDot)).Append(" bucketAxisDot=").Append(RRWLog.F(BucketAxisDot))
              .Append(" armOffsetE=").Append(RRWLog.F(E)).Append(" sig(slew,boom,stick,bucket)=(").Append(RRWLog.F(Sig0)).Append(',').Append(RRWLog.F(Sig1)).Append(',')
              .Append(RRWLog.F(Sig2)).Append(',').Append(RRWLog.F(Sig3)).Append(')');
            return sb.ToString();
        }

        public string LinksText()
        {
            var sb = new StringBuilder();
            sb.Append("planar(r,h) boomPivot=").Append(V2(P1)).Append(" stickPivot=").Append(V2(P2)).Append(" bucketPivot=").Append(V2(P3))
              .Append(" tip=").Append(V2(P4)).Append(" links: swingPivot->boomPivot=").Append(RRWLog.F(L0)).Append(" boom=").Append(RRWLog.F(L1))
              .Append(" stick=").Append(RRWLog.F(L2)).Append(" bucket(pivot->tip)=").Append(RRWLog.F(Lb)).Append(" (m at scale ").Append(RRWLog.F(Scale)).Append(")")
              .Append(" restAngles boomElev=").Append(RRWLog.F(A1)).Append(" stickRel=").Append(RRWLog.F(Wrap180(A2 - A1))).Append(" bucketRel=").Append(RRWLog.F(Wrap180(A3 - A2)))
              .Append(" tipSource=").Append(TipSource).Append(" frontClear=").Append(RRWLog.F(FrontClear));
            return sb.ToString();
        }

        // Piston pairs by name: "<prefix>Piston..01" + "<prefix>Piston..02", independent anchors (same rule as Rigs.Get).
        static List<int2> PairsOf(RigSub rs, List<string> names)
        {
            var list = new List<int2>();
            for (int a = 0; a < rs.Names.Length; a++)
            {
                string na = rs.Names[a];
                if (na == null || na.IndexOf("Piston", StringComparison.OrdinalIgnoreCase) < 0 || !na.EndsWith("01", StringComparison.Ordinal)) continue;
                string prefix = na.Substring(0, na.Length - 2);
                for (int b = 0; b < rs.Names.Length; b++)
                {
                    if (b == a || !string.Equals(rs.Names[b], prefix + "02", StringComparison.Ordinal)) continue;
                    if (rs.IsDescendant(b, a) || rs.IsDescendant(a, b)) continue;
                    list.Add(new int2(a, b));
                    names?.Add(prefix);
                    break;
                }
            }
            return list;
        }

        static string V3(float3 v) => "(" + RRWLog.F(v.x) + "," + RRWLog.F(v.y) + "," + RRWLog.F(v.z) + ")";
        static string V2(float2 v) => "(" + RRWLog.F(v.x) + "," + RRWLog.F(v.y) + ")";

        // ------------------------------------------------------------ math helpers

        public static float Deg(float2 v) => math.degrees(math.atan2(v.y, v.x));
        public static float2 Dir(float deg) { float a = math.radians(deg); return new float2(math.cos(a), math.sin(a)); }
        public static float Wrap180(float d) { d %= 360f; if (d > 180f) d -= 360f; else if (d <= -180f) d += 360f; return d; }
        public static float WrapAround(float d, float center) => center + Wrap180(d - center);
    }

    // IK models per prefab (built once from the prefab's ProceduralBone data at the clone's root scale; null = rig read failed, the
    // prefab keeps the fixed keyframe cycle - logged once). Cleared on preload (Rigs.Clear).
    public static class DigArms
    {
        private static readonly Dictionary<Entity, DigArm> s_Arms = new Dictionary<Entity, DigArm>();
        public static readonly Dictionary<Entity, string> Why = new Dictionary<Entity, string>();

        public static void Clear() { s_Arms.Clear(); Why.Clear(); }

        public static DigArm Get(EntityManager em, PrefabSystem ps, Entity prefab, float scale)
        {
            if (prefab == Entity.Null || ps == null) return null;
            if (s_Arms.TryGetValue(prefab, out var arm)) return arm;
            string why = null;
            try
            {
                var rig = Rigs.Get(em, ps, prefab);
                RigSub rs = null;
                foreach (var sub in rig.Subs) if (sub.Find("Arm01") >= 0 && sub.Find("Body") >= 0) { rs = sub; break; }
                if (rs == null) why = "no sub mesh with Body + Arm01 bones";
                else
                {
                    arm = DigArm.Build(rs, scale, out why);
                    if (arm != null)
                    {
                        // Verified tip calibration (MxConst): the stick line from the bucket pin, kDigTipStickFactor x the stick length
                        if (math.abs(MxConst.kDigTipStickFactor - 0.5f) > 1e-4f || math.abs(MxConst.kDigTipDeg) > 1e-4f)
                            arm.SetTip(MxConst.kDigTipStickFactor * arm.L2, MxConst.kDigTipDeg);
                    }
                }
            }
            catch (Exception e) { arm = null; why = "exception: " + e.Message; }
            s_Arms[prefab] = arm;
            Why[prefab] = arm != null ? "ok " + arm.LinksText() : why;
            if (arm == null) RRWLog.Once("machines-ik-rig-" + prefab.Index, "machines: IK rig read failed for prefab " + RRWLog.E(prefab) + " (" + why + "): the keyframe dig cycle is used");
            else RRWLog.Once("machines-ik-rig-ok-" + prefab.Index, "machines: IK rig of prefab " + RRWLog.E(prefab) + ": " + arm.LinksText() + " | " + arm.GeometryText());
            return arm;
        }
    }
}
