# Architecture

This document explains how Realistic Road Works is put together. It is for contributors who want to find their way around the code.
Players don't need it.

## 1. Design principles

1. **One saved fact per road segment.** A road segment under works carries one saved component, `RoadWorksSite` (work done, work
   required, chain coordinates, mode, a few flags). Everything else is derived from it every frame and is never written to the save:
   the trench, the ground textures, the props, the machines, the lane closures and the markers.
2. **The site is a pure function of progress.** The phase, the work front, the terrain profile, the extent of each ground texture,
   the fill of every heap and every machine's anchor are computed by pure functions (`PhasePlan`, `MachineLimits`, `DigSchedule`)
   from the progress `p` and the road geometry. Saving, loading, a dev jump or a "Rebuild visuals" all produce the same picture.
3. **No pops, no flicker.** A layer only disappears under another layer that has been on screen for at least one frame. Heaps grow
   and shrink instead of spawning at full size. A road is never un-hidden in the same frame its terrain clipping returns.
4. **Modules depend only on Core.** Every feature module reads and writes shared state through `Src/Core`. Modules never reference
   each other's types, so each module compiles on its own against Core.
5. **Fail safe.** No exception leaves `OnUpdate`: system bodies run inside try/catch, and most use the Core guard (`RRWGuard`),
   which logs the first error and disables the system after 30 failing frames in a row. Missing pieces degrade to a simpler site (no machines, no textures, a full closure) rather than to
   a broken one.
6. **Vanilla assets only.** All models, textures and effects are vanilla prefabs or runtime clones of them. The road roller's mesh
   is generated in code.

## 2. Repository map

| Path | Namespace | Responsibility |
|---|---|---|
| `Mod.cs` | `RealisticRoadWorks` | Entry point: registers the settings, then every system marked `[RegisterSystem]`, in ascending order |
| `RegisterSystemAttribute.cs` | `RealisticRoadWorks.Dev` | The attribute: update phase, `Before` / `After` target, order |
| `Src/Core` | `RealisticRoadWorks.V3` | Shared types, the saved component, settings and texts, the timeline (`PhasePlan`), geometry (`ChainGeometry.cs`: `EdgeArc`, `EdgeSection`), the site registry, the site factory, speed limits, the dig schedule, constants, system orders |
| `Src/Tools` | `.Tooling` | Turns tool actions into works: new, split, combined, replaced and upgraded roads; bulldozer intercept |
| `Src/Director` | `.Director` | Progress accrual, phases, hiding roads and nodes, tree clearing, wear, markers, requests (rush, cancel, finish), completion, staged-opening stages, crew count, machine allowance, tool-preview hiding |
| `Src/Ground` | `.Ground` | The terrain trench: composition clones with custom terrain profiles, natural-ground sampling, the reveal hold, clone cleanup |
| `Src/Surfaces` | `.Surfaces` | Ground texture layers as area decals: clone registration, polygons per layer and front, junction caps, verge scars, render-queue raise |
| `Src/Props` | `.Props` | Barriers, beacons, cones, fences, signs, heaps, rubble, crew props, dust puff emitters; heap fill; effect clone registration |
| `Src/Machines` | `.Machines` | Excavator clone registration, machine puppets, plans and legs, the mover and bone jobs, the IK dig cycle, road rollers, separation guard, machine report |
| `Src/Traffic` | `.Traffic` | Lane closures per lane group, parking, path invalidation, drain reports, road classification, save guard for lane fields |
| `Src/UI` | `.UI` | The Road works info section, click-to-select for sites, tooltips |
| `UI/RealisticRoadWorks.mjs` | – | The UI module (plain ES module, no build step) that renders the info section with the game's own UI components |
| `Src/Persistence` | `.Persistence` | Save sanitising (nothing derived reaches the save), legacy save migration, save audits |
| `Src/Dev`, `Dev/` | `.DevCmds`, `RealisticRoadWorks.Dev` | DevTools builds only: the dev command harness and the `rrw.*` commands |

## 3. Vocabulary

