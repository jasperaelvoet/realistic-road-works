using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// Surfaces module state: clone specs + registered clone entities, tracked areas per (edge, layer, band, piece)
// (a layer is a union of per-crew-section pieces, one area per edge-local piece),
// node caps, scars (fade list: construction verges ~1 h, demolition topsoil 12 h; no fresh-asphalt curing), retiring
// areas and the pending CreationDefinition bindings.
// Everything here is module-static, main-thread only, and cleared on game preload (areas are never saved).
namespace RealisticRoadWorks.V3.Surfaces
{
    // One RRW SurfacePrefab clone to register in the main menu.
    internal sealed class SurfaceCloneSpec
    {
        public string Name;              // prefab name ("RRW ...", PrefabNames)
        public string Source;            // vanilla SurfacePrefab whose RenderedArea is copied
        public string BaseTex, NormalTex, MaskTex; // TextureAsset name prefixes (null = keep the source's textures/material)
        public DecalLayers Layers;
        public int Priority;
        public float Smoothness;         // material gloss (RenderedArea.m_Smoothness)
        public float3 Tint = new float3(1f);
        public float Alpha = 1f;         // ABSOLUTE alpha of this variant (base alpha x fade percentage)
        public SurfaceLayer Layer;
        public int FadePct = 100;        // 100 / 50 / 20 (PrefabNames.Alpha)
        public bool DevOnly;             // "RRW Topsoil Agri", the rrw.surf.line test clones
        public bool RaisedQueue;         // the two Cover layers + TempMarking variants + test lines (SurfaceMaterialSystem)
        // ---- runtime style of the line clones (experimental switches)
        public float Roundness = -1f;    // RenderedArea.m_Roundness (-1 = keep the source's; the area grows by clamp(r,.01,.99)*0.375 m per side)
        public float2 EdgeNoise = new float2(float.NaN);   // RenderedArea.m_EdgeNoise (NaN = keep the source's)
        public float LodBias = float.NaN;// RenderedArea.m_LodBias (NaN = keep); TempMarking: RRWGates.TempLineLodBias ? +1 : 0
        public int TempSource = -1;      // TempMarking variant index (0 = Y1 Concrete, 1 = Y2 Sand, 2 = Y3 Pavement), -1 = not a variant
        public bool TestLine;            // rrw.surf.line test clone (DEVTOOLS): queue / prio / roundness overridable per call
        public int QueueRaise = RRWConst.kRenderQueueRaise;   // default raise of a RaisedQueue clone (covers +100; TempMarking: RRWGates.TempLineQueueRaise)
    }

    // A registered clone.
    internal sealed class SurfaceClone
    {
        public SurfaceCloneSpec Spec;
        public SurfacePrefab Prefab;
        public Entity Entity;
        public string TexMode;
        // Runtime style (SurfaceStyle.Sync): what the prefab entity / RenderedArea currently carry
        public float AppliedRoundness = float.NaN;
        public float AppliedLodBias = float.NaN;
        public string SourceStyle = "";  // source RenderedArea values at registration (roundness / edge noise / fade), for rrw.surf.list
    }

    // One area decal we own: a strip of one (edge, layer, band), a node cap, or a scar.
    internal sealed class TrackedArea
    {
        public Entity Area;              // live area entity (Null until bound at Mod4)
        public Entity Def;               // our CreationDefinition while pending (Null once bound / dropped)
        public uint DefUpdate;           // RRWClock.UpdateIndex the definition was made
        public Entity Prefab;
        public SurfaceLayer Layer;
        public int Band;                 // band slot (strips), TempLine row index (yellow lines)
        public SurfaceBand BandKind;     // band kind the polygon was built for (C3 Carriageway -> C4 CarriageHalf switch)
        public int Key = -1;             // yellow line piece key: -1 solid line, k = dash index in chain u (TempLines)
        public int FadePct = 100;
        public Entity Site;              // RRWDerived.m_Site (Null for scars)
        public uint ProjectId;
        public DerivedGroup Group = DerivedGroup.Area;
        public float LS0, LS1;           // LOGICAL edge-local span this polygon represents (ToEdgeLocal output, before trims)
        public float GS0, GS1;           // geometric s range actually drawn (after junction trims / extensions)
        public uint WriteUpdate;         // update of the last spawn / rewrite
        public uint BoundUpdate;         // update the area entity was bound (seen Created at Mod4)
        public float FloorAtWrite;       // ground floor metric at the last write (re-snap trigger)
        public uint StepAtWrite;         // RoadWorksGround.m_StepUpdate at the last write
        public uint ResnapDue;           // update at which the same span is rewritten (0 = none)
        public int GeomRev = int.MinValue;
        public uint EndsHash;            // node-end classification hash at the last write
        public float2 FirstXZ;           // first polygon node (binding key at Mod4)
        // Texture anchor: the game maps an area's texture from its first node and the direction to its second, so every write
        // starts the polygon at this fixed boundary point (SurfaceAreaSystem.AnchorPoly) and the texture never slides.
        public bool HasAnchor;
        public float2 AnchorXZ, AnchorDir;
        public readonly List<float2> PrevPoly = new List<float2>(16);
        public int NodeCount;
        public uint DeferSince;          // first update a shrink was deferred by the hand-over rule (0 = not deferred)
        public float PrevLS0, PrevLS1;   // logical span before the last rewrite (what was on screen before this update)
        public bool HasPrev;
        public int Piece;                // piece index on its (edge, layer, band) row (sorted by LS0)
        public uint OutgoingSince;       // update it was replaced by a piece-topology change (0 = current piece)
        public float LatLo = float.NaN;  // EDGE-frame laterals of the last write (upgrade works: a sub-strip that moved sideways
        public float LatHi = float.NaN;  // is rewritten; hand-overs between overlapping sub-strips use them)
        public bool SnapChecked;         // upgrade works: the one re-snap after the first appearance was scheduled

        public bool Pending => Area == Entity.Null && Def != Entity.Null;

        public bool Live(EntityManager em) => Area != Entity.Null && em.Exists(Area) && !em.HasComponent<Deleted>(Area);

