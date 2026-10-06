using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

// v2 save readers. EXACT copies of the v2 type names, namespace, fields and Serialize/Deserialize from
// v2's RoadWorks.cs, so a v2 save deserializes into them. v3 never adds any of these: the
// LegacyMigrationSystem converts them to RoadWorksSite and strips them in the first frames after the load.
// Do not rename, move or change the byte layout.
namespace RealisticRoadWorks
{
    public enum RoadWorksKind : byte { Construction = 0, Demolition = 1 }

    // v2: attached to a road edge while crews worked on it (10 bytes saved: start, end, kind, flags).
    public struct RoadWorks : IComponentData, ISerializable
    {
        public uint m_StartFrame;
        public uint m_EndFrame;
        public RoadWorksKind m_Kind;
        public byte m_Flags;
        public byte m_Layout;
        public byte m_DynamicKey;
        public byte m_Visual;
        public byte m_GroundKey;
        public uint m_GeometryHash;

        public const byte kIconShown = 1;
        public const byte kRushed = 2;
        public const byte kMaintenanceSuspended = 4;
        public const byte kGroundCleared = 8;
        public const byte kNoLayout = 0xFF;

        public float Progress(uint frame) =>
            m_EndFrame <= m_StartFrame ? 1f : math.saturate((frame - m_StartFrame) / (float)(m_EndFrame - m_StartFrame));

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_StartFrame);
            writer.Write(m_EndFrame);
            writer.Write((byte)m_Kind);
            writer.Write(m_Flags);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_StartFrame);
            reader.Read(out m_EndFrame);
            reader.Read(out byte kind);
            m_Kind = (RoadWorksKind)kind;
            reader.Read(out m_Flags);
            m_Layout = m_DynamicKey = m_GroundKey = m_Visual = kNoLayout;
            m_GeometryHash = 0;
        }
    }

    // v2: the props placed on an edge's work site.
    [InternalBufferCapacity(0)]
    public struct RoadWorksPropElement : IBufferElementData, ISerializable
    {
        public Entity m_Prop;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write(m_Prop);
        public void Deserialize<TReader>(TReader reader) where TReader : IReader => reader.Read(out m_Prop);
    }

    // v2: marks a prop entity as belonging to a work site.
    public struct RoadWorksProp : IComponentData, ISerializable
    {
        public Entity m_Edge;
        public byte m_Group;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Edge);
            writer.Write(m_Group);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Edge);
            reader.Read(out m_Group);
        }
    }

    public enum RoadWorksAction : byte { Cancel = 1 }

    // v2: player request from the info panel.
    public struct RoadWorksRequest : IComponentData, ISerializable
    {
        public RoadWorksAction m_Action;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write((byte)m_Action);
        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte a);
            m_Action = (RoadWorksAction)a;
        }
    }
}