- **Site.** A road edge with `RoadWorksSite`. Its `Kind` is Construction or Demolition.
- **Project / chain.** The edges of one tool action (one drawn road, one bulldozer drag) are split into maximal paths. Each path is
  one project with one progress value and one continuous work front. Chain coordinate `u` runs from 0 to the chain length `U`. Each
  edge stores the `u` of its two curve ends, so reversed edges are handled.
- **Trimmed chain.** Where a chain end meets a road that is not under works, the works start where that junction's geometry ends.
  Machines, props and road-surface textures stay inside the trimmed range.
- **Progress `p`.** Work done / work required, the same on every edge of a project. Work accrues in simulation frames, so it pauses
  with the game and follows the game speed.
- **Phase and `f`.** `PhasePlan.PhaseOf(kind, p, out f)`. `f` is the fraction within the phase.
- **Front `F`.** How far along the chain the current phase has got. Ground textures use it quantised to 2 m steps.
- **Crews and sections.** A long project is split into sections, one crew each. Section `i` has its own front `F_i`. Every span, fill
  and depth function takes the section layout into account, and with one crew it reduces to the single-front case.
- **Mode.** Chosen once at the start and saved. **Full** works (mode A) hide the road and dig a real trench. **Minimal** works
  (mode D) keep the road visible, with props, the closure and the timer only.
- **Closure.** Closed, slow zone or open, per edge, plus the lane groups that may open early (section 7).

## 4. One frame

Systems run in the vanilla update phases below. Inside a phase, our systems run in the order given by `RRWOrder`; `Mod.cs`
registers them in ascending order. Every system checks the game mode, and most return at once when there is no site.

| Phase | Order | System | Does |
|---|---|---|---|
| PrefabUpdate (main menu) | 50 | `SurfaceRegistrySystem` | Registers the ground-texture clones ("RRW Topsoil", "RRW Subgrade", "RRW Base Course Cover", "RRW Fresh Asphalt" (+ Cover), "RRW Temp Marking") |
| | 52 | `PropPrefabSystem` | Registers effect clones: amber light, dust VFX, beacon barrier, dust heaps, dust puff |
| | 55 | `MachinePrefabSystem` | Registers "RRW Road Excavator" (+ "Quiet"): a mining excavator clone without the large dust clouds and without its work-vehicle role |
| ToolUpdate | 100 | `BulldozeInterceptSystem` | Turns a bulldozer apply on roads into demolitions or cancellations instead of deletions |
| | 805 | `WorksClickSelectSystem` | A click on a trench, a works prop or a machine selects the works street |
| ApplyTool | 110 | `WorksTagSystem` | Adds `RoadWorksSite` to the road entities the apply will create: new roads, split remnants, combined and replaced edges |
| Modification1 | 210 | `WorksDirectorSystem` | Clock, registry, requests, accrual, phases, Hidden, wear, markers, tree clearing, closure targets, stages, crews, machine allowance, completion |
| | 215 | `LegacyMigrationSystem` | Converts works from older saves |
| | 220 | `TrafficRequestSystem` | Applies closure targets, lane groups, parking, drain reports, classification |
| | 230 | `PropSystem` | Spawns, moves and removes props from the slot plan |
| | 240 | `MachineDirectorSystem` | Spawns and removes machines, plans their legs, dig events and truck loads, separation; writes the machine report |
| | 250 | `SurfaceAreaSystem` | Creates, rewrites and deletes the ground-texture areas |
| Modification2B | 430 | `PreviewHideSystem` | Hides tool-preview copies of hidden works roads |
| Modification4 | 400–420 | `SurfaceTagSystem`, `PropOwnerSystem`, `MachineTagSystem` | Mark new areas, props and machine parts as never saved; give props their owner node |
| Modification5 | 435 | `PreviewHideCatchUpSystem` | Catches preview sub-lanes and sub-objects that vanilla did not regenerate |
| ModificationEnd | 500–520 | `LaneClosureSystem`, `ParkingClosureSystem`, `PathInvalidationSystem` | Write lane fields after the vanilla lane data systems; invalidate paths over closed lanes |
| | 530 | `HeapFillSystem` | Sets heap fullness after the vanilla quantity update |
| | 540 | `ExcavationSystem` | Terrain profiles on composition clones, right before `TerrainSystem` |
| PreCulling | 600 | `MachineMoverSystem` | Moves every machine along its plan (job) |
| | 605 | `RollerRenderSystem` | Poses, lights and level-of-detail for the road roller GameObjects |
| | 610 | `SurfaceMaterialSystem` | Raises the render queue of the cover layers above the lane markings |
| Rendering | 700 | `MachineBoneSystem` | Excavator and grader bones, IK, pistons, tyres |
| UIUpdate | 800 | `WorksInfoSection` | The info panel |
| UITooltip | 810 | `WorksTooltipSystem` | Tool tooltips |
| Serialize | 900–915 | `SaveSanitizeSystem`, `TrafficSaveGuardSystem`, lane audit, `SaveRestoreSystem`, `TrafficSaveRestoreSystem` | Keep derived state and modified lane fields out of the save, then restore them |