        // Live and rendered for at least one full update (hand-over rule: a layer only disappears under another layer
        // that has been present for >= 1 frame).
        public bool Settled(EntityManager em, uint now) => Live(em) && BoundUpdate < now && WriteUpdate < now;

        // Coverage that has been on screen for >= 1 update: the current span, or the previous one if rewritten this
        // update (coverers only grow while they cover a hand-over, so the previous span is the conservative choice).
        public bool SettledSpan(EntityManager em, uint now, out float s0, out float s1)
        {
            s0 = LS0; s1 = LS1;
            if (!Live(em) || BoundUpdate >= now) return false;
            if (WriteUpdate < now) return true;
            if (!HasPrev) return false;
            s0 = PrevLS0; s1 = PrevLS1;
            return true;
        }
    }

    // Per works edge: tracked strips + junction-end classification.
    internal sealed class EdgeSurf
    {
        public Entity Edge;
        public uint ProjectId;
        public float ChainU0, ChainU1;   // last seen (split / combine matching)
        public float Length;             // last seen arc length (re-keying logical spans after a split)
        // [layer, band slot, piece]. A row (layer, band) holds its pieces compact and sorted by LS0 (slots
        // 0..n-1); a piece-count / band-kind change rewrites the whole row in one update (new areas first, the replaced ones wait
        // in Outgoing until the row and the covering layer have been on screen for an update).
        // Upgrade works draw one polygon per sub-strip: their edges grow to kMaxSlots band slots (EnsureSlots), slot
        // = band index * kSubStripsPerBand + sub-strip index, so a sub-strip keeps its row while other bands come and go.
        public const int kMaxPieces = SpanSet.Capacity;
        public const int kSubStripsPerBand = 8;
        public const int kMaxSlots = RRWConst.kUwMaxBands * kSubStripsPerBand;
        public TrackedArea[,,] Areas { get; private set; } = new TrackedArea[(int)SurfaceLayer.Count, EdgeSection.kMaxIntervals, kMaxPieces];
        public readonly List<TrackedArea> Outgoing = new List<TrackedArea>(4);
        // high-water mark of used piece slots per row (rows are only scanned up to it; a row diff compacts and resets it)
        public byte[,] RowN { get; private set; } = new byte[(int)SurfaceLayer.Count, EdgeSection.kMaxIntervals];

        public int Slots => Areas.GetLength(1);

        // Grows the band-slot dimension (upgrade works); tracked rows are kept.
        public void EnsureSlots(int slots)
        {
            if (slots <= Slots) return;
            var a = new TrackedArea[(int)SurfaceLayer.Count, slots, kMaxPieces];
            var r = new byte[(int)SurfaceLayer.Count, slots];
            for (int l = 0; l < Areas.GetLength(0); l++)
                for (int b = 0; b < Areas.GetLength(1); b++)
                {
                    r[l, b] = RowN[l, b];
                    for (int k = 0; k < kMaxPieces; k++) a[l, b, k] = Areas[l, b, k];
                }
            Areas = a;
            RowN = r;
        }

        public void Put(int layer, int band, int piece, TrackedArea t)
        {
            Areas[layer, band, piece] = t;
            if (t != null && piece >= RowN[layer, band]) RowN[layer, band] = (byte)(piece + 1);
        }
        public bool Completed;           // turned into scars at Completing: never respawn
        public bool EndsUpgrade;         // the ends were classified for upgrade works (dead ends clipped, no round cap)
        public uint SeenUpdate;
        public int GeomRev = int.MinValue;
        public EdgeEnds Ends;
        public bool EndsValid;
        public uint EndsUpdate;          // last classification refresh
        public int EndsRegistryRev = int.MinValue;
        public uint CornerHash;          // EdgeGeometry corner hash (trims are re-projected when it changes)
        // split hand-over: shrinking writes wait until the siblings' areas are live
        public readonly List<Entity> SplitSiblings = new List<Entity>(2);
        public uint SplitSince;
        public bool RebuildDone;         // NeedsRebuild handled for the current flag period
        // Yellow temporary lines have their own store (TempLineCount can exceed kMaxIntervals; dashes are many areas)
        public readonly List<TempRow> TempRows = new List<TempRow>(2);
        public RoadZones TempOpenHalf;   // chain half the rows describe (None = no rows)
        public string TempReason = "";   // why the edge has no lines (dev dump)

        public float ChainLo => math.min(ChainU0, ChainU1);
        public float ChainHi => math.max(ChainU0, ChainU1);

        public int CountTracked()
        {
            int n = Outgoing.Count;
            for (int l = 0; l < Areas.GetLength(0); l++)
                for (int b = 0; b < Areas.GetLength(1); b++)
                    for (int k = 0; k < RowN[l, b]; k++)
                        if (Areas[l, b, k] != null) n++;
            for (int i = 0; i < TempRows.Count; i++) n += TempRows[i].Pieces.Count;
            return n;
        }

        // Tracked pieces of one (layer, band) row (non-null slots).
        public int PieceCount(int layer, int band)
        {
            int n = 0;
            for (int k = 0; k < RowN[layer, band]; k++) if (Areas[layer, band, k] != null) n++;
            return n;
        }

        public int TempPieceCount()
        {
            int n = 0;
            for (int i = 0; i < TempRows.Count; i++) n += TempRows[i].Pieces.Count;
            return n;
        }
    }

    // One yellow temporary line of an edge (EdgeSection.TempLine i). A solid line is one area;
    // a dashed lane line is one area per dash (kTempDash / kTempGap in chain u, anchored at the project's Trim0 so the pattern
    // runs on across nodes). A row is always written complete in one update (all its pieces), counted once against the cap.
    internal sealed class TempRow
    {
        public int Line;                 // TempLine index i (0 divider side, 1 outer edge, >= 2 dashed lane lines)
        public bool Dashed;
        public uint Sig;                 // laterals + geometry + junction ends of the last write (a change rewrites every piece)
        public float Left, Right;        // edge-frame laterals of the last write
        public readonly List<TrackedArea> Pieces = new List<TrackedArea>(4);   // TrackedArea.Key = -1 (solid) / dash index
    }

