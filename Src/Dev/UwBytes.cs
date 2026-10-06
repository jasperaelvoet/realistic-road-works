#if DEVTOOLS
using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// Minimal in-memory IWriter / IReader for the dev dump of a site's saved bytes (only the primitive overloads a RoadWorksSite
// uses are implemented; the rest throw). Little endian like the game's binary writer.
namespace RealisticRoadWorks.V3.DevCmds
{
    internal sealed class UwBytesWriter : IWriter
    {
        public readonly List<byte> Bytes = new List<byte>(128);
        public Context context => default;
        public void Initialize(Context context, NativeList<byte> buffer, NativeArray<Entity> entityTable) { }
        public WriterBlock Begin() => default;
        public bool End(WriterBlock block) => true;
        public bool End(WriterBlock block, out int size) { size = 0; return true; }

        private void Put(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); Bytes.AddRange(b); }
        public void Write(byte value) => Bytes.Add(value);
        public void Write(sbyte value) => Bytes.Add(unchecked((byte)value));
        public void Write(bool value) => Bytes.Add(value ? (byte)1 : (byte)0);
        public void Write(short value) => Put(BitConverter.GetBytes(value));
        public void Write(ushort value) => Put(BitConverter.GetBytes(value));
        public void Write(int value) => Put(BitConverter.GetBytes(value));
        public void Write(uint value) => Put(BitConverter.GetBytes(value));
        public void Write(long value) => Put(BitConverter.GetBytes(value));
        public void Write(ulong value) => Put(BitConverter.GetBytes(value));
        public void Write(float value) => Put(BitConverter.GetBytes(value));
        public void Write(double value) => Put(BitConverter.GetBytes(value));
        public void Write(char value) => Put(BitConverter.GetBytes(value));
        public void Write<TSerializable>(TSerializable value) where TSerializable : ISerializable => value.Serialize(this);