DevTools builds add `DevCommandSystem` (MainLoop), a camera follower for video capture (PreCulling) and a few watchers.

Structural changes (create, delete, add or remove components) happen only in PrefabUpdate, ToolUpdate, ApplyTool, the Modification phases and
Serialize, never in PreCulling, Rendering or UI. The UI and the settings enqueue requests (`WorksRequests`), and the Director handles
them at Modification1.

### Clocks

- **Progress** uses simulation frames. It stops when the game is paused and follows the game speed.
- **Animation** uses render time scaled by the simulation speed, so machines freeze on pause and speed up at higher game speeds.
- **Holds, throttles and cleanup ages** count rendered frames. They keep counting while paused, so a visual hand-over finishes even
  while the game is paused.

## 5. The timeline

### Construction (full mode)

| Phase | p | Road | Terrain | Ground textures | Crew |
|---|---|---|---|---|---|
| Survey & clearing | 0 – 0.08 | hidden | natural ground (the original landform) | topsoil band grows | crew truck, grader stripping topsoil; trees cleared ahead of the front |
| Excavation | 0.08 – 0.35 | hidden | per edge, eased from natural down to the subgrade depth as the front passes | dirt (subgrade) behind the excavator | excavator digging, dump trucks loading, grader following; spoil heaps grow on the verge |
| Foundation | 0.35 – 0.58 | hidden | the whole chain rises evenly to a gravel bed 0.125 m below grade | gravel cover behind the grader | stone trucks tipping, grader spreading, roller compacting |
| Paving | 0.58 – 0.86 | **revealed** | vanilla, after a short hold | fresh asphalt + cover behind the paver; gravel cover ahead | paver, feed truck, breakdown and finish rollers, crew truck |
| Markings & finishing | 0.86 – 1 | visible | vanilla | the asphalt cover shrinks behind the painter, uncovering the real markings; yellow temporary lines on the open half | line painter, crew truck; cones and barriers picked up |
| Complete | 1 | visible | vanilla | road textures removed in the same frame; verge soil fades within about an in-game hour | machines drive off, then the road opens |

Subgrade depth is 0.75 m on roads up to 20 m wide and 1.0 m on wider roads. A replaced road (new road type, no
buildings) starts at the excavation phase: it turns to gravel first, then the bed is dug out.

### Demolition (full mode)

| Phase | p | Road | Terrain | Ground textures | Crew |
|---|---|---|---|---|---|
| Breaking up | 0 – 0.35 | visible | vanilla | gravel cover grows over the road surface behind the breaker | first a traffic drain, then barriers; excavator chopping, stone truck, rubble and windrows |
| Removing road bed | 0.35 – 0.75 | **hidden** | the bed sinks from −0.125 to −0.5 m below grade | gravel shrinks over dirt | excavator digging, truck loading |
| Restoring ground | 0.75 – 1 | hidden | per edge, morphs back to the natural ground | topsoil | grader backfilling, ore truck tipping, roller |
| Complete | 1 | deleted | natural | topsoil scar fades over about 12 in-game hours | machines drive off |

The edge and its orphan nodes are deleted one frame after the terrain has settled, so the deletion itself changes nothing on screen.

### Minimal mode