    // rrw.surf.line (DEVTOOLS): one test line request (queued by the dev command, spawned at Mod1) and its areas.
    internal sealed class DevLineRequest
    {
        public Entity Edge;
        public int Kind;                 // 0..2 = Y1..Y3, 3 = BLK
        public float Lat, Width, T0, T1;
        public float Dash, Gap;          // 0 = solid
        public float Round = float.NaN;  // NaN = the product default (RRWGates.TempLineRoundness)
        public int Queue = int.MinValue; // queue raise override of the test clone (int.MinValue = keep)
        public int Prio = int.MinValue;  // renderer priority override of the test clone
        public string Tag;
    }

    internal sealed class DevLine
    {
        public string Tag;
        public string Clone;
        public Entity Edge;
        public string What;
        public readonly List<TrackedArea> Pieces = new List<TrackedArea>(8);
    }

    // Per project: write throttle + phase memory (phase-change bypass).
    internal sealed class ProjSurf
    {
        public uint LastWriteUpdate;
        public bool HasWritten;
        public WorksPhase LastPhase = WorksPhase.None;
        public WorksKind LastKind;
        public uint PendingSince;        // first update a needed write was deferred by the global cap (0 = none)
        public uint SeenUpdate;
        public uint PhaseSince;          // update the current phase was first seen (rrw.surf.check grace)
        // Log state (one Info line per change and project)
        public bool HalvesShown;         // C4 Fresh Asphalt (Cover) drawn per half
        public int HalvesStage = -1;     // stage index (C4a 0 / C4b 1) of the last per-half log line
        public bool LinesShown;          // yellow temporary lines on at least one edge
        public string LinesWhy = "";
        // Bypass work (phase change, ModelReset, NeedsRebuild, rrw.surf.rebuild) is spread over
        // updates per PROJECT; a deferred project keeps its one-frame flags latched and is processed whole in a later update
        public bool ResetLatched;        // ModelReset seen while the project was deferred
        public bool RebuildLatched;      // NeedsRebuild seen while deferred
        public bool DevRebuildLatched;   // rrw.surf.rebuild requested while deferred
        public uint BypassWaitSince;     // first update the project's bypass work was deferred (0 = none)
        public int LastCrews = -1;       // crew layout of the last crew-layout log line
        public int MaxPieces;            // most pieces on one (edge, layer, band) row in the last processed update
        // Upgrade works log state (one Info line when the window, primitive or sub-strip covers change)
        public uint UpgradeSig;
        public int UpgradeWindow = int.MinValue;   // window of the last update (a switch is written like a phase change)
        public bool UpgradeWaiting;      // an edge's band data was not current in the last update (logged once per wait)
    }

    // Node cap at a junction whose every edge is a works edge:
    // Base Course Cover -> Fresh Asphalt Cover (+ Fresh Asphalt under it) -> both deleted together once every
    // painter has passed (no curing asphalt on the junction).
    internal sealed class NodeCap
    {
        public Entity Node;
        public int Stage;                // 0 none, 1 base cover, 2 asphalt cover (+ fresh asphalt); no curing stage 3
        public TrackedArea Cover;        // BaseCourseCover (stage 1) or FreshAsphaltCover (stage 2)
        public TrackedArea Asphalt;      // FreshAsphalt (normal queue, stages 2-3)
        public uint SeenUpdate;
        public uint PolyHash;
        public bool RebuildDone;
    }

    // What a curing / scar decal lies on. A construction's curing layers (fresh asphalt, brown verges) belong to the finished
    // road they were painted on: they must go when that road is worked on again, deleted or replaced. The
    // demolition topsoil scar has no anchor: it is meant to outlive its (deleted) edge.
    internal sealed class ScarAnchor
    {
        public Entity Entity;            // edge (strips) or junction node (node-cap asphalt)
        public bool IsNode;
        public Colossal.Mathematics.Bezier4x3 Curve;   // edge curve at anchoring time (split remnants / in-place replacement)
        public Entity Prefab;            // edge PrefabRef at anchoring time (road type replaced in place)
        public EdgeArc Arc;              // lazily built from Curve when the edge is lost (remnant search)
    }

    // Fading decal after the works (Completing -> scar list; construction = verge soil only, gone in
    // kVergeEndHours; demolition topsoil scar 12 h). Survives the site; not saved.
    internal sealed class ScarEntry
    {
        public TrackedArea Area;         // current variant
        public TrackedArea Next;         // next fade variant while it is being spawned (spawn-first swap)
        public bool Construction;
        public double AgeFrames;         // simulation frames (x RRWDebug.WorkTimeScale for dev time-lapse)
        public int Stage;                // index into SurfacePalette.FadeLadder(layer): 0 = a100, then lighter steps
        // ---- conflict tracking (curing / scars give way to new works)
        public readonly List<ScarAnchor> Anchors = new List<ScarAnchor>(1);
        public bool Anchored;            // had anchors at creation: losing all of them removes the scar
        public readonly List<float2> PolyXZ = new List<float2>(64);   // polygon at creation (geometric overlap with new works)
        public float4 Bounds;            // xz min (xy) / max (zw) of PolyXZ
        public int ConflictRev = int.MinValue;   // SiteRegistry.Revision of the last geometric scan
        public readonly List<Entity> Conflicts = new List<Entity>(1);   // other registry edges whose footprint overlaps it
        public uint WaitSince;           // first update a hand-over to a demolition was waited for (0 = none)
        public uint CreatedUpdate;
        // Upgrade works on the road under the scar: the scar is clipped to what lies outside the works bands, once per
        // (edge, band layout); ClipSigs[i] is the layout signature ClipEdges[i] was clipped against
        public readonly List<Entity> ClipEdges = new List<Entity>(1);
        public readonly List<uint> ClipSigs = new List<uint>(1);
    }

    // An area that is being replaced: deleted once its successor has been live for >= 1 update (or after a timeout).
    internal sealed class RetireEntry
    {
        public Entity Area;
        public TrackedArea Successor;    // null = delete after MinUpdates
        public uint Since;
        public int MinUpdates;
    }

