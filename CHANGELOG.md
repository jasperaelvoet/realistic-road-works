# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

## [3.2.1] - 2026-10-08

### Fixed

- Road upgrades no longer look finished before the works reach a strip: the old road that still carries traffic keeps a worn
  old-asphalt look (with the yellow temporary lines), a strip that is not dug yet shows the ground it was (grass), and only
  freshly paved asphalt is black until the final markings are painted.
- The yellow temporary lines stop where the temporary lanes ease back to the road's own lanes before a junction, so they no
  longer end in a jog at the junction.

[3.2.1]: https://github.com/jasperaelvoet/realistic-road-works/releases/tag/v3.2.1

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