Elevated roads, bridges, tunnels, lowered roads, very short roads, the Low preset, works whose closure does not start Closed (the
*Always slow zone* and *Visual only* policies, a replaced road with buildings) and works started with excavation switched off run in
minimal mode. The road stays visible and vanilla, with no trench, no textures and no machines. Barriers, cones and crew props are
placed along the road, road wear rises or falls with progress, and the closure, timer, marker and panel work as in full mode. An
ineligible edge inside a full project (a bridge in the middle of a chain) is minimal on its own.

### Cancel, rush, split

- **Cancel during construction** turns the works into a demolition that continues from the exact current state: from the hidden
  phases it jumps to the restore phase and starts from the current terrain depth; from the visible phases it breaks the new asphalt
  up again. The refund is `paid × (1 − p)`. Below 5 % progress the road is deleted at once with a full refund.
- **Cancel during demolition** is allowed only while the road is still being broken up; the site is called off once the machines
  have left.
- **Rush** costs a percentage of the road's price, scaled by the work left. It raises the work rate to 1.5x and cannot be undone.
- **Splits and upgrades** keep the works. Remnants inherit the site and get chain coordinates projected from the original curve;
  props are keyed by project and slot, not by edge, so a split never touches them.

## 6. How the visible parts work

### Hiding the road and nodes

The Director adds the game's `Hidden` component (with `BatchesUpdated`) to works edges, and to nodes whose connected edges are all
hidden works edges. It re-asserts `Hidden` every frame, because tool previews and edits remove it. It removes `Hidden` only on the
transition frame. While a net tool hovers over or snaps to a hidden works road, the game builds temporary preview copies of it;
`PreviewHideSystem` hides those copies too, so the finished road never shows through the site.

### Terrain trench (Ground)

A hidden road still clips and flattens the terrain under it, which shows as a flat, untextured bed. To get a real trench, Ground
clones the edge's three composition entities (edge, start node, end node), points the edge's `Composition` at the clones and gives
each clone a `TerrainComposition` with:

- a cut cap and a fill floor at the target depth, so the game's `TerrainSystem` cuts or fills the ground to the profile;
- the clipping moved far underground, so there is no hole and no road imprint.

The target profile comes from `PhasePlan.Terrain`. It can be natural ground (sampled from the verges and the base heightmap when the
works start), a flat bed at a depth, or a morph between them. Values are quantised to 1/16 m. A profile change is applied by marking
the edge `Updated` right before `TerrainSystem` runs (at ModificationEnd, after every vanilla consumer of `Updated`, so no lanes or
geometry regenerate), at most about four times per second. Node slots take the shallowest connected works profile, or keep the
vanilla clip where a visible road meets the node.

**Reveal hold.** When the road becomes visible (start of paving, cancel, lost site), the clones stay for 45 frames with the floor at
−0.125 m, so the road mesh is drawn over intact ground before the clipping returns. Only then are the vanilla values written back,
the edge is pointed at the vanilla compositions again and the clones are cleaned up. The clones are never saved. After a load they
are rebuilt from the saved site in the first frame.

### Ground textures (Surfaces)

In the main menu, Surfaces registers runtime clones of vanilla surface prefabs with swapped textures and tints:

| Layer | Look | Drawn on |
|---|---|---|
| Topsoil | lighter stripped soil, semi-transparent | terrain |
| Subgrade | compacted brown dirt | terrain |
| Base Course Cover | crushed gravel, above the lane markings | terrain and roads |
| Fresh Asphalt | dark new asphalt, below the lane markings | terrain and roads |
| Fresh Asphalt Cover | dark new asphalt, above the lane markings | terrain and roads |
| Temp Marking | yellow temporary lane lines | roads |

Each layer becomes area decals created through the game's area creation path. There is one polygon per edge, layer, carriageway
band and section piece, built from the edge's arc with mitred cuts at interior nodes and junction caps where works roads meet.
Spans come from `PhasePlan.SurfaceSpans`, which never drops a piece and only shrinks a layer at a front. A rewrite happens only when
a front moves a 2 m step, at most four times per second per project, and at most eight projects per frame. Phase changes and
geometry changes bypass the limits, so all layers of a project change in the same frame. The two cover layers get a raised render
queue, so they hide the lane markings until the painter uncovers them.

### Props