    internal enum EndKind : byte
    {
        Visible = 0,         // node with any non-works / visible edge: clip to the edge geometry trims
        Interior = 1,        // degree 2, both mode-A works edges: mitred cut at the node centre
        DeadEnd = 2,         // degree 1: round cap over the cul-de-sac
        WorksJunction = 3,   // degree >= 3, all mode-A works edges: node cap; terrain layers to the centre
        OpenDeadEnd = 4,     // degree 1 on a road that stays visible (upgrade works): clip to the edge geometry like a visible
                             // junction, no round cap over the cul-de-sac (the node's road surface is never dressed)
    }

    internal struct EdgeEnds
    {
        public EndKind Start, End;
        public float3 CutStart, CutEnd;  // mitre line directions (xz) for Interior ends, zero otherwise
        public float TrimStart, TrimEnd; // edge geometry trims (arc metres) measured by Surfaces itself
        public uint Hash;
    }

    internal static class SurfaceState
    {
        // ------------------------------------------------------------------ clones (process-wide)

        public static readonly List<SurfaceCloneSpec> Specs = BuildSpecs();
        public static readonly Dictionary<string, SurfaceClone> Clones = new Dictionary<string, SurfaceClone>(StringComparer.Ordinal);
        public static readonly HashSet<Entity> CloneEntities = new HashSet<Entity>();
        public static readonly Dictionary<Entity, SurfaceClone> ByEntity = new Dictionary<Entity, SurfaceClone>();
        public static readonly List<Colossal.IO.AssetDatabase.TextureAsset> HeldTextures = new List<Colossal.IO.AssetDatabase.TextureAsset>();
        public static bool Registered;
        public static string RegistrationInfo = "not yet";

        // ------------------------------------------------------------------ dev switches (always compiled; release never changes them)

        public static bool DevAgri;            // rrw.surf.agri: topsoil from "RRW Topsoil Agri"
        public static bool FadeBySwap;         // rrw.surf.fade swap: alpha steps swap PrefabRef + Updated (experimental)
        public static double DevScarAgeBonus;  // rrw.surf.age: hours added to every scar once
        public static bool DevRebuildAll;      // rrw.surf.rebuild: replace every strip and cap once (spawn first, retire old)
        // rrw.surf.line: requests queued by the dev command, test lines alive, per-test-clone overrides
        public static readonly List<DevLineRequest> DevLineRequests = new List<DevLineRequest>();
        public static readonly List<DevLine> DevLines = new List<DevLine>();
        public static string DevLineClear;     // non-null: delete the dev lines with this tag ("" = all) next update
        public static readonly Dictionary<string, int> DevQueue = new Dictionary<string, int>(StringComparer.Ordinal);
        public static readonly Dictionary<string, int> DevPrio = new Dictionary<string, int>(StringComparer.Ordinal);
        public static readonly Dictionary<string, float> DevRound = new Dictionary<string, float>(StringComparer.Ordinal);

        // ------------------------------------------------------------------ runtime (cleared on preload)

        public static readonly Dictionary<Entity, EdgeSurf> Edges = new Dictionary<Entity, EdgeSurf>();
        public static readonly Dictionary<uint, ProjSurf> Projects = new Dictionary<uint, ProjSurf>();
        public static readonly Dictionary<Entity, NodeCap> Caps = new Dictionary<Entity, NodeCap>();
        public static readonly List<ScarEntry> Scars = new List<ScarEntry>();
        public static readonly List<RetireEntry> Retiring = new List<RetireEntry>();
        public static readonly List<TrackedArea> Pending = new List<TrackedArea>();
        public static bool SurfacesWereOn = true;
        public static bool TagFaulted;          // SurfaceTagSystem disabled itself: never spawn areas that could not get LivePath

        // stats (dev)
        public static int Spawns, Rewrites, Deletes, Deferred, CapSpawns, ScarSteps, Unmatched, Bound;
        // Completion stats: frame-N removals of road layers (fresh asphalt / covers / temp marking, never kept
        // as curing), verge scars started, verge scars deleted at kVergeEndHours, stray curing layers removed defensively.
        public static int CompletedEdges, CompletedRoadLayers, CompletedVergeScars, VergeScarsEnded, StrayCuringRemoved;
        public static string LastCompletion = "none";
        public static int ScarsDropped, ScarsRekeyed;   // curing / scars removed for new works / lost roads, re-keyed to split remnants
        public static string LastScarDrop = "none";
        // Staged-traffic stats: C3 -> C4 band switches (per-half covers), yellow line rows / pieces written, style resyncs
        public static int BandSwitches, TempRowWrites, TempPieceSpawns, TempPieceRewrites, TempPieceDeletes, StyleSyncs;
        public static string LastLines = "none", LastHalves = "none", LastStyle = "none";
        // Piece stats: piece-topology changes (row rewritten in one update), rows grown in place during one,
        // replaced areas deleted after the hand-over / by timeout, projects whose bypass work was deferred to a later update
        public static int TopologyChanges, TopologyKept, OutgoingDeleted, OutgoingTimeouts, BypassDeferred, MaxPiecesSeen;
        public static string LastTopology = "none", LastCrews = "none";
        // Upgrade works: sub-strip polygons wanted by the last upgrade project processed, scars clipped to the outside of the
        // bands / checked and left whole / removed because nothing was left outside, edge updates that waited for band data
        public static int UpgradePolygons, ScarsClipped, ScarsKeptOutside, ScarsInsideBands, UpgradeWaits;
        public static string LastUpgrade = "none", LastScarClip = "none";
        // Self-heal: live work-site areas that nothing tracked any more, removed by the orphan sweep
        public static int OrphansRemoved;
        public static string LastOrphan = "none";

        public static void ClearRuntime(string why)
        {
            int n = 0;
            foreach (var e in Edges.Values) n += e.CountTracked();
            if (n > 0 || Scars.Count > 0 || Caps.Count > 0)
                RRWLog.Info("surfaces: state cleared (" + why + "): " + n + " strips, " + Caps.Count + " caps, " + Scars.Count + " scars");
            Edges.Clear();
            Projects.Clear();
            Caps.Clear();
            Scars.Clear();
            Retiring.Clear();
            Pending.Clear();
            DevLines.Clear();
            DevLineRequests.Clear();
            SurfacesWereOn = true;
        }

