# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

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