Props are static vanilla objects spawned by code: barriers, concrete barriers, cones, fences, signs, spoil and stone heaps, rubble
and crew props. They are keyed by project, group and slot, and each slot has a fixed chain position. `PhasePlan.PropFill` gives
each slot a fill value: below zero the prop must not exist, otherwise it must exist with that fill. Heaps are quantity objects, so
they grow and shrink through their fullness instead of popping. Each prop gets a works node as its owner in the same frame, one
phase after it is created (Modification4); this stops the game from treating it as an object in the way of the road and hiding it. A periodic self-heal respawns anything missing.

Blinking beacons are barrier clones carrying a dimmed amber light clone. On a closed end, their blink phases are picked so the light
runs across the barrier line. Dust heaps carry a small vanilla dust effect. Dust puffs at the excavator bucket are short-lived
emitter props.

### Machines

Machines are **puppets**: real vehicle entities from vanilla prefabs, with every AI and navigation component removed. They keep
everything the renderer needs: transform history, bones, lights, effects and engine sounds.

- **Plans.** Each puppet has a job-safe plan: its path along the chain, up to twelve legs (hold, drive or reverse, follow the
  front, dig-and-hop, shuttle, scrape, three-point turn, brake), a copy of the project's progress model and a floor height table. A leg can be absolute or relative to the crew's
  front.
- **Mover and bones.** A job evaluates the plan at render time and writes the transforms (PreCulling). A second pass animates the
  bones (Rendering): excavator slew, boom, stick and bucket, pistons aimed at their partners, and tyres that turn only when the
  machine moves.
- **Speed limits.** `MachineLimits` gives every role a forward, reverse and turning speed, an acceleration and a working speed.
  Every leg is stretched to respect them, and a dev check measures the real motion against them.
- **IK dig cycle.** The excavator runs a fixed 20 s cycle from `DigSchedule`: lower, penetrate, drag, curl, lift, swing, dump, shake,
  return. A planar two-link IK solver places the bucket on the real trench floor at the bite point and over the truck bed at the
  dump. Breakout and dump events are computed from the schedule at Modification1, where they fill the truck bucket by bucket and
  spawn dust puffs.
- **Road rollers** are plain Unity GameObjects with a mesh built at runtime, not entities. That keeps them out of saves and out of
  the game's selection. They share the puppets' plans, speed limits, separation and report, and are drawn with the game's lit
  shader and level of detail.
- **Separation.** A planner checks planned boxes of every pair over the next seconds before committing a plan, and a runtime guard
  brakes the lower-priority machine if two would still touch. Parking and spawn spots are kept apart and out of working machines'
  paths (`ChoreoSeparation.cs`). A standing machine that blocks a higher-priority one makes way, and two leavers that would meet
  head-on in one lane agree on an exit instead of backing out into the same conflict again.
- **Crews and level of detail.** Machines spawn only for crews whose front is within 600 m of the camera, up to 40 machines and
  8 active crews in total. They are removed only when out of sight (beyond 800 m or culled), with one exception: a machine leaving the site
  disappears a few seconds after it stops at an open exit, so the road can open. A crew without machines still advances its front.
- **Machine report.** Every frame, Machines publishes which lane groups each project's machines occupy now or will drive into. The
  Director opens nothing a machine is on, and the road opens at completion only after the report shows the carriageway clear.

### Traffic

Traffic classifies each lane of a works edge into a group: the left or right direction half, the left or right sidewalk, the left or
right parking strip. Each group gets a state:

- **Open:** cars at work-zone speed, pedestrians and trams as vanilla.
- **Soft:** closing, while the vehicles already on it drain.
- **Closed:** a restriction that pathfinding will not route through, with a stricter blockage as an optional mode.

Closures refresh the lanes, invalidate the paths that use them, and move parked cars from building-free closed roads. Before a save,
every modified lane field is put back to its vanilla value and re-applied afterwards, so the save holds vanilla lanes. Traffic also
classifies roads (dead-end chains, tram tracks, transport stops, which side has buildings) for the staged-opening rules, and reports
per group whether it has drained.

## 7. Staged opening and the release gate

On a full construction under the Realistic policy:

1. **Survey to foundation:** everything closed, with a site fence along edges that serve buildings.
2. **Paving:** the sidewalks open behind kerb fences once a fresh machine report shows them clear.
3. **Markings, first half:** one direction half opens at work-zone speed, with yellow temporary lines, divider cones, barrier lines
   and signs, while the painter works the other half.