        // ------------------------------------------------------------------ prefab lookup

        // Cached (layer, fade) -> prefab entity; rebuilt when clones are added or the dev topsoil switch flips.
        // Fade slots are SurfacePalette.AllFadePcts (a layer only has the variants of its own ladder; others resolve Null).
        private static readonly Entity[,] s_PrefabCache = new Entity[(int)SurfaceLayer.Count, SurfacePalette.AllFadePcts.Length];
        private static int s_CacheClones = -1;
        private static bool s_CacheAgri;
        private static int s_CacheLineSrc = -1;

        public static Entity Prefab(SurfaceLayer layer, int fadePct)
        {
            if (s_CacheClones != Clones.Count || s_CacheAgri != DevAgri || s_CacheLineSrc != RRWGates.TempLineSource)
            {
                s_CacheLineSrc = RRWGates.TempLineSource;
                for (int l = 0; l < (int)SurfaceLayer.Count; l++)
                    for (int k = 0; k < SurfacePalette.AllFadePcts.Length; k++)
                        s_PrefabCache[l, k] = Resolve((SurfaceLayer)l, SurfacePalette.AllFadePcts[k]);
                s_CacheClones = Clones.Count;
                s_CacheAgri = DevAgri;
            }
            int li = (int)layer;
            if (li < 0 || li >= (int)SurfaceLayer.Count) return Entity.Null;
            return s_PrefabCache[li, SurfacePalette.SlotOf(fadePct)];
        }

        private static Entity Resolve(SurfaceLayer layer, int fadePct)
        {
            string name;
            if (layer == SurfaceLayer.TopsoilStrip && DevAgri && fadePct >= 100) name = PrefabNames.TopsoilAgriDev;
            else if (layer == SurfaceLayer.TempMarking)
            {
                // The variant RRWGates.TempLineSource selects; Y1 when that one is not registered
                name = PrefabNames.TempMarkingVariants[TempLineSourceIndex];
                if (!Clones.TryGetValue(name, out var v) || v.Entity == Entity.Null) name = PrefabNames.TempMarking;
            }
            else name = PrefabNames.Alpha(PrefabNames.Surface(layer), PrefabNames.HasAlphaVariants(layer) ? fadePct : 100);
            if (Clones.TryGetValue(name, out var c) && c.Entity != Entity.Null) return c.Entity;
            if (layer == SurfaceLayer.TopsoilStrip && DevAgri && Clones.TryGetValue(PrefabNames.TopsoilStrip, out c)) return c.Entity;
            return Entity.Null;
        }

        public static int TempLineSourceIndex => math.clamp(RRWGates.TempLineSource, 0, PrefabNames.TempMarkingVariants.Length - 1);

        public static bool IsOurPrefab(Entity prefab) => CloneEntities.Contains(prefab);

        // Render-queue raise of a RaisedQueue clone: covers +kRenderQueueRaise (100), the TempMarking variants
        // +RRWGates.TempLineQueueRaise (200 = queue 2200), test lines their per-clone override (rrw.surf.line queue=).
        // SurfaceBatch.ClampQueue caps the result at 2500 (HDRP decal range).
        public static int QueueRaiseOf(SurfaceCloneSpec s)
        {
            if (s.TestLine && DevQueue.TryGetValue(s.Name, out int q)) return q;
            if (s.TempSource >= 0) return RRWGates.TempLineQueueRaise;
            return s.QueueRaise;
        }

        // Renderer priority wanted at runtime (int.MinValue = the registered one): rrw.surf.line prio= on a test clone.
        public static int PriorityOverrideOf(SurfaceCloneSpec s) =>
            s.TestLine && DevPrio.TryGetValue(s.Name, out int p) ? p : int.MinValue;

        // Roundness wanted at runtime (NaN = not managed): TempMarking variants follow RRWGates.TempLineRoundness, test lines
        // their per-call override or their registered value.
        public static float RoundnessOf(SurfaceCloneSpec s)
        {
            if (s.TempSource >= 0) return RRWGates.TempLineRoundness;
            if (s.TestLine) return DevRound.TryGetValue(s.Name, out float r) ? r : s.Roundness;
            return float.NaN;
        }

        public static float LodBiasOf(SurfaceCloneSpec s) =>
            s.TempSource >= 0 || s.TestLine ? (RRWGates.TempLineLodBias ? 1f : 0f) : float.NaN;

        public static string NameOf(Entity prefab) => ByEntity.TryGetValue(prefab, out var c) ? c.Spec.Name : "?";

        // ------------------------------------------------------------------ palette (values in SurfacePalette)

        public const float kAsphaltSmoothness = SurfacePalette.kAsphaltSmoothness;
        public const float kTopsoilSmoothness = SurfacePalette.kTopsoilSmoothness;
        public const float kSubgradeSmoothness = SurfacePalette.kSubgradeSmoothness;
        public const float kBaseSmoothness = SurfacePalette.kBaseSmoothness;

        // (BuildSpecs reads RRWConst directly: it runs from the FIRST static field initializer of this class, so a static field
        //  declared further down would still hold its default value there)

