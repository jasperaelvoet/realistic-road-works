using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Unity.Collections;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

// Deferred upgrades: while partial upgrade works (mode H) build, the road keeps its OLD state in the game (type, upgrades, curve,
// junction nodes, the neighbours the tool moved), so its junctions, kerbs and markings stay the old ones; the state the tool
// applied waits on the entities in RRWDeferredNet and is written when the works reach their finishing (the re-marking window,
// or the completion). The tool's apply is snapshot by Tools (WorksTagSystem), reverted at the next Modification1
// (DeferredNetSystem), and switched on the Director's request.
//  * Saved: a save holds the old road plus the pending target; without the mod the old road simply stays.
//  * EDGE frame: the works keep the frame of the target curve (EdgeRecord.Arc is built from it while deferred), so bands,
//    sub-strips, machines and props sit where the new road will be; traffic uses the old road's live lanes measured in that frame.
namespace RealisticRoadWorks.V3
{
    public enum DeferredKind : byte { None = 0, Edge = 1, Node = 2 }

    // One strip of the target layout of an edge (CompositionLayout strip, 1/16 m).
    public struct DeferredStrip
    {
        public short Lo16, Hi16;
        public byte Kind;
        public sbyte Dir;
    }

    public struct RRWDeferredNet : IComponentData, ISerializable
    {
        public const ushort kVersion = 1;
        public const int kMaxStrips = CompositionLayout.kMax;

        public uint m_ProjectId;
        public byte m_Kind;                 // DeferredKind
        public Entity m_Prefab;             // target prefab
        public CompositionFlags m_Upgraded; // target Upgraded flags
        public bool m_HasUpgraded;
        public Bezier4x3 m_Curve;           // edge: target curve
        public float3 m_Position;           // node: target position
        public quaternion m_Rotation;       // node: target rotation
        public float2 m_Elevation;          // target Elevation (edge / node)
        public bool m_HasElevation;
        // edge: the target layout (the classifier's reading of the new road in the target curve's frame)
        public float m_OuterL, m_OuterR;
        public byte m_StripCount;
        public FixedList512Bytes<DeferredStrip> m_Strips;

        public DeferredKind Kind => (DeferredKind)m_Kind;

        public void SetLayout(CompositionLayout l)
        {
            m_Strips.Clear();
            m_OuterL = l.OuterL;
            m_OuterR = l.OuterR;
            for (int i = 0; i < l.Count && m_Strips.Length < kMaxStrips; i++)
            {
                var s = l.Strips[i];
                m_Strips.Add(new DeferredStrip { Lo16 = UpgradeBand.Q16(s.Lo), Hi16 = UpgradeBand.Q16(s.Hi), Kind = (byte)s.Kind, Dir = s.Dir });
            }
            m_StripCount = (byte)m_Strips.Length;
        }

