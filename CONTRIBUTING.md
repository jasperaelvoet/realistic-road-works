# Contributing

Thanks for helping out. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with:

- what you did and what you expected to see;
- `Logs/RealisticRoadWorks.log` from the game's user data folder (next to `Mods`). Turn on **Detailed log** in the mod's
  Maintenance settings first if you can reproduce the problem;
- a screenshot of the Road works panel or the site, if it is a visual problem;
- the game version and any other mods that touch roads, traffic or terrain.

## Pull requests

1. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first. In short: features live in their module folder under `Src/`, modules
   talk to each other only through `Src/Core`, and every shared field has exactly one writing module.
2. Build both variants before you open the PR. See [docs/BUILDING.md](docs/BUILDING.md):
   - the release build (`-p:DevTools=false`);
   - the DevTools build (`-p:DevTools=true`).

   Code outside `Dev/` and `Src/Dev` may reference dev-only types only inside `#if DEVTOOLS`.
3. Test in game on a throwaway save. `rrw.check` and the module checks should stay clean, and `rrw.perf` should not get worse with
   your change (the budgets in the architecture document are the targets). See [docs/DEV-COMMANDS.md](docs/DEV-COMMANDS.md).
4. Keep a PR to one topic, and describe what changed in game, ideally with a screenshot or a short clip.

## Code style

- C# 9 for .NET Framework 4.8, four-space indentation (see `.editorconfig`).
- No Burst, no LINQ or allocations in per-frame code paths, no structural changes outside the phases listed in the architecture
  document.
- Log through `RRWLog` with your module's prefix. Per-frame logging only under verbose logging.
- Only vanilla game assets or assets generated in code. Do not add third-party models or textures, and do not paste decompiled game
  code into the repository.

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