        private static List<SurfaceCloneSpec> BuildSpecs()
        {
            var list = new List<SurfaceCloneSpec>(24);
            float3 topTint = RRWConst.kTopsoilUseAgriculture ? RRWConst.kAgricultureTopsoilTint : SurfacePalette.TopsoilTint;
            string topSrc = RRWConst.kTopsoilUseAgriculture ? PrefabNames.SrcAgriculture : PrefabNames.SrcOre;
            foreach (int pct in SurfacePalette.SoilFadeLadder)
            {
                list.Add(new SurfaceCloneSpec
                {
                    Name = PrefabNames.Alpha(PrefabNames.TopsoilStrip, pct), Source = topSrc,
                    Layers = DecalLayers.Terrain, Priority = -94, Smoothness = kTopsoilSmoothness, Tint = topTint,
                    Alpha = SurfacePalette.TopsoilAlpha * pct / 100f, Layer = SurfaceLayer.TopsoilStrip, FadePct = pct,
                });
            }
            foreach (int pct in SurfacePalette.SoilFadeLadder)
            {
                list.Add(new SurfaceCloneSpec
                {
                    Name = PrefabNames.Alpha(PrefabNames.Subgrade, pct), Source = PrefabNames.SrcOre,
                    Layers = DecalLayers.Terrain, Priority = -93, Smoothness = kSubgradeSmoothness, Tint = SurfacePalette.SubgradeTint,
                    Alpha = SurfacePalette.SubgradeAlpha * pct / 100f, Layer = SurfaceLayer.Subgrade, FadePct = pct,
                });
            }
            list.Add(new SurfaceCloneSpec
            {
                Name = PrefabNames.BaseCourseCover, Source = PrefabNames.SrcSand,
                BaseTex = PrefabNames.TexGravelBase, NormalTex = PrefabNames.TexGravelNormal, MaskTex = PrefabNames.TexGravelMask,
                Layers = DecalLayers.Terrain | DecalLayers.Roads, Priority = -91, Smoothness = kBaseSmoothness,
                Layer = SurfaceLayer.BaseCourseCover, RaisedQueue = true,
            });
            float3 asph = SurfacePalette.AsphaltTint;
            foreach (int pct in SurfacePalette.AsphaltFadeLadder)
            {
                list.Add(new SurfaceCloneSpec
                {
                    Name = PrefabNames.Alpha(PrefabNames.FreshAsphalt, pct), Source = PrefabNames.SrcConcrete,
                    BaseTex = PrefabNames.TexAsphaltBase, NormalTex = PrefabNames.TexAsphaltNormal, MaskTex = PrefabNames.TexAsphaltMask,
                    Layers = DecalLayers.Terrain | DecalLayers.Roads, Priority = -90, Smoothness = kAsphaltSmoothness, Tint = asph,
                    Alpha = pct / 100f, Layer = SurfaceLayer.FreshAsphalt, FadePct = pct,
                });
            }
            list.Add(new SurfaceCloneSpec
            {
                Name = PrefabNames.FreshAsphaltCover, Source = PrefabNames.SrcConcrete,
                BaseTex = PrefabNames.TexAsphaltBase, NormalTex = PrefabNames.TexAsphaltNormal, MaskTex = PrefabNames.TexAsphaltMask,
                Layers = DecalLayers.Terrain | DecalLayers.Roads, Priority = -89, Smoothness = kAsphaltSmoothness, Tint = asph,
                Layer = SurfaceLayer.FreshAsphaltCover, RaisedQueue = true,
            });
            // Upgrade works, the dug strip on a visible road: the verified subgrade dirt (Ore material untouched, subgrade tint) on
            // Terrain|Roads at the raised queue, so it also covers the road surface and its markings. Prio -92 sits between the
            // subgrade and the base course. No alpha variants (never a scar).
            list.Add(new SurfaceCloneSpec
            {
                Name = PrefabNames.RoadDirt, Source = PrefabNames.SrcOre,
                Layers = DecalLayers.Terrain | DecalLayers.Roads, Priority = -92, Smoothness = kSubgradeSmoothness, Tint = SurfacePalette.SubgradeTint,
                Alpha = SurfacePalette.SubgradeAlpha, Layer = SurfaceLayer.RoadDirt, RaisedQueue = true,
            });
            // Upgrade works, the old road that still carries traffic: the road asphalt texture worn grey and matte, over the new
            // road's markings (Terrain|Roads, raised queue, the cover's priority). The fresh asphalt cover takes over where a crew paved.
            list.Add(new SurfaceCloneSpec
            {
                Name = PrefabNames.OldAsphaltCover, Source = PrefabNames.SrcConcrete,
                BaseTex = PrefabNames.TexAsphaltBase, NormalTex = PrefabNames.TexAsphaltNormal, MaskTex = PrefabNames.TexAsphaltMask,
                Layers = DecalLayers.Terrain | DecalLayers.Roads, Priority = -89, Smoothness = SurfacePalette.kOldAsphaltSmoothness,
                Tint = SurfacePalette.OldAsphaltCoverTint, Layer = SurfaceLayer.OldAsphaltCover, RaisedQueue = true,
                QueueRaise = RRWConst.kRenderQueueRaise - 1,   // under the fresh asphalt cover where both lie (a new strip on the half painted)
            });
            // Upgrade works, a strip that is not dug yet (the new road already lies there): the ground it was, grass, on
            // Terrain|Roads at the raised queue so it also hides the new road surface, its kerb and its markings.
            list.Add(new SurfaceCloneSpec
            {
                Name = PrefabNames.GroundCover, Source = PrefabNames.SrcGrass,
                Layers = DecalLayers.Terrain | DecalLayers.Roads, Priority = -92, Smoothness = kTopsoilSmoothness,
                Layer = SurfaceLayer.GroundCover, RaisedQueue = true,
            });
            // Upgrade works, the removed strip outside the narrowed road before it is broken up: the road asphalt texture
            // worn grey and matte. Terrain only and the normal queue (it lies beside the road, never on it). Only drawn while
            // RRWGates.UpgradeOldAsphalt is on; Base Course Cover takes its place otherwise.
            list.Add(new SurfaceCloneSpec
            {
                Name = PrefabNames.OldAsphalt, Source = PrefabNames.SrcConcrete,
                BaseTex = PrefabNames.TexAsphaltBase, NormalTex = PrefabNames.TexAsphaltNormal, MaskTex = PrefabNames.TexAsphaltMask,
                Layers = DecalLayers.Terrain, Priority = -92, Smoothness = SurfacePalette.kOldAsphaltSmoothness, Tint = SurfacePalette.OldAsphaltTint,
                Layer = SurfaceLayer.OldAsphalt,
            });
            // Staged traffic: yellow temporary lane lines on the half that is open to
            // traffic in C4a. Light, uniform source with its own material (no texture swap) tinted works yellow; Roads layer only
            // (only ever drawn on the visible carriageway); roundness 0.01 (seen in game: the 0.5 default grew a 0.15 m line to ~0.53 m and
            // blurred it), queue 2000 + RRWGates.TempLineQueueRaise (2200: over the markings and both covers at 2100), prio -87
            // (-88 is reserved for a future MarkingBlackout layer); no alpha variants (never a scar). Three source variants
            // (RRWGates.TempLineSource): Y1 Concrete (kTempMarkingTint), Y2 Sand, Y3 Pavement. Roundness / LOD bias / queue follow
            // the experimental switches at runtime (SurfaceStyle, SurfaceMaterialSystem).
            if (SurfacePalette.kTempMarkingOn)
            {
                string[] src = { PrefabNames.SrcConcrete, PrefabNames.SrcSand, PrefabNames.SrcPavement };
                float3[] tint = { SurfacePalette.TempMarkingTint, SurfacePalette.TempMarkingTintY2, SurfacePalette.TempMarkingTintY3 };
                for (int k = 0; k < PrefabNames.TempMarkingVariants.Length && k < src.Length; k++)
                    list.Add(new SurfaceCloneSpec
                    {
                        Name = PrefabNames.TempMarkingVariants[k], Source = src[k],
                        Layers = DecalLayers.Roads, Priority = RRWConst.kTempMarkingPriority, Smoothness = SurfacePalette.kTempMarkingSmoothness,
                        Tint = tint[k], Alpha = 1f, Layer = SurfaceLayer.TempMarking, RaisedQueue = true, TempSource = k,
                        Roundness = RRWGates.TempLineRoundness, LodBias = RRWGates.TempLineLodBias ? 1f : 0f,
                        QueueRaise = RRWConst.kTempMarkingQueueRaise,
                    });
            }
#if DEVTOOLS
            // dev-only experiment: Agriculture-sourced topsoil (visually unverified, may read as a crop field)
            list.Add(new SurfaceCloneSpec
            {
                Name = PrefabNames.TopsoilAgriDev, Source = PrefabNames.SrcAgriculture,
                Layers = DecalLayers.Terrain, Priority = -94, Smoothness = kTopsoilSmoothness, Tint = RRWConst.kAgricultureTopsoilTint,
                Alpha = SurfacePalette.TopsoilAlpha, Layer = SurfaceLayer.TopsoilStrip, DevOnly = true,
            });
            // Look-test lines (rrw.surf.line): Y1 / Y2 / Y3 / BLK x roundness 0.5 ("r50", the old look) / 0.01 ("r01"), so both
            // roundness values can be compared side by side. Never used by the product layers (Layer = TempMarking only for the
            // queue / style bookkeeping; SurfaceState.Prefab never resolves them).
            for (int k = 0; k < DevLineKindCount; k++)
                for (int r = 0; r < 2; r++)
                    list.Add(new SurfaceCloneSpec
                    {
                        Name = DevLineClone(k, r == 0), Source = DevLineSource(k),
                        BaseTex = k == 3 ? PrefabNames.TexAsphaltBase : null, NormalTex = k == 3 ? PrefabNames.TexAsphaltNormal : null,
                        MaskTex = k == 3 ? PrefabNames.TexAsphaltMask : null,
                        Layers = DecalLayers.Roads, Priority = k == 3 ? RRWConst.kTempMarkingPriority - 1 : RRWConst.kTempMarkingPriority,
                        Smoothness = k == 3 ? kAsphaltSmoothness : SurfacePalette.kTempMarkingSmoothness, Tint = DevLineTint(k),
                        Alpha = 1f, Layer = SurfaceLayer.TempMarking, RaisedQueue = true, DevOnly = true, TestLine = true,
                        Roundness = r == 0 ? 0.5f : 0.01f, QueueRaise = k == 3 ? RRWConst.kRenderQueueRaise : RRWConst.kTempMarkingQueueRaise,
                    });
#endif
            return list;
        }

