# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

## [3.4.0] - 2026-10-09

### Added

- **Gravel roads in one step.** Building a gravel road digs and lays the gravel base, and the road is done: no paving, no
  markings, about 40 % less time than an asphalt road. An upgrade to a gravel road runs as a single step (both sides at once) and
  has no re-marking.

### Fixed

- Upgrading a road with the replace tool dragged against the road's direction tore up its junctions and mirrored the works.
  Such an upgrade now places the new road at once (the works run on it), and saves affected by 3.3.0 repair themselves on load.
- A kerb that only moves by a metre is left to the finishing only at the edge of the road; narrow raised islands inside the
  carriageway are built as before.

[3.4.0]: https://github.com/jasperaelvoet/realistic-road-works/releases/tag/v3.4.0

## [3.3.0] - 2026-10-08

### Added

- **The old road stays until the works are done.** During a road upgrade the old road, its junctions and its connections stay
  exactly as they were while the new strips are built beside them. The upgraded road and its junctions appear only when the
  works reach their finishing (the re-marking, or the completion). Saved games keep the pending upgrade.
- **Re-marking on the move.** Every upgrade that changes the carriageway ends with re-marking. On roads with two lanes or more
  per direction the painting crew works on the move: only the lanes next to the lines being painted close, over a short stretch
  around the crew that moves along with it, and every direction keeps driving. Narrower roads repaint one half at a time on
  temporary lanes (or with portable traffic lights), and close fully only as a last resort.
- Ahead of the painting crew the road carries no markings; behind it the new markings appear. New strips keep their fresh
  asphalt look until the crew has passed. There is no more black resurfacing of the whole road during re-marking.

### Fixed

- The digging and fences of a widened side start right at the old kerb instead of leaving a strip of grass between the road and
  the works, and the barriers stand along the lane in use.
- A kerb that only moves by a metre (a slightly wider or narrower sidewalk) no longer gets fenced-off works of its own.
- The ground textures of the works no longer appear to slide when a layer grows, shrinks or changes.
- The yellow temporary lines follow the temporary lanes back to the road's own markings at the ends of a road piece and run on
  over a straight node to join the markings of the next piece; in bends and at width changes they end at the junction.

[3.3.0]: https://github.com/jasperaelvoet/realistic-road-works/releases/tag/v3.3.0

## [3.2.0] - 2026-10-07

### Added

- **Temporary lanes on the old asphalt.** For every step of an upgrade the mod compares the old and the new road: the asphalt
  that stays drivable carries temporary lanes (moved sideways where the final lanes are not yet usable), marked with yellow
  temporary lines exactly where cars drive. Lanes that do not fit are closed with lane closures inside the works.
- **One lane with traffic lights.** When only one lane fits next to the works, both directions share it and alternate:
  portable traffic lights at both ends of the section switch red / green, with an all-red phase that waits until the section
  is empty. Used on sections up to 300 m whose ends have no traffic lights of their own.
- **Re-marking without a detour.** While one half of the road is resurfaced and painted, both directions use temporary lanes
  on the other half (or one shared lane with signals on narrow roads) instead of closing one direction.

### Changed

- The sidewalk of a widened side is dug and built with the new strip and closed to pedestrians while it is built (on sides
  without buildings).
- Panel and tooltip texts describe the temporary lanes and the alternating one-lane operation.

[3.2.0]: https://github.com/jasperaelvoet/realistic-road-works/releases/tag/v3.2.0

## [3.1.0] - 2026-10-07

### Added

- **Road upgrades build only what changes.** Replacing a road with another type (for example Small Road to Medium Road) or using
  an upgrade that moves a kerb no longer rebuilds the whole road or skips the works:
  - widening digs, grades and paves only the new strip;
  - narrowing breaks out only the strip beyond the new edge and restores the ground;
  - a change of lanes or markings re-marks the road one direction at a time.
- **Lane by lane.** Traffic keeps running in both directions next to the works on yellow temporary construction markings
  (centre line, lane lines and an edge line along the works). Only the lanes being built are closed, with invisible lane
  closures; each direction keeps at least one lane, using the old pavement where needed.
- Machines (excavator or mini excavator, trucks, grader, paver, rollers, painter) work only inside the closed strip, never in a lane
  that carries traffic.
- Streets with houses keep every lane, parking spot and driveway open: slow zone with a covered work strip and yellow markings.
- New setting **Road upgrades**: Realistic (default), Full rebuild, or Instant. Trees, lights and other decorations stay instant.
- The works panel shows the upgrade kind, a step per work window, progress per side and the traffic arrangement. Upgrade works
  cannot be cancelled (bulldozing the road ends them and demolishes it normally); Rush still works.
- Placement tooltip with the expected hours and traffic arrangement while replacing a road.

### Changed

- Save format v2 (backward compatible): saves from 3.0.0 load unchanged, and removing the mod still leaves a clean city.
- Yellow temporary lines are wider and more visible.

### Fixed

- Rollers no longer turn black under fresh-asphalt covers on a visible road.

[3.1.0]: https://github.com/jasperaelvoet/realistic-road-works/releases/tag/v3.1.0

## [3.0.0] - 2026-10-06

First public release. (Earlier 1.x and 2.x builds were never published; the version number continues their history.)

### Added

- Roads take in-game time to build (48 working hours per km, minimum 6 h, by default) and to demolish (24 h per km, minimum 3 h).
  Crews work around the clock.
- Visible construction in five phases: survey & clearing, excavation, foundation, paving, and markings & finishing.
- A real terrain trench that deepens behind the excavator, with dirt, gravel and fresh-asphalt ground textures.
- Demolition in three phases: breaking up, removing the road bed and restoring the ground, ending without a terrain jump.
- Machines: excavators with an IK dig cycle that load dump trucks bucket by bucket, a grading excavator, tandem road rollers, a
  paver, a line painter and crew trucks, all within a per-machine speed and acceleration table, plus dust puffs and night lights.
- Multiple crews on long roads, each working its own section.
- Closed sites with path rerouting and parked-car relocation. The road opens only after every machine has left.
- Staged opening: sidewalks open from paving, and one direction opens during the markings, with yellow temporary lines, fences, divider
  cones, signs and blinking beacons. Then the halves swap.
- A Road works info panel with phase stepper, progress, ETA, crews, traffic status, cost and refund, and Rush, Cancel and Show work
  front buttons. Clicking a trench, barrier or machine opens it.
- Tooltips with the expected work time while drawing or bulldozing roads, and map markers over closed sites.
- Settings for timing, traffic policy, detail level and every visual layer, machines per crew and crews per road.
- Save safety: one small saved component per road segment under works. A save loads cleanly without the mod.
- DevTools builds with in-game dev commands (see `docs/DEV-COMMANDS.md`).

[3.0.0]: https://github.com/jasperaelvoet/realistic-road-works/releases/tag/v3.0.0
