# Dev commands

DevTools builds include an in-game command interface for testing, debugging and recording videos. You can jump a site to any phase,
speed up time, frame the camera on the work front, and run self-checks that report broken invariants. Release builds contain none of
this.

> Use a throwaway save. Several commands change progress, money or lanes the way a player never could.

## Enabling it

Build with `-p:DevTools=true` (see [BUILDING.md](BUILDING.md)) and install the DLL as usual. A DevTools build defines `DEVTOOLS` and
compiles `Dev/` (the harness) and `Src/Dev` plus every module's `#if DEVTOOLS` code.

## Running a command

The mod watches one file:

```
<game user data>/ModsData/RealisticRoadWorks/cmd.txt
```

`<game user data>` is the folder that also holds `Mods` and `Logs` (on Windows
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II`). The folder is created when the game starts.

- Write one command per line: the name, then arguments separated by spaces. For example:

  ```
  rrw.list
  rrw.timescale 60 shift=ignore
  rrw.cam #0 dist=200 pitch=35 follow=1
  ```

- The mod checks for the file every frame. It reads the file, deletes it, then runs its lines in order on the main thread. Write the
  whole file at once (write to a temporary name and rename it).
- Output goes to `Logs/RealisticRoadWorks.log`. Lines from the harness start with `dev`, and most `rrw.*` output lines start with
  `dev rrw`.
- `help` logs every command available in the build with its usage line. The startup log also lists them all.
- Command names are case-insensitive (except `help`, which must be lower case). Options are `key=value`; everything else is positional.
- Commands run in the main menu too, but almost all of them need a loaded city.

## Addressing sites

Wherever a command takes `<site>`:

| Form | Means |
|---|---|
| `#n` (or a bare number) | Entry `n` of `rrw.list` (projects sorted by id) |
| `p<id>` | Project id |
| `e<index>` | The project of the edge with this entity index (`rrw.dump` then shows only that edge) |
| `r<n>` | The project of road `n` in the harness `list` (newest roads first) |
| `all` | Every project |

Some commands accept a comma-separated list (`e123,e124`). Machine commands also take a role (`excavator`, `trucka`, …),
`p<id>:<role>` or `#n`.

## Command reference

The tables list the commands of the 3.0.0 code. Run `help` for the exact usage line of each command in your build.

### Harness (`Dev/`)

| Command | Description |
|---|---|
| `help` | List every command with its usage |
| `list [n]` | The newest road edges with index, entity, length and hidden state (the indices used by `r<n>` and `rrw.start`) |
| `inspect [n=3] [built]` | The n road edges nearest the camera pivot with all their component types |
| `focus <road#> <zoom> <pitch> <yaw> [t=0.5]` | Move the gameplay camera to a point on a road |
| `hide <road#> <0\|1>` | Toggle the game's `Hidden` component on a road edge |
| `speed <0\|1\|2\|3>` | Simulation speed (0 pauses) |
| `pause` | Pause the simulation |
| `ui <0\|1>` | Hide or show the game UI (for clean screenshots) |
| `overlay <0\|1>` | Hide or show world overlays: street names, notification icons and outlines, like photo mode. Re-applied every frame, so an autosave does not bring them back |
| `tod <hour>\|off` | Override the visual time of day (sun and sky only; the simulation clock keeps running) |

### Sites, time and requests (`Src/Dev`)

| Command | Description |
|---|---|
| `rrw.list` | Every works project with kind, mode, phase, progress, length, trims, crews, rollers, closure, machine count and ETA |
| `rrw.dump <site>` | Full state of a project: saved site, runtime, ground output, registry records and every module's dump |
| `rrw.p <site> <p>` / `rrw.set <site> <p>` | Set progress (0..1, or a percentage) on the whole project |
| `rrw.phase <site> <C0..C4\|D0..D2> [f=0]` | Jump to the start of a phase, plus fraction `f` within it |
| `rrw.timescale [x] [shift=obey\|ignore] [machines=<y>]` | Global work-time scale for every site (kept across loads in the session). Machines run at the same scale unless `machines=` says otherwise |
| `rrw.start <roads> [c\|d]` | Start a construction (`c`) or demolition (`d`) on existing roads as one batch (`3`, `0-4`, `0,2,5` = harness road indices) |
| `rrw.finish <site>` | Complete the works now |
| `rrw.finishall` | Complete every works in the city |
| `rrw.cancel <site> [instant]` | Cancel like the panel button. `instant` deletes a construction with a full refund |
| `rrw.rush <site>` | Rush the works (charges the rush cost) |
| `rrw.rebuild` | Every module respawns its visuals (same as the Rebuild visuals setting) |
| `rrw.crews [site [n\|auto]] [max=<1..6>]` | Crew table. `<site> <n>` pins the crew count until the next phase; `max=` sets "Crews per road" for this session |
| `rrw.rollers [site\|all] [on\|off] [preview] [sweep] [phase=C2\|C3\|D2]` | Planned road rollers per crew. `on`/`off` toggles the setting for this session |
| `rrw.dig [site] [mode=dig\|break] …` | The IK dig schedule: key times, events, hop window; toggles for detailed digging and dust |
| `rrw.layer <name\|all> <0\|1>` | Switch a feature layer off or on: `hide`, `terrain`, `surfaces`, `machines`, `bones`, `props`, `closure`, `icons`, `wear`, `previewhide` |
| `rrw.hideafterground <0\|1>` | Hide works roads only after Ground has applied its first profile (one visible frame instead of a possible hole) |
| `rrw.verbose <0\|1>` | Extra logging |
| `rrw.check` | Core invariants plus every module's checker. Logs `check ok` or each failure |
| `rrw.perf [seconds=10] [detail=1]` | Per-module main-thread time over a window, with the module sections and the budget verdict |

### Camera (`Src/Dev`)

| Command | Description |
|---|---|
| `rrw.cam <site> [dist=200] [pitch=35] [yaw=auto\|autofwd\|<deg>] [follow=1] [lead=0] [tau=1.5] [orbit=0] [crew=i]` | Frame the work front and follow it smoothly (for video). It stops following when you move the camera. `rrw.cam off` stops; `rrw.cam` alone prints the current camera as a command |
| `rrw.focus <site> [dist=150] [pitch=35] [yaw=…] [lead=0] [crew=i]` | Frame the work front once |
| `rrw.campos <x> <y> <z> [dist=] [pitch=] [yaw=]` | Fixed camera position for reproducible shots |

### Staged traffic (`Src/Dev`, `Src/Director`)

| Command | Description |
|---|---|
| `rrw.stage [projectId]` | Stage, switch step, open / soft / ready groups, car-half verdict and reason, hold age, exits, drain report per edge |
| `rrw.stage.step <projectId> <vacate\|swap\|drain\|ready\|skip\|reset>` | Force a step of the side-switch sequence |
| `rrw.stage.force <site> <c4a\|c4b\|switch\|teardown\|d0\|d0close> [f=]` | Jump progress to a stage point |
| `rrw.stage.info <site\|all>` | The planned staged-traffic state compared with the Director's fields, drain report and classification |
| `rrw.stage.watch [on\|off\|zones\|nozones\|reset]` | Log every stage transition |
| `rrw.stage.gates [site\|all]` | Overview of the staged-traffic selectors with live readouts |
| `rrw.gate [<name> <value> \| defaults]` | Runtime selectors for staged-traffic variants (`closedb`, `soft`, `c4swap`, `d0sidewalks`, `deadend`, `fence`, `fencelat`, `divider`, `signs`, `signflip`, `amber`, `signalflip`, `conelamps`, `arrow`, `lineSrc`, `lineW`, `lineRound`, `lineQueue`, `lineLod`, `halfcovers`). No arguments prints them |
| `rrw.preview.errors` | Tool validation errors and warnings while a road preview hovers over a site |

### Director (`Src/Director`)

| Command | Description |
|---|---|
| `rrw.director.state` | Per project: phase, progress, accrual, model, release gate, machine allowance, trims, completion wait |
| `rrw.director.preview [list]` | Preview-hiding statistics. Visible previews of hidden works roads must be 0 |
| `rrw.director.nodes` | Every works end node: hidden or not, connected works and other edges |
| `rrw.director.trees [projectId] [list]` | Trees left in the clearing footprint or overhanging the strip |

### Tools (`Src/Tools`)

| Command | Description |
|---|---|
| `rrw.tools.last` | How the last tool apply was classified (new, replaced, split, combined, upgrade) and the last bulldozer intercept |

### Ground (`Src/Ground`)

| Command | Description |
|---|---|
| `rrw.ground.xsec <site> [t=0.5\|chain] [half=] [step=1] [v]` | Rendered terrain against grade and road surface: a cross-section at chain fraction `t`, or a long section along the chain |
| `rrw.ground.status` | Clone table: states, orphans, clones alive and pending cleanup, trigger rate, natural sampling stats |
| `rrw.ground.nat <site>` | Natural ground estimates per edge (centre, verge interpolation, base heightmap) and the saved values |
| `rrw.ground.floor <site\|all>` | Applied against target floor per edge, write lag and node slots |

### Surfaces (`Src/Surfaces`)

| Command | Description |
|---|---|
| `rrw.surf.list [edges]` | Summary, cover queues and the pieces of every layer per project (`edges`: per edge) |
| `rrw.surf.check` | Surface invariants: never-saved marker, tracked areas, holes in the covers, overlapping pieces |
| `rrw.surf.scars` | Verge and demolition scars with alpha and age |
| `rrw.surf.age <hours>` | Age every scar by this many in-game hours |
| `rrw.surf.fade <spawn\|swap>` | How scar fade steps are applied |
| `rrw.surf.rebuild` | Replace every strip and junction cap once |
| `rrw.surf.agri <0\|1>` | Alternative topsoil look (test only) |
| `rrw.surf.line <edge#> <Y1\|Y2\|Y3\|BLK> lat=<m> w=<m> …` / `info` / `list` / `clear` | Draw test lines on a road to compare temporary-marking looks |

### Props (`Src/Props`)

| Command | Description |
|---|---|
| `rrw.props.list [site]` | Props per project by kind, with heap, dust, beacon, owner and override counts |
| `rrw.props.check` | Prop invariants, including "props keep their height after terrain steps" |
| `rrw.props.respawn [site]` | Respawn props in the same slots, seeds and blink phases |
| `rrw.props.foreign [site]` | Non-works roads next to each chain (props stay off them) and props outside the trims |
| `rrw.props.y [site]` | Sample of props placed at an explicit height |
| `rrw.props.beacons [site]` | Beacon barriers per line with their blink phases |
| `rrw.props.clones` | Effect clone registration and the resolved prefabs |
| `rrw.props.dust [check\|auto\|clone\|vanilla]` | Which dust effect the dust heaps use |
| `rrw.props.puff <site> [n=1] …` / `at <x> <y> <z>` / `list` / `clear` | Spawn test dust puffs |
| `rrw.props.fx <prefab> <edge#> …` / `clear` / `list` | Lay a run of any static prop along a road (to test fences and signs) |
| `rrw.props.sig <target> <state>` | Set or watch the state of traffic-light props |

### Machines (`Src/Machines`)

| Command | Description |
|---|---|
| `rrw.mx.list` | Every machine: role, activity, leg, position, load, camera distance |
| `rrw.mx.check [watch=0\|1] [reset=1]` | Overlaps, floor contact, reverse lengths, never-saved markers, speed and acceleration against the limits, hand-over counters. `watch=1` repeats every 30 frames |
| `rrw.mx.zones [p<id>]` | The machine report: lane groups occupied now and planned, per project and per machine |
| `rrw.mx.plan <machine>` | A machine's plan legs, with times relative to now |
| `rrw.mx.trace <machine> [frames=300]` | Per-frame pose, speed, acceleration and limit trace with a summary |
| `rrw.mx.perf [seconds=10] [groups=mx,sx]` | Machines and Surfaces timing sections, counters and the worst update |
| `rrw.mx.pose …` | Show or tune the keyframe poses of the excavator, grader and loader |
| `rrw.mx.dig <machine\|all> [bite=<m>] [trace <s>] [pose]` | The IK dig cycle: key, events, dump target, truck clearance, tip error, piston alignment |
| `rrw.mx.roller [p<id>] [list\|spawn\|clear\|lod\|beacon]` | Road roller units: duty, pass, speed, vibration, LOD; force or clear rollers |
| `rrw.mx.sig <machine\|all> <0..3\|auto>` | Force the arrow board signal on a machine |
| `rrw.mx.ready [contract\|legacy]` | Which lane-group rule machines plan into |
| `rrw.mx.dust [0\|1]` | Bucket dust on the excavator (evaluation only) |

### Traffic (`Src/Traffic`)

| Command | Description |
|---|---|
| `rrw.tr.status` | Closures, selector states, invalidation and relocation counters, drain report per project |
| `rrw.tr.classify <site>` | Classification per edge: dependants, dead-end chain, tram track, stops, building sides |
| `rrw.tr.probe <site> [v] [dir=] [lanes=] [side=]` | Lane fields against the applied state per lane group (`MATCH` / `MISMATCH`) |
| `rrw.tr.paths <site> [closed\|open] [dir=] [lanes=] [side=] [humans]` | Path users of the site's lanes; through traffic must drop to 0 after a closure |
| `rrw.tr.inval <0\|1>` or `rrw.tr.inval <site> [dir=] [lanes=] [side=] [humans]` | Switch in-flight path invalidation, or invalidate the paths over a lane subset once |
| `rrw.tr.close <edges> <open\|slow\|soft\|closed\|blocked> [dir=] [lanes=] [side=] [kmh=] [tag=]` | Test closure of a lane subset on roads that are not under works (kept out of saves like works closures) |
| `rrw.tr.open <edges\|all> [tag=]` | Remove test closures |
| `rrw.tr.watch <edges> <everySimFrames> [samples=20]` / `stop` | Periodic report of path users, failed paths, stuck vehicles and residents' trips on the edges |
| `rrw.tr.home <edges>` | Residents, workers and pending paths of the buildings on the edges |

### UI (`Src/UI`)

| Command | Description |
|---|---|
| `rrw.ui.select <site> [edge]` | Select a site's road and open the panel |
| `rrw.ui.dump [sel\|<site>]` | The panel's numbers and texts for the selection or a site |
| `rrw.ui.texts` | Every traffic-row text from synthetic states |
| `rrw.ui.focus [sel\|<site>] [crew=i]` | Camera to the work front, like the Show work front button |
| `rrw.ui.click [stats\|probe\|reset]` | Click-to-select statistics, or what a click at the mouse would select |

### Persistence (`Src/Persistence`)

| Command | Description |
|---|---|
| `rrw.save.scan [now]` | Dry run of the save sanitiser (anything derived that would reach the save), lane field counters around the last save and load, dust puffs and roller objects |

## Typical sessions

**Watch a whole construction in a few minutes**

```
rrw.start 0 c
rrw.timescale 60 shift=ignore
rrw.cam #0 dist=200 pitch=35 follow=1
```

**Check a phase hand-over**

```
rrw.phase #0 C2 f=0.97
rrw.p #0 0.58
rrw.check
```

**Film a phase**

```
ui 0
overlay 0
rrw.phase #0 C1 f=0.4
rrw.rebuild
rrw.cam #0 dist=40 pitch=30 yaw=150 follow=1 crew=0
```

After a phase jump, `rrw.rebuild` respawns the machines at their planned positions, so nobody has to drive half the site first.
`tod 16` keeps the light the same between shots.

**Measure performance**

```
rrw.perf 10
rrw.mx.check reset=1
```

Reset the time scale with `rrw.timescale 1 shift=obey` when you are done.