        public static Color ColorOf(SurfaceCloneSpec s) => new Color(s.Tint.x, s.Tint.y, s.Tint.z, s.Alpha);

        // ---- rrw.surf.line test clones (registered in DEVTOOLS builds only; names keep the "RRW " prefix for the bulldoze intercept)
        // (methods, not static arrays: BuildSpecs runs from the first static field initializer, before later fields are set)
        public const int DevLineKindCount = 4;
        public static string DevLineKindName(int kind) => kind == 0 ? "Y1" : kind == 1 ? "Y2" : kind == 2 ? "Y3" : "BLK";
        public static string DevLineClone(int kind, bool round50) => "RRW Line Test " + DevLineKindName(kind) + (round50 ? " r50" : " r01");
        static string DevLineSource(int kind) => kind == 1 ? PrefabNames.SrcSand : kind == 2 ? PrefabNames.SrcPavement : PrefabNames.SrcConcrete;
        static float3 DevLineTint(int kind) => kind == 1 ? SurfacePalette.TempMarkingTintY2 : kind == 2 ? SurfacePalette.TempMarkingTintY3
                                              : kind == 3 ? new float3(SurfacePalette.kBlackoutTint) : SurfacePalette.TempMarkingTint;
    }

    // ONE place for every look constant of the Surfaces clones: tints, alphas, gloss and the scar fade ladders.
    // Tints multiply the source material's own texture (verified in game: the Ore Surface 01 material untouched + tint for soil;
    // RoadEUWorldspace swap at tint .45 for fresh asphalt).
    //
    // Topsoil / scar: the earlier RRWConst.kTopsoilTint (1.0, .92, .70) at 50 % made a bright, uniform yellow-sand band
    // (blue at 70 % of red = strong yellow, full brightness, and half the grass hidden). Freshly turned natural soil is
    // darker, browner and less saturated: the subgrade dirt hue (R:G:B ~ 1 : .87 : .77), a touch lighter than the
    // subgrade's own tint (luminance ~.80 vs ~.76) because it is loose soil mixed with grass roots, and only 35 % alpha
    // so the grass keeps showing through. The demolition scar is this same layer and fades out over its whole life
    // (SoilFadeLadder + ScarHoldFraction) instead of sitting at full strength for 8 h and then stepping.
    // The topsoil tint / alpha live in Core (RRWConst.kTopsoilTint / kTopsoilAlpha) and are read here.
    internal static class SurfacePalette
    {
        // ---- tints (multiply the source texture)
        public static readonly float3 TopsoilTint = RRWConst.kTopsoilTint;            // freshly turned soil (0.90, .78, .69; was (1.0, .92, .70): yellow sand)
        public static readonly float3 SubgradeTint = RRWConst.kSubgradeTint;          // verified "Subgrade Src" brown dirt (.85, .75, .65)
        public static readonly float3 AsphaltTint = new float3(RRWConst.kAsphaltTint); // verified very dark fresh asphalt (.45)
        public static readonly float3 OldAsphaltTint = new float3(0.70f);
        public static readonly float3 OldAsphaltCoverTint = new float3(0.9f);          // the old road in use: worn, close to the vanilla road surface              // worn old asphalt (the fresh asphalt is .45)
        public static readonly float3 TempMarkingTint = RRWConst.kTempMarkingTint;     // works yellow temporary lines (Y1, Concrete)
        public static readonly float3 TempMarkingTintY2 = new float3(1.15f, 0.95f, 0.20f); // experimental variant Y2 (Sand Surface 01)
        public static readonly float3 TempMarkingTintY3 = new float3(1.10f, 0.85f, 0.12f); // experimental variant Y3 (Pavement Surface 01)
        public const float kBlackoutTint = 0.30f;                                       // rrw.surf.line BLK (look test for a future marking black-out)