        private static Exception No() => new NotSupportedException("UwBytesWriter: only primitive writes");
        public void Write(NativeArray<Entity> value) => throw No();
        public void Write(NativeList<Entity> value) => throw No();
        public void Write(NativeList<int> value) => throw No();
        public void Write<TSerializable>(NativeArray<TSerializable> value) where TSerializable : struct, ISerializable => throw No();
        public void Write(NativeArray<int> value) => throw No();
        public void Write(NativeArray<int2> value) => throw No();
        public void Write(NativeArray<ushort> value) => throw No();
        public void Write(NativeArray<byte> value) => throw No();
        public void Write(NativeArray<float4> value) => throw No();
        public void Write(NativeArray<float2> value) => throw No();
        public void Write(NativeArray<byte> value, int stride) => throw No();
        public void Write(Entity value) => throw No();
        public void Write(Entity value, bool ignoreVersion) => throw No();
        public void Write(Bezier4x3 curve) => throw No();
        public void Write(string value) => throw No();
        public void Write(Color value) => throw No();
        public void Write(Color32 value) => throw No();
        public void Write(quaternion value) => throw No();
        public void Write(float4 value) => throw No();
        public void Write(float3 value) => throw No();
        public void Write(float2 value) => throw No();
        public void Write(int4 value) => throw No();
        public void Write(int3 value) => throw No();
        public void Write(int2 value) => throw No();
        public void Write(bool4 value) => throw No();
        public void Write(bool3 value) => throw No();
        public void Write(bool2 value) => throw No();
        public void Write(uint4 value) => throw No();
        public void Write(Colossal.Hash128 hash) => throw No();
    }

    internal sealed class UwBytesReader : IReader
    {
        private readonly byte[] m_B;
        public int Position;
        public UwBytesReader(byte[] bytes) { m_B = bytes; }
        public Context context => default;
        public void Initialize(Context context, NativeArray<byte> buffer, NativeReference<int> position, NativeArray<Entity> entityTable) { }
        public ReaderBlock Begin() => default;
        public ReaderBlock Begin(out int size) { size = 0; return default; }
        public bool End(ReaderBlock block) => true;
        public void Skip(int size) => Position += size;

        private byte[] Take(int n)
        {
            if (Position + n > m_B.Length) throw new IndexOutOfRangeException("UwBytesReader: read past the end at " + Position);
            var r = new byte[n];
            Array.Copy(m_B, Position, r, 0, n);
            Position += n;
            if (!BitConverter.IsLittleEndian) Array.Reverse(r);
            return r;
        }
        public void Read(out byte value) => value = Take(1)[0];
        public void Read(out sbyte value) => value = unchecked((sbyte)Take(1)[0]);
        public void Read(out bool value) => value = Take(1)[0] != 0;
        public void Read(out short value) => value = BitConverter.ToInt16(Take(2), 0);
        public void Read(out ushort value) => value = BitConverter.ToUInt16(Take(2), 0);
        public void Read(out int value) => value = BitConverter.ToInt32(Take(4), 0);
        public void Read(out uint value) => value = BitConverter.ToUInt32(Take(4), 0);
        public void Read(out long value) => value = BitConverter.ToInt64(Take(8), 0);
        public void Read(out ulong value) => value = BitConverter.ToUInt64(Take(8), 0);
        public void Read(out float value) => value = BitConverter.ToSingle(Take(4), 0);
        public void Read(out double value) => value = BitConverter.ToDouble(Take(8), 0);
        public void Read(out char value) => value = BitConverter.ToChar(Take(2), 0);
        public void Read<TSerializable>(out TSerializable value) where TSerializable : struct, ISerializable { value = default; value.Deserialize(this); }
        public void Read<TSerializable>(TSerializable value) where TSerializable : class, ISerializable => value.Deserialize(this);

        private static Exception No() => new NotSupportedException("UwBytesReader: only primitive reads");
        public void Read(NativeArray<Entity> value) => throw No();
        public void Read(NativeArray<int> value) => throw No();
        public void Read(NativeArray<int2> value) => throw No();
        public void Read(NativeArray<ushort> value) => throw No();
        public void Read(NativeArray<byte> value) => throw No();
        public void Read(NativeArray<float4> value) => throw No();
        public void Read(NativeArray<float2> value) => throw No();
        public void Read(NativeArray<byte> value, int stride) => throw No();
        public void Read(NativeList<int> value) => throw No();
        public void Read(NativeList<Entity> value) => throw No();
        public void Read<TSerializable>(NativeArray<TSerializable> value) where TSerializable : struct, ISerializable => throw No();
        public void Read(out Entity value) => throw No();
        public void Read(out Bezier4x3 curve) => throw No();
        public void Read(out string value) => throw No();
        public void Read(out Color value) => throw No();
        public void Read(out Color32 value) => throw No();
        public void Read(out quaternion value) => throw No();
        public void Read(out float4 value) => throw No();
        public void Read(out float3 value) => throw No();
        public void Read(out float2 value) => throw No();
        public void Read(out int4 value) => throw No();
        public void Read(out int3 value) => throw No();
        public void Read(out int2 value) => throw No();
        public void Read(out bool4 value) => throw No();
        public void Read(out bool3 value) => throw No();
        public void Read(out bool2 value) => throw No();
        public void Read(out uint4 value) => throw No();
        public void Read(out Colossal.Hash128 value) => throw No();
    }

    // The site reader of version 3.0.0 (payload v1 only), kept verbatim as the reference for "what does 3.0.0 see".
    internal struct SiteReader300 : ISerializable
    {
        public byte m_Kind, m_Mode;
        public ushort m_Flags;
        public uint m_WorkDone, m_WorkRequired, m_ProjectId;
        public float m_ChainU0, m_ChainU1, m_ChainLength;
        public ushort m_Seed;
        public int m_PaidCost;
        public float m_NatAbove, m_NatBelow;
        public byte m_RestoreT, m_RestoreY16, m_CancelPhase;
        public float m_CancelFront;
        public bool Incompatible;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => throw new NotSupportedException();

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out ushort version);
            reader.Read(out ushort size);
            bool v1Exact = version == 1 && size == 49;
            bool newer = version > 1 && size >= 49;
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
                for (int i = 49; i < size; i++) reader.Read(out byte _);
                return;
            }
            for (int i = 0; i < size; i++) reader.Read(out byte _);
            this = default;
            m_WorkRequired = 1;
            m_WorkDone = 1;
            m_Mode = 3;
            Incompatible = true;
        }
    }
}
#endif