        // The stored target layout (Normalise()d). False when none was stored.
        public bool GetLayout(CompositionLayout l)
        {
            l.Clear();
            if (m_Strips.Length == 0) return false;
            l.OuterL = m_OuterL;
            l.OuterR = m_OuterR;
            for (int i = 0; i < m_Strips.Length; i++)
            {
                var s = m_Strips[i];
                l.Add(s.Lo16 / 16f, s.Hi16 / 16f, (StripKind)s.Kind, s.Dir);
            }
            l.Normalise();
            return true;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_ProjectId);
            writer.Write(m_Kind);
            writer.Write(m_Prefab);
            writer.Write(m_HasUpgraded);
            writer.Write((uint)m_Upgraded.m_General);
            writer.Write((uint)m_Upgraded.m_Left);
            writer.Write((uint)m_Upgraded.m_Right);
            writer.Write(m_Curve.a); writer.Write(m_Curve.b); writer.Write(m_Curve.c); writer.Write(m_Curve.d);
            writer.Write(m_Position);
            writer.Write(m_Rotation);
            writer.Write(m_HasElevation);
            writer.Write(m_Elevation);
            writer.Write(m_OuterL);
            writer.Write(m_OuterR);
            writer.Write((byte)m_Strips.Length);
            for (int i = 0; i < m_Strips.Length; i++)
            {
                var s = m_Strips[i];
                writer.Write(s.Lo16); writer.Write(s.Hi16); writer.Write(s.Kind); writer.Write((byte)s.Dir);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out ushort version);
            reader.Read(out m_ProjectId);
            reader.Read(out m_Kind);
            reader.Read(out m_Prefab);
            reader.Read(out m_HasUpgraded);
            reader.Read(out uint g); reader.Read(out uint l); reader.Read(out uint r);
            m_Upgraded = new CompositionFlags((CompositionFlags.General)g, (CompositionFlags.Side)l, (CompositionFlags.Side)r);
            reader.Read(out float3 a); reader.Read(out float3 b); reader.Read(out float3 c); reader.Read(out float3 d);
            m_Curve = new Bezier4x3(a, b, c, d);
            reader.Read(out m_Position);
            reader.Read(out m_Rotation);
            reader.Read(out m_HasElevation);
            reader.Read(out m_Elevation);
            reader.Read(out m_OuterL);
            reader.Read(out m_OuterR);
            reader.Read(out byte n);
            m_Strips.Clear();
            for (int i = 0; i < n; i++)
            {
                reader.Read(out short lo); reader.Read(out short hi); reader.Read(out byte k); reader.Read(out byte dir);
                if (m_Strips.Length < kMaxStrips) m_Strips.Add(new DeferredStrip { Lo16 = lo, Hi16 = hi, Kind = k, Dir = (sbyte)dir });
            }
            m_StripCount = (byte)m_Strips.Length;
        }
    }

    // Requests between the modules (main thread).
    public static class DeferredNet
    {
        // Tools -> DeferredNetSystem: the snapshot of one apply (filled before ApplyNetSystem, reverted at the next Modification1).
        public struct Snapshot
        {
            public Entity Entity;
            public DeferredKind Kind;
            public Entity OldPrefab;
            public CompositionFlags OldUpgraded; public bool OldHasUpgraded;
            public Bezier4x3 OldCurve;
            public float3 OldPosition; public quaternion OldRotation;
            public float2 OldElevation; public bool OldHasElevation;
            public RRWDeferredNet Target;   // the tool's state (project id resolved at the revert)
            public Entity SiteEdge;         // an upgraded edge this entity belongs to (project id source)
        }

        public static readonly List<Snapshot> PendingRevert = new List<Snapshot>();
        // Director -> DeferredNetSystem: projects whose target state is written now
        public static readonly HashSet<uint> SwitchRequests = new HashSet<uint>();
        // Director -> DeferredNetSystem: projects whose works ended without their target (bulldozed): drop the targets
        public static readonly HashSet<uint> DropRequests = new HashSet<uint>();

        public static int Reverted, Switched, Dropped;

        // The curve the works use for a road piece: the target curve while it is deferred (the works build the new road), else the
        // live one. The edge must have a Curve.
        public static Bezier4x3 WorksCurve(EntityManager em, Entity e)
        {
            if (em.HasComponent<RRWDeferredNet>(e))
            {
                var d = em.GetComponentData<RRWDeferredNet>(e);
                if (d.Kind == DeferredKind.Edge) return d.m_Curve;
            }
            return em.GetComponentData<Game.Net.Curve>(e).m_Bezier;
        }

        // The deferred state of a road piece (false when it is not deferred or the entity is a node).
        public static bool TryEdge(EntityManager em, Entity e, out RRWDeferredNet d)
        {
            d = default;
            if (!em.Exists(e) || !em.HasComponent<RRWDeferredNet>(e)) return false;
            d = em.GetComponentData<RRWDeferredNet>(e);
            return d.Kind == DeferredKind.Edge;
        }
    }
}
