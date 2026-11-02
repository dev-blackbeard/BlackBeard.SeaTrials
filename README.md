# BlackBeard.SeaTrials

Run queued build, test and measurement commands against exact git commits in private worktrees,
check the output, and keep every log and result. A Windows desktop app and command line for
repeatable measurements on a real PC. .NET 10, WPF.

Status: early development. The core library and its tests are being built one slice at a time; there
is no installer and no app yet.

## Build and test

You need the .NET 10 SDK and git.

```bash
dotnet build SeaTrials.sln -c Release
dotnet test SeaTrials.sln -c Release
```

## Contributing

This repository is public, and a guard protects it: a Python 3 script, `scripts/sanity-check.py`,
runs in a `pre-push` hook and again in CI. Enable the hook once per clone with
`scripts/install-hooks.sh` (or `scripts/install-hooks.ps1` on Windows). The guard needs the
`SEATRIALS_DENYLIST` environment variable set to the maintainer's list of terms; without it a push
is refused. Read `CLAUDE.md` for the rules.

## Licence

See `LICENSE`.
