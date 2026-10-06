using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // THE ONLY SAVED STATE of Realistic Road Works v3: one per road edge under works.
    // Full type name "RealisticRoadWorks.V3.RoadWorksSite" (do not rename or move namespace).
    // Without the mod the game logs "Not serializable type: RealisticRoadWorks.V3.RoadWorksSite" and skips it:
    // roads load open and visible (Hidden/clones/props/areas/puppets are never saved).
    //
    // Layout (little endian, via IWriter):
    //   ushort version (=kVersion), ushort payloadBytes, then payload v1 (49 bytes):
    //   byte  m_Kind, byte m_Mode, ushort m_Flags,
    //   uint  m_WorkDone, uint m_WorkRequired, uint m_ProjectId,
    //   float m_ChainU0, float m_ChainU1, float m_ChainLength,
    //   ushort m_Seed, int m_PaidCost,
    //   float m_NatAbove, float m_NatBelow,
    //   byte  m_RestoreT, byte m_RestoreY16 (sbyte bits),
    //   byte  m_CancelPhase, float m_CancelFront
    // Forward compatibility: a reader that sees version >= 1 and payloadBytes >= kPayloadV1 reads the v1 fields and skips
    // the rest (newer versions only APPEND fields). Only payloadBytes < kPayloadV1 (or version 1 with a different size =
    // corrupt) gives a neutral Incompatible site, which the Director finishes at once. A downgrade never breaks a city.
    // (v3 is unreleased: the v1 payload may still change until the first public build; after that, append only.)
    public struct RoadWorksSite : IComponentData, ISerializable
    {
        public const ushort kVersion = 1;
        public const ushort kPayloadV1 = 49;

        public byte m_Kind;            // WorksKind
        public byte m_Mode;            // VisualMode chosen at start (fixed for the works' lifetime)
        public ushort m_Flags;         // SiteFlags
        public uint m_WorkDone;        // work frames accrued (only during working hours, unpaused)
        public uint m_WorkRequired;    // work frames needed (identical on every edge of a project)
        public uint m_ProjectId;       // project = one chain of edges worked by one crew
        public float m_ChainU0;        // chain coordinate (m) of this edge's curve t = 0
        public float m_ChainU1;        // chain coordinate (m) of this edge's curve t = 1 (< U0 if the edge runs backwards)
        public float m_ChainLength;    // project chain length U (m)
        public ushort m_Seed;          // livery / layout randomness
        public int m_PaidCost;         // what the player paid for this edge (construction) - for refunds and rush pricing
        public float m_NatAbove;       // natural ground above grade (m, max over the edge); NaN = not sampled
        public float m_NatBelow;       // natural ground below grade (m, positive); NaN = not sampled
        public byte m_RestoreT;        // D2 morph start t (0..255 -> 0..1): 255 for normal demolition
        public byte m_RestoreY16;      // D2 morph start y in 1/16 m (sbyte bits): -8 (= -0.5 m) for normal demolition
        public byte m_CancelPhase;     // WorksPhase of the construction when it was cancelled (WorksPhase.None = never)
        public float m_CancelFront;    // chain u of the main front at cancel time (decals/heaps continue from it)

        public WorksKind Kind { get => (WorksKind)m_Kind; set => m_Kind = (byte)value; }
        public VisualMode Mode { get => (VisualMode)m_Mode; set => m_Mode = (byte)value; }
        public SiteFlags Flags { get => (SiteFlags)m_Flags; set => m_Flags = (ushort)value; }
        public bool Has(SiteFlags f) => ((SiteFlags)m_Flags & f) != 0;
        public void Set(SiteFlags f, bool on) { if (on) m_Flags |= (ushort)f; else m_Flags &= (ushort)~f; }

        public float Progress => m_WorkRequired == 0 ? 1f : math.saturate(m_WorkDone / (float)m_WorkRequired);
        public float RestoreT => m_RestoreT / 255f;
        public float RestoreY => unchecked((sbyte)m_RestoreY16) / 16f;
        public WorksPhase CancelPhase => Has(SiteFlags.CancelledBuild) ? (WorksPhase)m_CancelPhase : WorksPhase.None;
        public bool NaturalValid => Has(SiteFlags.NaturalSampled) && !float.IsNaN(m_NatAbove) && !float.IsNaN(m_NatBelow);
        public float ChainLo => math.min(m_ChainU0, m_ChainU1);
        public float ChainHi => math.max(m_ChainU0, m_ChainU1);

        public static byte EncodeY16(float y) => unchecked((byte)(sbyte)math.clamp((int)math.round(y * 16f), -127, 127));
        public static byte EncodeT(float t) => (byte)math.clamp((int)math.round(t * 255f), 0, 255);

        public static RoadWorksSite Create(WorksKind kind, VisualMode mode, uint workRequired, uint projectId,
                                           float chainU0, float chainU1, float chainLength, ushort seed, int paidCost)
        {
            return new RoadWorksSite
            {
                m_Kind = (byte)kind,
                m_Mode = (byte)mode,
                m_WorkRequired = math.max(1u, workRequired),
                m_ProjectId = projectId,
                m_ChainU0 = chainU0,
                m_ChainU1 = chainU1,
                m_ChainLength = chainLength,
                m_Seed = seed,
                m_PaidCost = paidCost,
                m_NatAbove = float.NaN,
                m_NatBelow = float.NaN,
                m_RestoreT = 255,
                m_RestoreY16 = EncodeY16(-RRWConst.kDemolitionDepth),
                m_CancelPhase = (byte)WorksPhase.None,
                m_CancelFront = 0f,
            };
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(kPayloadV1);
            writer.Write(m_Kind);
            writer.Write(m_Mode);
            writer.Write(m_Flags);
            writer.Write(m_WorkDone);
            writer.Write(m_WorkRequired);
            writer.Write(m_ProjectId);
            writer.Write(m_ChainU0);
            writer.Write(m_ChainU1);
            writer.Write(m_ChainLength);
            writer.Write(m_Seed);
            writer.Write(m_PaidCost);
            writer.Write(m_NatAbove);
            writer.Write(m_NatBelow);
            writer.Write(m_RestoreT);
            writer.Write(m_RestoreY16);
            writer.Write(m_CancelPhase);
            writer.Write(m_CancelFront);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out ushort version);
            reader.Read(out ushort size);
            bool v1Exact = version == 1 && size == kPayloadV1;
            bool newer = version > 1 && size >= kPayloadV1;
            if (v1Exact || newer)
            {
                reader.Read(out m_Kind);
                reader.Read(out m_Mode);
                reader.Read(out m_Flags);
                reader.Read(out m_WorkDone);
                reader.Read(out m_WorkRequired);
                reader.Read(out m_ProjectId);
                reader.Read(out m_ChainU0);
                reader.Read(out m_ChainU1);
                reader.Read(out m_ChainLength);
                reader.Read(out m_Seed);
                reader.Read(out m_PaidCost);
                reader.Read(out m_NatAbove);
                reader.Read(out m_NatBelow);
                reader.Read(out m_RestoreT);
                reader.Read(out m_RestoreY16);
                reader.Read(out m_CancelPhase);
                reader.Read(out m_CancelFront);
                for (int i = kPayloadV1; i < size; i++) reader.Read(out byte _);   // newer appended fields
                return;
            }
            // Unknown layout (newer version or corrupted size): consume exactly the payload, keep a safe neutral site.
            for (int i = 0; i < size; i++) reader.Read(out byte _);
            this = default;
            m_WorkRequired = 1;
            m_WorkDone = 1;
            m_NatAbove = float.NaN;
            m_NatBelow = float.NaN;
            m_Mode = (byte)VisualMode.Minimal;
            m_Flags = (ushort)SiteFlags.Incompatible;
            m_CancelPhase = (byte)WorksPhase.None;
        }
    }
}
