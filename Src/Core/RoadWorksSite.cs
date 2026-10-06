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
    //   ushort version (= kVersion), ushort payloadBytes (= kPayloadV2), then
    //   payload v1 (49 bytes, written by every version since 3.0.0):
    //     byte  m_Kind, byte m_Mode, ushort m_Flags,
    //     uint  m_WorkDone, uint m_WorkRequired, uint m_ProjectId,
    //     float m_ChainU0, float m_ChainU1, float m_ChainLength,
    //     ushort m_Seed, int m_PaidCost,
    //     float m_NatAbove, float m_NatBelow,
    //     byte  m_RestoreT, byte m_RestoreY16 (sbyte bits),
    //     byte  m_CancelPhase, float m_CancelFront
    //   upgrade tail (46 bytes, version 2):
    //     byte m_PlanKind, byte m_UpgradeClass, byte m_BandCount, byte m_WindowCount, byte m_SetupP8, byte m_UpFlags,
    //     uint workDone (the real work done of a site with a plan kind; 0 otherwise),
    //     4 x UpgradeBand (byte KindSide, byte WinPrim, byte G0, short Lo16, short Hi16; unused slots 0),
    //     8 x byte window end (1/255 of p; unused slots 0)
    //
    // Compatibility (3.0.0 is released, so the payload is append-only):
    //  * The game reads all sites of a save as one data block and fails the whole load if a reader consumes more or fewer
    //    bytes than were written, so every reader path below consumes exactly 4 + payloadBytes.
    //  * 3.0.0 reads version >= 2 with payloadBytes >= 49 as "newer": the v1 fields, the rest skipped. For a mode 2
    //    construction (mode H upgrade works) the writer puts m_WorkRequired into the v1 m_WorkDone field, so 3.0.0 sees a
    //    FINISHED mode 2 site: it completes at once, releases (mode 2 runs like mode D there: no machines) and removes the
    //    site; Cancel and the bulldozer find nothing left to cancel, so the road is never demolished by a cancel. The road
    //    keeps its new type (it already had it). The real progress is only in the tail; in memory m_WorkDone is the truth
    //    and the swap exists only here. A mode 2 construction without a plan kind is written as finished too (no version
    //    ever runs such a site as unfinished works over an existing road). The tail is written only for a mode 2
    //    construction; a plan kind left on any other site (it could never validate) is written as zeros with the site's
    //    real progress in the v1 fields.
    //  * This reader: version 1 / 49 bytes -> v1, no tail (every 3.0.0 save); version >= 2 and >= 95 bytes -> v1 + tail, the
    //    rest skipped (later appends); version >= 2 and 49..94 bytes -> v1, no tail; anything else -> a neutral Incompatible
    //    site that the Director finishes at once. A downgrade never breaks a city.
    //  * After reading: a mode 2 site without a plan kind can only be a 3.0.0 resave of an upgrade site (its v1 part says
    //    finished): it becomes mode D and completes. A tail that fails validation is dropped the same way (the v1 view, which
    //    says finished, is kept). Both are noted in m_LoadNote for the load log. A site without a plan kind keeps no tail
    //    bytes (whatever the payload carried there is cleared).
    public struct RoadWorksSite : IComponentData, ISerializable
    {
        public const ushort kVersion = 2;
        public const ushort kPayloadV1 = 49;
        public const ushort kPayloadV2 = 95;
        public const int kTailBytes = kPayloadV2 - kPayloadV1;   // 46

        // m_LoadNote values (runtime only, never written)
        public const byte kNoteNone = 0;
        public const byte kNoteHalfWidthNoPlan = 1;   // mode 2 without a plan kind (3.0.0 resave): now mode D, finished as saved
        public const byte kNoteBadTail = 2;           // tail failed validation: dropped, mode D, finished as saved

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

        // ---- upgrade tail (version 2). All zero for every site without a plan kind.
        public byte m_PlanKind;        // PlanKind
        public byte m_UpgradeClass;    // UpgradeClass (1..4)
        public byte m_BandCount;       // 0..4 bands of THIS edge
        public byte m_WindowCount;     // 1..8 (project-uniform)
        public byte m_SetupP8;         // setup share, 1/255, fixed at creation (project-uniform)
        public byte m_UpFlags;         // UpgradeFlags
        public UpgradeBand m_Band0, m_Band1, m_Band2, m_Band3;   // edge frame of this edge
        public ulong m_WinEnds;        // byte k = end of window k, 1/255 (project-uniform)

        public byte m_LoadNote;        // RUNTIME ONLY (never written): what the reader changed (kNote*)

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

        public PlanKind Plan { get => (PlanKind)m_PlanKind; set => m_PlanKind = (byte)value; }
        public UpgradeClass UpClass { get => (UpgradeClass)m_UpgradeClass; set => m_UpgradeClass = (byte)value; }
        public UpgradeFlags UpFlags { get => (UpgradeFlags)m_UpFlags; set => m_UpFlags = (byte)value; }
        public bool IsUpgrade => m_PlanKind == (byte)PlanKind.Upgrade;
        public UpgradeSchedule Schedule => UpgradeSchedule.Of(this);

        public UpgradeBand Band(int i) => i == 0 ? m_Band0 : i == 1 ? m_Band1 : i == 2 ? m_Band2 : m_Band3;

        public void SetBand(int i, UpgradeBand b)
        {
            if (i == 0) m_Band0 = b; else if (i == 1) m_Band1 = b; else if (i == 2) m_Band2 = b; else m_Band3 = b;
        }

        public byte WinEnd8(int k) => (byte)((m_WinEnds >> (8 * math.clamp(k, 0, 7))) & 0xFF);

        // Clears every upgrade field (plan kind None).
        public void ClearTail()
        {
            m_PlanKind = m_UpgradeClass = m_BandCount = m_WindowCount = m_SetupP8 = m_UpFlags = 0;
            m_Band0 = m_Band1 = m_Band2 = m_Band3 = default;
            m_WinEnds = 0;
        }

        // Writes an upgrade tail: plan kind, class, this edge's bands (edge frame; at most kUwMaxBands), the project schedule.
        public void SetTail(PlanKind plan, UpgradeClass cls, in UpgradeSchedule s, UpgradeFlags flags, UpgradeBand b0 = default,
                            UpgradeBand b1 = default, UpgradeBand b2 = default, UpgradeBand b3 = default, int bandCount = 0)
        {
            m_PlanKind = (byte)plan;
            m_UpgradeClass = (byte)cls;
            m_BandCount = (byte)math.clamp(bandCount, 0, RRWConst.kUwMaxBands);
            m_WindowCount = s.N;
            m_SetupP8 = s.SetupP8;
            m_UpFlags = (byte)flags;
            m_Band0 = m_BandCount > 0 ? b0 : default;
            m_Band1 = m_BandCount > 1 ? b1 : default;
            m_Band2 = m_BandCount > 2 ? b2 : default;
            m_Band3 = m_BandCount > 3 ? b3 : default;
            m_WinEnds = s.Ends;
            if (s.N < 8) m_WinEnds &= (1UL << (8 * s.N)) - 1UL;   // unused window slots 0
        }

        // Band laterals beyond this (1/16 m; 256 m from the curve) are garbage: no road is that wide.
        public const short kMaxLateral16 = 256 * 16;

        // Is the tail consistent? Plan kind None: always (it carries nothing). Upgrade: a construction in mode 2, class 1..4,
        // 0..4 bands with Lo < Hi within +-256 m and a window inside the schedule, a valid schedule. Reconstruction (reserved):
        // a construction in mode 2 with a valid schedule. Unknown plan kinds: never.
        public bool TailValid()
        {
            if (m_PlanKind == (byte)PlanKind.None) return true;
            if (m_PlanKind > (byte)PlanKind.Reconstruction) return false;
            if (m_Kind != (byte)WorksKind.Construction || m_Mode != (byte)VisualMode.HalfWidth) return false;
            if (!Schedule.Valid) return false;
            if (m_PlanKind == (byte)PlanKind.Reconstruction) return true;
            if (m_UpgradeClass < (byte)UpgradeClass.Remark || m_UpgradeClass > (byte)UpgradeClass.Mixed) return false;
            if (m_BandCount > RRWConst.kUwMaxBands) return false;
            for (int i = 0; i < m_BandCount; i++)
            {
                var b = Band(i);
                if (b.Lo16 >= b.Hi16 || b.Window >= m_WindowCount) return false;
                if (b.Lo16 < -kMaxLateral16 || b.Hi16 > kMaxLateral16) return false;
                var t = b.Traffic;
                if (t == (BandTraffic)6) return false;
            }
            return true;
        }

        // Hash of the saved upgrade tail without the progress, keyed by project and chain position (stable across save / load;
        // used by the save checks to compare tails before and after a reload).
        public uint TailHash()
        {
            uint h = 2166136261u;
            h = Fnv(h, m_ProjectId);
            h = Fnv(h, (uint)math.asint(m_ChainU0));
            h = Fnv(h, (uint)(m_PlanKind | (m_UpgradeClass << 8) | (m_BandCount << 16) | (m_WindowCount << 24)));
            h = Fnv(h, (uint)(m_SetupP8 | (m_UpFlags << 8)));
            for (int i = 0; i < RRWConst.kUwMaxBands; i++)
            {
                var b = Band(i);
                h = Fnv(h, (uint)(b.KindSide | (b.WinPrim << 8) | (b.G0 << 16)));
                h = Fnv(h, (uint)((ushort)b.Lo16 | ((ushort)b.Hi16 << 16)));
            }
            h = Fnv(h, (uint)m_WinEnds);
            h = Fnv(h, (uint)(m_WinEnds >> 32));
            return h;
        }

        private static uint Fnv(uint h, uint v)
        {
            for (int k = 0; k < 4; k++) { h ^= (v >> (8 * k)) & 0xFF; h *= 16777619u; }
            return h;
        }

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

        // Mode 2 construction: the v1 fields say "finished" (downgrade safety, see the header).
        public bool WritesFinished => m_Kind == (byte)WorksKind.Construction && m_Mode == (byte)VisualMode.HalfWidth;

        // The tail is written (instead of zeros) only for a mode 2 construction with a plan kind.
        public bool WritesTail => WritesFinished && m_PlanKind != (byte)PlanKind.None;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            bool plan = WritesTail;
            writer.Write(kVersion);
            writer.Write(kPayloadV2);
            // ---- v1 (49 bytes); a mode 2 construction says "finished" here (downgrade safety)
            writer.Write(m_Kind);
            writer.Write(m_Mode);
            writer.Write(m_Flags);
            writer.Write(WritesFinished ? m_WorkRequired : m_WorkDone);
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
            // ---- v2 tail (46 bytes; all zero unless WritesTail)
            writer.Write(plan ? m_PlanKind : (byte)0);
            writer.Write(plan ? m_UpgradeClass : (byte)0);
            writer.Write(plan ? m_BandCount : (byte)0);
            writer.Write(plan ? m_WindowCount : (byte)0);
            writer.Write(plan ? m_SetupP8 : (byte)0);
            writer.Write(plan ? m_UpFlags : (byte)0);
            writer.Write(plan ? m_WorkDone : 0u);
            WriteBand(writer, plan ? m_Band0 : default);
            WriteBand(writer, plan ? m_Band1 : default);
            WriteBand(writer, plan ? m_Band2 : default);
            WriteBand(writer, plan ? m_Band3 : default);
            for (int k = 0; k < 8; k++) writer.Write(plan ? WinEnd8(k) : (byte)0);
        }

        private static void WriteBand<TWriter>(TWriter writer, in UpgradeBand b) where TWriter : IWriter
        {
            writer.Write(b.KindSide);
            writer.Write(b.WinPrim);
            writer.Write(b.G0);
            writer.Write(b.Lo16);
            writer.Write(b.Hi16);
        }

        private static UpgradeBand ReadBand<TReader>(TReader reader) where TReader : IReader
        {
            var b = new UpgradeBand();
            reader.Read(out b.KindSide);
            reader.Read(out b.WinPrim);
            reader.Read(out b.G0);
            reader.Read(out b.Lo16);
            reader.Read(out b.Hi16);
            return b;
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
                ClearTail();
                m_LoadNote = kNoteNone;
                int consumed = kPayloadV1;
                uint tailWorkDone = 0;
                if (newer && size >= kPayloadV2)
                {
                    reader.Read(out m_PlanKind);
                    reader.Read(out m_UpgradeClass);
                    reader.Read(out m_BandCount);
                    reader.Read(out m_WindowCount);
                    reader.Read(out m_SetupP8);
                    reader.Read(out m_UpFlags);
                    reader.Read(out tailWorkDone);
                    m_Band0 = ReadBand(reader);
                    m_Band1 = ReadBand(reader);
                    m_Band2 = ReadBand(reader);
                    m_Band3 = ReadBand(reader);
                    ulong ends = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        reader.Read(out byte e);
                        ends |= (ulong)e << (8 * k);
                    }
                    m_WinEnds = ends;
                    consumed = kPayloadV2;
                }
                for (int i = consumed; i < size; i++) reader.Read(out byte _);   // newer appended fields
                AfterRead(tailWorkDone);
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

        // The rules after reading the v1 fields and the tail (if any). Pure; public for tests.
        public void AfterRead(uint tailWorkDone)
        {
            if (m_PlanKind != (byte)PlanKind.None)
            {
                if (TailValid())
                {
                    m_WorkDone = math.min(tailWorkDone, m_WorkRequired);   // the real progress (the v1 field says finished)
                    if (m_PlanKind == (byte)PlanKind.Upgrade)
                        for (int i = m_BandCount; i < RRWConst.kUwMaxBands; i++) SetBand(i, default);
                    if (m_WindowCount < 8) m_WinEnds &= (1UL << (8 * m_WindowCount)) - 1UL;   // unused window slots 0
                    return;
                }
                m_LoadNote = kNoteBadTail;                        // keep the v1 view: finished
            }
            ClearTail();                                          // no plan kind: no tail bytes either
            if (m_Mode == (byte)VisualMode.HalfWidth)
            {
                m_Mode = (byte)VisualMode.Minimal;
                if (m_LoadNote == kNoteNone) m_LoadNote = kNoteHalfWidthNoPlan;
            }
        }
    }
}
