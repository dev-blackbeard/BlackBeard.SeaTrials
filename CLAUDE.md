# CLAUDE.md

This repository is **public**. Whatever is pushed is published for good, even if it is deleted a
minute later. Read this file before every commit and before every push.

## Where things live

- This repository holds source code, tests with synthetic fixtures, the JSON schema, scripts, CI
  workflows, `LICENSE`, `README.md` and this file. Nothing else.
- The specification, the plan and the decisions are in the private notes repository attached to the
  session. Read them there. Nothing from them, examples included, is copied into this repository.
- If a .NET build journal is attached to the session, consult it before touching `global.json`, a
  `.csproj`, `.props`, `.sln`, a workflow file or a NuGet feed config, and whenever a build or CI
  step fails.

## Never in this repository, by any route

By any route means a file, a comment, a test name, a sample, a commit message, a branch name, a pull
request title or body, an issue, or a CI log.

- Documentation of any kind: no `docs/`, no specification, plan, decisions, notes, kickoff text, or
  any Markdown or text file other than `README.md` and this file.
- Results: run folders, `run.json`, `summary.txt`, logs, reports, zips, exports, screenshots of
  real runs.
- Real request files. Only synthetic `samples/*.seatrials.json`.
- The name of anything that is not SeaTrials itself: a product, client, host system, data set,
  person, machine, path, repository, branch or commit id. Also machine data, credentials, and the
  denylist itself.
- Built programs and installers. CI builds them; they are not committed.

Tests and samples use synthetic names (`SampleRepo`, `alpha`, `acme`) and create their git
repositories and commit ids at run time.

## The sanity check

`scripts/sanity-check.py` (Python 3, standard library only) is the guard. The terms it refuses are
in the environment variable `SEATRIALS_DENYLIST` (one per line) or in the file named by
`SEATRIALS_DENYLIST_FILE`. Never print, echo, `cat`, commit or paste them, and never print the
environment. The check never prints what it matched; it reports a rule id, a place and a line number.
Look at the place, not at the output. If the denylist is missing the check exits with code 2.

1. After every commit: `python3 scripts/sanity-check.py --unpushed`.
2. Before a commit, to test a file or a message first: `python3 scripts/sanity-check.py --text <file>`.
3. Before sending any text to GitHub (a pull request, a comment, an issue): write it to a temporary
   file outside the repository, run `python3 scripts/sanity-check.py --text <file>`, then send it.
4. A `pre-push` hook (`scripts/hooks`) runs the same check on exactly what is being pushed. A
   `SessionStart` hook enables it; on another machine run `scripts/install-hooks.sh` (or `.ps1`).
   Never use `git push --no-verify`, and never push through a GitHub file-writing tool: use `git push`.
5. CI runs the check again as a backstop. A failure there means the content is already public. That
   is an incident, not a red build to fix quietly: stop and tell the owner.

If the check fails, **do not push**: not that branch, and not any branch that contains the failing
commit.

1. Find the last clean commit (`origin/main`, or the last commit that passed).
2. `git switch -c <new-branch> <last-clean-commit>` and redo the work **without** the offending
   content, so that it is in no commit of the new branch. Removing it in a later commit is not enough.
3. Run the check on the new branch. Push only the new branch. Delete the failed branch.
4. Tell the owner which rule failed and in which file, never the matched text.

If something does get out, or you suspect it has, stop and tell the owner at once.

## Invariants

- The harness never changes the user's checkout. The only git sub-commands it runs against a
  repository are `rev-parse`, `log`, `for-each-ref`, `show`, `status`, `fetch`,
  `worktree add|remove|prune|list`, `cat-file`, `submodule` (in a worktree only), `remote get-url`
  and `--version`, through one wrapper with an allow-list that a test pins. Never `checkout`,
  `reset`, `clean`, `stash`, `pull` or `merge` against the user's copy.
- `BlackBeard.SeaTrials.Core` has no UI and references no `System.Windows`. Windows-only pieces sit
  behind small interfaces so that Core's logic is tested on Linux.
- Core knows nothing about what it runs: a step is a command line; build, test and run are labels.
- .NET 10, nullable on, warnings as errors.

## Tests and CI

- xUnit with plain asserts or Shouldly. No FluentAssertions 8 or later. No GPL or commercial
  packages; if a package is wanted, say which and why in the pull request.
- Tests do real things in small: temporary git repositories, real commits, cheap commands, assertions
  on the files produced.
- The session may have no .NET SDK and no Windows. CI is then the compiler and the test run. Say in
  every pull request exactly what was and was not run, and never claim that tests pass for something
  only CI ran.
- Workflows use read-only permissions and versioned actions, never `pull_request_target`, and never
  print an environment variable, a secret or a path of the owner's machine. CI logs are public.
- Commit messages, branch names, pull request text and comments are public and are checked like code.