        // Switch for the yellow temporary lines. Set false if a thin area decal does not read
        // as paint: the clone is then not registered, the layer resolves to Entity.Null and is skipped cleanly (the open
        // half still opens; only the dressing is missing). No Core change needed.
        public const bool kTempMarkingOn = true;
        public const float kTempMarkingSmoothness = 0.35f;   // fresh road paint: a little sheen
        public const float kTempMarkingMinWidth = 0.02f;     // strips of this layer are never widened to SurfaceGeom.kMinBandWidth
                                                             // (the line width comes from RRWGates.TempLineWidth, >= 0.02)

        // ---- base alphas (ABSOLUTE alpha of the a100 variant; fade variants are relative to it)
        public const float TopsoilAlpha = RRWConst.kTopsoilAlpha;   // 0.35 (was 0.5): grass shows through the stripped soil
        public const float SubgradeAlpha = 1f;

        // ---- gloss. The "very dark fresh asphalt" look was verified in game at m_Smoothness 0.6. 0.25 was considered,
        // reasoning that it is the decal edge softness, but RenderedArea.m_Smoothness is the material gloss (edge softness
        // is m_EdgeFadeRange, kept from the source). Keep the verified 0.6.
        public const float kAsphaltSmoothness = 0.6f;
        public const float kTopsoilSmoothness = 0.2f;    // was 0.3: matte, freshly turned soil is not glossy
        public const float kSubgradeSmoothness = 0.1f;
        public const float kBaseSmoothness = 0.15f;
        public const float kOldAsphaltSmoothness = 0.25f; // worn: matte next to the fresh asphalt's 0.6

        // ---- fade ladders (percent of the base alpha). Every value is a registered clone "<name> a<pct>" (100 = base name).
        // Soil (topsoil + subgrade: construction verges and the demolition scar) fades in small steps over its lifetime;
        // the last step (10 % of 35 % = 3.5 % alpha for topsoil) disappears invisibly.
        public static readonly int[] SoilFadeLadder = { 100, 75, 55, 40, 25, 10 };
        public static readonly int[] AsphaltFadeLadder = { 100, 50, 20 };   // curing keeps the kCure* hours schedule
        public static readonly int[] AllFadePcts = { 100, 75, 55, 50, 40, 25, 20, 10 };

        // Fraction of a soil scar's lifetime it stays at full strength before fading linearly to 0 at its end time.
        public const float ScarHoldFraction = 0.15f;

        public static int[] FadeLadder(SurfaceLayer l) => l == SurfaceLayer.FreshAsphalt ? AsphaltFadeLadder : SoilFadeLadder;

        // Construction verge scars (topsoil + subgrade) have NO hold: visibility follows the
        // anchors a100 at 0 h -> a50 at kVergeA50Hours (0.25) -> a20 at kVergeA20Hours (0.6) -> 0 at kVergeEndHours (1.0,
        // deleted), piecewise linear, rounded to the nearest soil ladder step (100/75/55/40/25/10).
        public static float VergeVisibility(float hours)
        {
            float a50 = RRWConst.kVergeA50Hours, a20 = RRWConst.kVergeA20Hours, end = RRWConst.kVergeEndHours;
            if (hours <= 0f) return 1f;
            if (hours < a50) return math.lerp(1f, 0.5f, hours / math.max(1e-3f, a50));
            if (hours < a20) return math.lerp(0.5f, 0.2f, (hours - a50) / math.max(1e-3f, a20 - a50));
            if (hours < end) return math.lerp(0.2f, 0f, (hours - a20) / math.max(1e-3f, end - a20));
            return 0f;
        }

        public static int VergeStage(float hours) => NearestSoilStep(VergeVisibility(hours) * 100f);

        static int NearestSoilStep(float want)
        {
            int best = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < SoilFadeLadder.Length; i++)
            {
                float d = math.abs(SoilFadeLadder[i] - want);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        public static int SlotOf(int pct)
        {
            int best = 0, bd = int.MaxValue;
            for (int i = 0; i < AllFadePcts.Length; i++)
            {
                int d = Math.Abs(AllFadePcts[i] - pct);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // Ladder step a soil scar should show `hours` into a life of `endHours`: hold, then linear visibility 1 -> 0,
        // rounded to the nearest ladder step (never back up: the caller only moves forward).
        public static int SoilStage(float hours, float endHours)
        {
            if (endHours <= 0f) return SoilFadeLadder.Length - 1;
            float hold = endHours * ScarHoldFraction;
            float v = hours <= hold ? 1f : math.saturate(1f - (hours - hold) / math.max(1e-3f, endHours - hold));
            return NearestSoilStep(v * 100f);
        }
    }
}