4. **Switch:** progress is held. The crew leaves the works half, the halves swap (the newly closed half drains first), and the crew
   comes back on the other side.
5. **Markings, second half:** the first half now has its final markings and stays open. The crew finishes the second half.
6. **Release:** at completion every machine gets a leave plan. The road opens on the first frame the report shows the carriageway
   clear, or at a safety cap.

A direction opens early only when both chain ends connect to the road network, the road has two separable direction halves each at
least 2.9 m wide (measured per lane against the local curve direction in `LaneSection.cs`, so curved and reversed roads qualify),
there is no tram track, and no transport stop is in a half the crew will work in. Otherwise only the sidewalks open. A group that opened stays open. Every
step fails closed: without a fresh report from Machines or Traffic, nothing opens.

## 8. Save model

- **Saved:** `RealisticRoadWorks.V3.RoadWorksSite` on each works edge. It has a version, a payload size and a 49-byte payload.
  The reader is forward compatible: it reads the fields it knows and skips the rest. A payload that is too short or malformed becomes
  a neutral site that finishes at once.
- **Never saved:** the runtime state components, `Hidden`, composition clones, area decals, props, machines and all their sub-objects,
  rollers, dust puffs and markers. Derived entities carry the game's `LivePath` marker, which excludes them from serialization. A
  sanitising pass before every save also hides from the serializer anything derived that lacks it. Lane fields are reset to vanilla
  around the save.
- **Load:** every module clears its state on preload. In the first frame of the city, the Director rebuilds its registry from the
  saved sites, Ground re-clones and re-applies the terrain, and props, textures, machines and closures come back.
- **Load without the mod:** the game skips the unknown component. Roads under construction are plain finished roads, roads being
  demolished stay, and the terrain is vanilla.
- **Legacy saves:** a migration system converts works components written by earlier, unreleased development builds into sites on
  the first frames after loading.

## 9. Performance budgets

Design targets for about 30 active sites. They are goals, not guarantees; check them with the dev `rrw.perf` command in a DevTools
build:

| Item | Budget | How |
|---|---|---|
| All mod systems | < 0.6 ms average per frame, no single-frame spike > 2 ms | per-system timers |
| Director | < 0.15 ms | `Hidden` asserts are component checks; wear every 16 frames, clearing every 30 |
| Ground | < 0.05 ms, terrain re-renders ≤ 4 per second | global trigger cap, batching |
| Surfaces | < 0.15 ms | 2 m steps, ≤ 4 Hz per project, ≤ 8 projects rewritten per frame, nearest first |
| Props | < 0.1 ms | slot diff, self-heal every 16 frames, ≤ 250 per project and 3000 in total, far projects keep only barriers and cones |
| Machines | < 0.05 ms per active crew | plan sample cache, time-sliced planning (1 ms per frame budget), spatial hash for the guard, ≤ 40 machines, ≤ 12 rollers |
| Traffic | < 0.1 ms amortised | event-driven refreshes, sweep every 64 frames, classification time-sliced at one edge per frame |
| UI | < 0.05 ms | only while the panel is open |

Steady-state update paths are written not to allocate managed memory: no LINQ, closures or boxing; lists and buffers are reused. With no site
in the city, every system returns at once.

## 10. Adding to the mod

- Put new code in the module folder it belongs to and keep the namespace of that folder.
- Cross-module data goes through Core: the registry records (`EdgeRecord`, `ProjectRecord`, plus per-module slots), the runtime
  components and `WorksRequests`. Each shared field has exactly one writing module. Writers read, modify and write back; they never
  construct a fresh struct over an existing component.
- New systems use `[RegisterSystem(phase, Before|After = typeof(X), Order = RRWOrder.Y)]`. Add the order constant to `RRWOrder`.
- Wrap the system body in the Core guard and the perf timer, and log through `RRWLog` with a module prefix.
- Diagnostics and dev commands go inside `#if DEVTOOLS` in your module folder, named `rrw.<module>.<verb>`. Register a checker for
  `rrw.check` when your module has invariants.
- Check that both the Release and the DevTools build compile.
