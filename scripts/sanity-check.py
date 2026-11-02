#!/usr/bin/env python3
"""Sanity check for the public repository. Python 3, standard library only.

It looks for things that must never be published in: every commit (added and
changed lines, paths, message, author and committer), the whole tree at a
revision, branch names, and free text such as a pull request description.

It NEVER prints the text it matched. A finding is a rule id, a place and a line
number, because the logs of a public repository's CI are public.

Exit codes:  0 clean   1 findings   2 the check could not run.
Both non-zero codes must block a push. There is no switch that skips a rule.

Rules:
  D1  a term of the denylist (SEATRIALS_DENYLIST, one term per line, or the file
      named by SEATRIALS_DENYLIST_FILE; a line starting "re:" is a regular
      expression); case-insensitive; no denylist at all is exit code 2
  P1  a path outside the allow-list of file kinds
  S1  credentials: common token formats and literal secret assignments
  M1  machine data: user-profile paths, e-mail addresses, IPv4 addresses,
      machine names
  R1  other repositories: addresses of repositories that are not this one,
      full 40-hex commit ids (except the synthetic 000...0001 form)

Modes (combine freely):
  --unpushed            commits of HEAD that are on no origin/* ref, the tree at
                        HEAD and the current branch name
  --range A..B          the commits of a range, the tree at HEAD
  --history             every commit reachable from HEAD, the tree at HEAD
  --pre-push REMOTE...  what a pre-push hook is told on standard input
  --tree [REV]          only the tree at REV (default HEAD)
  --branch NAME         a branch or tag name about to be pushed (repeatable)
  --text FILE           free text or any file's lines, content rules only
  --selftest            test the check itself, with synthetic terms only
"""

import argparse
import os
import posixpath
import re
import subprocess
import sys

OK, FINDINGS, ERROR = 0, 1, 2

# Lower-case "owner/name" of this repository.
OWN_REPOSITORY = "dev-blackbeard/blackbeard.seatrials"
# Owners whose public repositories may be linked to (actions in workflows, etc.).
KNOWN_GITHUB_OWNERS = frozenset({"actions", "dotnet", "xunit", "shouldly"})
ZERO_OID = "0" * 40
MAX_REPORTED = 100
SCRIPT_PATH = os.path.abspath(__file__)


class CheckError(Exception):
    """The check could not run (exit code 2). The message is safe to print."""


# --------------------------------------------------------------------------
# The denylist
# --------------------------------------------------------------------------

class Denylist:
    """Terms that must never appear. Never printed, never written anywhere."""

    def __init__(self, lines, allow_empty=False):
        self._entries = []
        for number, line in enumerate(lines, 1):
            term = line.strip()
            if not term:
                continue
            if term.startswith("re:"):
                try:
                    pattern = re.compile(term[3:].strip(), re.IGNORECASE)
                except re.error:
                    raise CheckError(
                        "denylist entry %d is not a valid regular expression" % number
                    ) from None
                self._entries.append((number, pattern, None))
            else:
                self._entries.append((number, None, term.lower()))
        if not self._entries and not allow_empty:
            raise CheckError("the denylist is empty")

    def match(self, text):
        """Return the number of the first matching entry, or None."""
        lowered = text.lower()
        for number, pattern, term in self._entries:
            if pattern is not None:
                if pattern.search(text):
                    return number
            elif term in lowered:
                return number
        return None


def _inside_git_work_tree(path):
    directory = os.path.dirname(os.path.abspath(path)) or "."
    try:
        proc = subprocess.run(
            ["git", "-C", directory, "rev-parse", "--is-inside-work-tree"],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False,
        )
    except OSError:
        return False
    return proc.returncode == 0 and proc.stdout.strip() == b"true"


def load_denylist(environ, allow_missing=False):
    lines = []
    value = environ.get("SEATRIALS_DENYLIST", "")
    if value.strip():
        lines.extend(value.splitlines())
    path = environ.get("SEATRIALS_DENYLIST_FILE", "").strip()
    if path:
        if _inside_git_work_tree(path):
            raise CheckError("the denylist file is inside a git working tree; move it out")
        try:
            with open(path, encoding="utf-8-sig") as handle:
                lines.extend(handle.read().splitlines())
        except OSError:
            raise CheckError("SEATRIALS_DENYLIST_FILE cannot be read") from None
    if not any(line.strip() for line in lines):
        if allow_missing:
            return Denylist([], allow_empty=True)
        raise CheckError(
            "no denylist: set SEATRIALS_DENYLIST or SEATRIALS_DENYLIST_FILE "
            "(the check fails closed, so nothing may be pushed)"
        )
    return Denylist(lines)


# --------------------------------------------------------------------------
# Line rules: S1, M1, R1 (and D1 through the denylist)
# --------------------------------------------------------------------------

_TOKEN_PATTERNS = [
    re.compile(r"gh[pousr]_[A-Za-z0-9]{30,}"),
    re.compile(r"github_pat_[A-Za-z0-9_]{20,}"),
    re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
    re.compile(r"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----"),
    re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{10,}"),
    re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}"),
    re.compile(r"\bbearer\s+[A-Za-z0-9._~+/=-]{20,}", re.IGNORECASE),
]
_SECRET_NAME = r"(?:password|passwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)"
_ASSIGN_QUOTED = re.compile(
    _SECRET_NAME + r"""["']?\s*[:=]\s*(?:"([^"]{4,})"|'([^']{4,})')""", re.IGNORECASE
)
_ASSIGN_BARE = re.compile(
    r"""^\s*["']?[\w.-]*""" + _SECRET_NAME
    + r"""["']?\s*[:=]\s*([A-Za-z0-9+/_=-]{8,})\s*,?\s*(?:#.*)?$""",
    re.IGNORECASE,
)
_PLACEHOLDER_WORDS = frozenset({
    "example", "sample", "changeme", "change-me", "placeholder", "redacted", "dummy",
    "password", "secret", "token", "value", "none", "null", "true", "false",
    "string", "required", "hidden", "masked",
})

_WIN_PROFILE = re.compile(
    r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]+Users[\\/]+([^\\/\s\"'<>|:*?]+)", re.IGNORECASE
)
_MAC_PROFILE = re.compile(r"(?<![A-Za-z0-9_.~-])/Users/([^/\s\"'<>|:*?]+)")
_LINUX_HOME = re.compile(r"(?<![A-Za-z0-9_.~-])/home/([^/\s\"'<>|:*?]+)")
_PROFILE_PLACEHOLDERS = frozenset({
    "user", "username", "name", "you", "me", "example", "sample", "public", "default",
    "all", "runner", "runneradmin", "yourname", "your-name",
})
_EMAIL = re.compile(
    r"[A-Za-z0-9]([A-Za-z0-9._%+-]*)@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})"
)
_IPV4 = re.compile(r"(?<![\w.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?!\w)")
_DOCUMENTATION_NETWORKS = frozenset({(192, 0, 2), (198, 51, 100), (203, 0, 113)})
_MACHINE_NAME = re.compile(r"\b(?:DESKTOP|LAPTOP|WIN)-[A-Z0-9]{6,}\b")

_GITHUB_ADDRESS = re.compile(
    r"(?:github\.com[/:]+|raw\.githubusercontent\.com/|api\.github\.com/repos/)"
    r"([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)",
    re.IGNORECASE,
)
_WORKFLOW_USES = re.compile(r"^\s*-?\s*uses:\s*([A-Za-z0-9_.-]+)/")
_HEX40 = re.compile(r"(?<![0-9A-Fa-f])[0-9A-Fa-f]{40}(?![0-9A-Fa-f])")


def _is_placeholder_value(value):
    lowered = value.strip().lower()
    if not lowered or lowered[0] in "<{$%*[(":
        return True
    if set(lowered) <= set("x*.-_#"):
        return True
    return lowered in _PLACEHOLDER_WORDS


def _profile_name_is_placeholder(name):
    lowered = name.strip().lower()
    return (not lowered) or (not lowered[0].isalnum()) or lowered in _PROFILE_PLACEHOLDERS


def _email_is_allowed(local, domain):
    local, domain = local.lower(), domain.lower()
    if "noreply" in local or "no-reply" in local or "noreply" in domain:
        return True
    if local == "git":
        return True
    return domain in ("example.com", "example.org", "example.net") or domain.endswith(
        (".example", ".invalid", ".test")
    )


def _repository_is_allowed(owner, name):
    owner, name = owner.lower(), name.lower().rstrip(".")
    if name.endswith(".git"):
        name = name[:-4]
    return owner in KNOWN_GITHUB_OWNERS or ("%s/%s" % (owner, name)) == OWN_REPOSITORY


class Rules:
    def __init__(self, denylist):
        self.denylist = denylist

    def check_line(self, text, path=None):
        """Return a list of (rule id, denylist entry number or None)."""
        found = []
        entry = self.denylist.match(text)
        if entry is not None:
            found.append(("D1", entry))
        if self._has_secret(text):
            found.append(("S1", None))
        if self._has_machine_data(text):
            found.append(("M1", None))
        if self._has_foreign_repository(text, path):
            found.append(("R1", None))
        return found

    @staticmethod
    def _has_secret(text):
        if any(pattern.search(text) for pattern in _TOKEN_PATTERNS):
            return True
        for match in _ASSIGN_QUOTED.finditer(text):
            if not _is_placeholder_value(match.group(1) or match.group(2) or ""):
                return True
        bare = _ASSIGN_BARE.match(text)
        if bare:
            value = bare.group(1)
            if (any(c.isdigit() for c in value) and any(c.isalpha() for c in value)
                    and not _is_placeholder_value(value)):
                return True
        return False

    @staticmethod
    def _has_machine_data(text):
        for pattern in (_WIN_PROFILE, _MAC_PROFILE, _LINUX_HOME):
            for match in pattern.finditer(text):
                if not _profile_name_is_placeholder(match.group(1)):
                    return True
        for match in _EMAIL.finditer(text):
            local = text[match.start():match.start(2) - 1]
            if not _email_is_allowed(local, match.group(2)):
                return True
        for match in _IPV4.finditer(text):
            octets = [int(part) for part in match.groups()]
            if all(octet <= 255 for octet in octets) and tuple(octets[:3]) not in _DOCUMENTATION_NETWORKS:
                return True
        return bool(_MACHINE_NAME.search(text))

    @staticmethod
    def _has_foreign_repository(text, path):
        for match in _GITHUB_ADDRESS.finditer(text):
            if not _repository_is_allowed(match.group(1), match.group(2)):
                return True
        for match in _HEX40.finditer(text):
            if match.group(0)[:39] != "0" * 39:
                return True
        if path is not None and path.lower().endswith(".yml"):
            uses = _WORKFLOW_USES.match(text)
            if uses and uses.group(1) not in (".", "..") and uses.group(1).lower() not in KNOWN_GITHUB_OWNERS:
                return True
        return False


# --------------------------------------------------------------------------
# Path rule: P1
# --------------------------------------------------------------------------

_ALLOWED_EXTENSIONS = frozenset({
    ".cs", ".xaml", ".csproj", ".props", ".sln", ".ps1", ".py", ".sh", ".yml", ".json",
})
_ALLOWED_PATHS = frozenset({
    "LICENSE", "THIRD-PARTY-NOTICES.txt", "README.md", "CLAUDE.md",
    ".gitignore", ".gitattributes", ".editorconfig",
    "scripts/hooks/pre-push",
})
_FORBIDDEN_DIRECTORIES = frozenset({
    "docs", "notes", "runs", "results", "artifacts", "bin", "obj", "testresults", ".vs",
    "node_modules",
})
_FORBIDDEN_NAMES = frozenset({"run.json", "summary.txt"})


def path_is_allowed(path):
    parts = path.split("/")
    if any(part.lower() in _FORBIDDEN_DIRECTORIES for part in parts[:-1]):
        return False
    name = parts[-1].lower()
    if name in _FORBIDDEN_NAMES:
        return False
    if name.endswith(".seatrials.json"):
        return len(parts) == 2 and parts[0] == "samples"
    if path in _ALLOWED_PATHS:
        return True
    return posixpath.splitext(name)[1] in _ALLOWED_EXTENSIONS


# --------------------------------------------------------------------------
# Findings
# --------------------------------------------------------------------------

class Report:
    def __init__(self, rules):
        self.rules = rules
        self.findings = []
        self._seen = set()
        self.counts = {"commits": 0, "tree files": 0, "texts": 0, "names": 0}

    def display_path(self, path):
        """A path that is safe to print: never one that matches the denylist."""
        if self.rules.denylist.match(path) is not None:
            return "<name-matches-denylist>"
        return path.encode("ascii", "backslashreplace").decode("ascii")

    def add(self, rule, scope, where, entry=None):
        key = (rule, scope, where)
        if key in self._seen:
            return
        self._seen.add(key)
        self.findings.append((rule, scope, where, entry))

    def add_line(self, scope, display, line_number, text, path=None):
        for rule, entry in self.rules.check_line(text, path):
            self.add(rule, scope, "%s:%d" % (display, line_number), entry)

    def add_path(self, scope, path):
        display = self.display_path(path)
        if not path_is_allowed(path):
            self.add("P1", scope, display)
        entry = self.rules.denylist.match(path)
        if entry is not None:
            self.add("D1", scope, display, entry)

    def lines(self):
        out = []
        for rule, scope, where, entry in self.findings[:MAX_REPORTED]:
            suffix = " entry=%d" % entry if entry is not None else ""
            out.append("FAIL %s %s %s%s" % (rule, scope, where, suffix))
        if len(self.findings) > MAX_REPORTED:
            out.append("... and %d more findings" % (len(self.findings) - MAX_REPORTED))
        return out


# --------------------------------------------------------------------------
# Git
# --------------------------------------------------------------------------

def run_git(repo, args, stdin=None, allow_failure=False):
    command = ["git", "-c", "core.quotepath=off", "-C", repo] + args
    try:
        proc = subprocess.run(
            command, input=stdin, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False
        )
    except OSError:
        raise CheckError("git was not found") from None
    if proc.returncode != 0:
        if allow_failure:
            return None
        # git's own message can quote names, so only the sub-command is reported.
        raise CheckError("git %s failed (exit %d)" % (args[0], proc.returncode))
    return proc.stdout


def find_repository(path):
    out = run_git(path or ".", ["rev-parse", "--show-toplevel"])
    return out.decode("utf-8", "replace").strip()


def rev_list(repo, args):
    out = run_git(repo, ["rev-list", "--reverse"] + args)
    return [line for line in out.decode("ascii", "replace").split() if line]


def current_branch(repo):
    out = run_git(repo, ["symbolic-ref", "--short", "-q", "HEAD"], allow_failure=True)
    return out.decode("utf-8", "replace").strip() if out else None


def short(sha):
    return sha[:12]


def _unquote(raw):
    """Undo git's C-style quoting of a path in a patch header."""
    raw = raw.rstrip(b"\t")
    if not (raw.startswith(b'"') and raw.endswith(b'"')):
        return raw.decode("utf-8", "replace")
    body, out, i = raw[1:-1], bytearray(), 0
    escapes = {b"n": b"\n", b"t": b"\t", b"r": b"\r", b'"': b'"', b"\\": b"\\"}
    while i < len(body):
        char = body[i:i + 1]
        if char == b"\\" and i + 1 < len(body):
            nxt = body[i + 1:i + 2]
            if nxt in escapes:
                out += escapes[nxt]
                i += 2
                continue
            octal = body[i + 1:i + 4]
            if re.fullmatch(rb"[0-7]{3}", octal):
                out.append(int(octal, 8))
                i += 4
                continue
        out += char
        i += 1
    return out.decode("utf-8", "replace")


_HUNK = re.compile(rb"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")


def parse_added_lines(patch):
    """Yield (path, line number, bytes) for each added line and (path, None, None)
    for each binary change, from a patch made with -U0."""
    path, line_number, in_hunk = None, 0, False
    for raw in patch.split(b"\n"):
        if raw.startswith(b"diff --git "):
            path, in_hunk = None, False
            continue
        if not in_hunk:
            if raw.startswith(b"+++ "):
                target = _unquote(raw[4:])
                path = target[2:] if target.startswith("b/") else target
            elif raw.startswith(b"Binary files ") and raw.endswith(b" differ"):
                target = raw[:-len(b" differ")].rsplit(b" and ", 1)[-1]
                target = _unquote(target)
                yield (target[2:] if target.startswith("b/") else target), None, None
            else:
                hunk = _HUNK.match(raw)
                if hunk:
                    line_number, in_hunk = int(hunk.group(1)), True
            continue
        hunk = _HUNK.match(raw)
        if hunk:
            line_number = int(hunk.group(1))
        elif raw.startswith(b"+"):
            yield path, line_number, raw[1:]
            line_number += 1


# --------------------------------------------------------------------------
# Scanning
# --------------------------------------------------------------------------

def scan_commit(repo, sha, report):
    scope = "commit %s" % short(sha)
    meta = run_git(repo, ["log", "-1", "--format=%an%x00%ae%x00%cn%x00%ce%x00%P%x00%B", sha])
    fields = meta.decode("utf-8", "replace").split("\x00", 5)
    if len(fields) != 6:
        raise CheckError("unexpected git log output")
    author_name, author_mail, committer_name, committer_mail, parents, message = fields
    report.add_line(scope, "author", 1, "%s <%s>" % (author_name, author_mail))
    report.add_line(scope, "committer", 1, "%s <%s>" % (committer_name, committer_mail))
    for number, line in enumerate(message.splitlines(), 1):
        report.add_line(scope, "message", number, line)

    parent_list = parents.split()
    base = [parent_list[0], sha] if parent_list else ["--root", sha]
    names = run_git(repo, [
        "diff-tree", "-r", "-z", "--name-only", "--no-commit-id", "--diff-filter=ACMRT", "-M",
    ] + base)
    for raw in names.split(b"\0"):
        if raw:
            report.add_path(scope, raw.decode("utf-8", "replace"))
    patch = run_git(repo, [
        "diff-tree", "-r", "-p", "-U0", "--no-color", "--no-ext-diff", "--no-textconv",
        "--src-prefix=a/", "--dst-prefix=b/", "-M", "--diff-filter=ACMRT", "--no-commit-id",
    ] + base)
    for path, line_number, raw in parse_added_lines(patch):
        display = report.display_path(path or "?")
        if raw is None:
            report.add("P1", scope, display)
            continue
        text = raw.decode("utf-8", "replace").rstrip("\r")
        report.add_line(scope, display, line_number, text, path)
    report.counts["commits"] += 1


def scan_tree(repo, rev, report):
    listing = run_git(repo, ["ls-tree", "-r", "-z", "--full-tree", rev])
    resolved = run_git(repo, ["rev-parse", "--verify", rev + "^{tree}"]).decode("ascii", "replace").strip()
    scope = "tree %s" % short(resolved)
    blobs = []
    for entry in listing.split(b"\0"):
        if not entry:
            continue
        meta, _, raw_path = entry.partition(b"\t")
        mode, kind, oid = meta.decode("ascii", "replace").split(" ")
        path = raw_path.decode("utf-8", "replace")
        report.add_path(scope, path)
        if kind == "commit":
            report.add("P1", scope, report.display_path(path))  # a submodule
        elif kind == "blob":
            blobs.append((path, oid))
    unique = sorted({oid for _, oid in blobs})
    contents = {}
    if unique:
        data = run_git(repo, ["cat-file", "--batch"], stdin=("\n".join(unique) + "\n").encode("ascii"))
        position = 0
        for oid in unique:
            newline = data.index(b"\n", position)
            header = data[position:newline].split(b" ")
            if len(header) != 3 or header[1] != b"blob":
                raise CheckError("unexpected cat-file output")
            size = int(header[2])
            contents[oid] = data[newline + 1:newline + 1 + size]
            position = newline + 1 + size + 1
    for path, oid in blobs:
        content = contents[oid]
        display = report.display_path(path)
        if b"\0" in content:
            report.add("P1", scope, display)  # binary content
        else:
            for number, line in enumerate(content.decode("utf-8", "replace").splitlines(), 1):
                report.add_line(scope, display, number, line, path)
        report.counts["tree files"] += 1


def scan_text_file(path, index, report):
    try:
        with open(path, "rb") as handle:
            content = handle.read()
    except OSError:
        raise CheckError("a --text file cannot be read") from None
    display = report.display_path(os.path.basename(path) or "text%d" % index)
    for number, line in enumerate(content.decode("utf-8", "replace").splitlines(), 1):
        report.add_line("text", display, number, line, os.path.basename(path))
    report.counts["texts"] += 1


def scan_name(name, index, report):
    # A name is never printed: it is the text a rule may have matched.
    for rule, entry in report.rules.check_line(name):
        report.add(rule, "branch", "ref#%d" % index, entry)
    report.counts["names"] += 1


def _strip_ref(ref):
    for prefix in ("refs/heads/", "refs/tags/"):
        if ref.startswith(prefix):
            return ref[len(prefix):]
    return ref


def pre_push_targets(repo, remote, stdin_lines):
    """Turn the lines git gives a pre-push hook into (commits, revisions, names)."""
    commits, revisions, names = [], [], []
    for line in stdin_lines:
        parts = line.split()
        if len(parts) != 4:
            continue
        local_ref, local_oid, remote_ref, remote_oid = parts
        if set(local_oid) == {"0"}:
            continue  # a deletion pushes no content
        args = [local_oid, "--not"]
        known = set(remote_oid) != {"0"} and run_git(
            repo, ["cat-file", "-e", remote_oid + "^{commit}"], allow_failure=True) is not None
        args += [remote_oid] if known else ["--remotes=" + remote]
        for sha in rev_list(repo, args):
            if sha not in commits:
                commits.append(sha)
        revisions.append(local_oid)
        names += [_strip_ref(local_ref), _strip_ref(remote_ref)]
    return commits, revisions, names


# --------------------------------------------------------------------------
# Command line
# --------------------------------------------------------------------------

def build_parser():
    parser = argparse.ArgumentParser(
        prog="sanity-check.py",
        description="Look for what must not be published. Never prints what it matched.",
    )
    parser.add_argument("--selftest", action="store_true", help="test the check itself")
    parser.add_argument("--unpushed", action="store_true",
                        help="commits on no origin/* ref, the tree at HEAD, the branch name")
    parser.add_argument("--range", dest="ranges", action="append", default=[], metavar="A..B",
                        help="the commits of a revision range; also the tree at HEAD")
    parser.add_argument("--history", action="store_true",
                        help="every commit reachable from HEAD; also the tree at HEAD")
    parser.add_argument("--pre-push", nargs="+", metavar="REMOTE",
                        help="as a pre-push hook: REMOTE [URL], ref lines on standard input")
    parser.add_argument("--tree", nargs="?", const="HEAD", metavar="REV",
                        help="only the tree at REV (default HEAD)")
    parser.add_argument("--branch", action="append", default=[], metavar="NAME",
                        help="a branch or tag name about to be pushed")
    parser.add_argument("--text", action="append", default=[], metavar="FILE",
                        help="free text (a pull request, a comment) or any file")
    parser.add_argument("--no-denylist", action="store_true",
                        help="CI only, for pull requests from forks, which get no secrets: "
                             "rule D1 is NOT checked")
    parser.add_argument("--repo", metavar="DIR", help="the repository (default: the current one)")
    return parser


def run(args, environ, stdin):
    if args.no_denylist and environ.get("GITHUB_ACTIONS") != "true":
        raise CheckError("--no-denylist is only accepted in GitHub Actions")
    needs_git = bool(args.unpushed or args.ranges or args.history or args.pre_push or args.tree)
    if not (needs_git or args.branch or args.text):
        raise CheckError("nothing to check: give a mode (see --help)")
    denylist = load_denylist(environ, allow_missing=args.no_denylist)
    report = Report(Rules(denylist))

    commits, revisions, names = [], [], list(args.branch)
    repo = None
    if needs_git:
        repo = find_repository(args.repo)

        def add_commits(found):
            for sha in found:
                if sha not in commits:
                    commits.append(sha)

        if args.unpushed:
            add_commits(rev_list(repo, ["HEAD", "--not", "--remotes=origin"]))
            revisions.append("HEAD")
            branch = current_branch(repo)
            if branch:
                names.append(branch)
        for spec in args.ranges:
            add_commits(rev_list(repo, [spec]))
            revisions.append("HEAD")
        if args.history:
            add_commits(rev_list(repo, ["HEAD"]))
            revisions.append("HEAD")
        if args.tree:
            revisions.append(args.tree)
        if args.pre_push:
            pushed, pushed_revisions, pushed_names = pre_push_targets(
                repo, args.pre_push[0], stdin.read().splitlines())
            add_commits(pushed)
            revisions += pushed_revisions
            names += pushed_names

    for sha in commits:
        scan_commit(repo, sha, report)
    for rev in dict.fromkeys(revisions):
        scan_tree(repo, rev, report)
    for index, name in enumerate(names, 1):
        scan_name(name, index, report)
    for index, path in enumerate(args.text, 1):
        scan_text_file(path, index, report)

    if args.no_denylist:
        print("NOTE: no denylist in this run (a pull request from a fork): rule D1 was NOT checked.")
    if report.findings:
        for line in report.lines():
            print(line)
        print("%d finding(s). Do not push. Look at the place named, not at this output; "
              "see CLAUDE.md for what to do." % len(report.findings))
        return FINDINGS
    scanned = ", ".join("%d %s" % (n, k) for k, n in report.counts.items() if n)
    print("OK: %s." % (scanned or "nothing to scan"))
    return OK


def main(argv=None, environ=None, stdin=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="backslashreplace")
    environ = os.environ if environ is None else environ
    args = build_parser().parse_args(argv)
    try:
        if args.selftest:
            return run_selftest()
        return run(args, environ, stdin or sys.stdin)
    except CheckError as error:
        print("ERROR: %s" % error, file=sys.stderr)
        return ERROR
    except Exception as error:  # never print the message: it could hold matched text
        print("ERROR: internal error (%s)" % type(error).__name__, file=sys.stderr)
        if environ.get("SEATRIALS_CHECK_DEBUG"):
            raise
        return ERROR


# --------------------------------------------------------------------------
# Self-test: synthetic terms only; every sample that a rule would refuse is
# assembled from pieces so that this file passes its own check.
# --------------------------------------------------------------------------

def run_selftest():
    import shutil
    import tempfile

    failures, passed = [], [0]

    def check(condition, label):
        if condition:
            passed[0] += 1
        else:
            failures.append(label)

    term = "zorblax-sample"
    regex_hit = "quuxnet-" + "42"
    denylist_text = term + "\n" + "re:quuxnet-[0-9]+" + "\n"
    base = tempfile.mkdtemp(prefix="sanity-selftest-")

    def environment(denylist=denylist_text, **extra):
        env = {k: v for k, v in os.environ.items()
               if not k.startswith("SEATRIALS_") and k != "GITHUB_ACTIONS"}
        env.update({
            "GIT_AUTHOR_NAME": "Sample Author", "GIT_AUTHOR_EMAIL": "noreply@example.invalid",
            "GIT_COMMITTER_NAME": "Sample Author", "GIT_COMMITTER_EMAIL": "noreply@example.invalid",
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull, "GIT_TERMINAL_PROMPT": "0",
            "HOME": base,
        })
        if denylist is not None:
            env["SEATRIALS_DENYLIST"] = denylist
        env.update(extra)
        return env

    def git(repo, *args, **extra):
        subprocess.run(["git", "-C", repo] + list(args), env=environment(**extra), check=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    counter = [0]

    def new_repo():
        counter[0] += 1
        repo = os.path.join(base, "repo%d" % counter[0])
        os.makedirs(repo)
        git(repo, "init", "-q", "-b", "main")
        return repo

    def commit(repo, files, message="sample change", **extra):
        for name, content in files.items():
            target = os.path.join(repo, *name.split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            if content is None:
                os.remove(target)
            else:
                with open(target, "wb") as handle:
                    handle.write(content if isinstance(content, bytes) else content.encode("utf-8"))
        git(repo, "add", "-A")
        git(repo, "commit", "-q", "-m", message, **extra)
        out = subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"], stdout=subprocess.PIPE, check=True)
        return out.stdout.decode().strip()

    def cli(args, repo=None, stdin=None, **env_args):
        proc = subprocess.run(
            [sys.executable, SCRIPT_PATH] + args, cwd=repo or base, env=environment(**env_args),
            input=stdin, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
        )
        return proc.returncode, (proc.stdout + proc.stderr).decode("utf-8", "replace")

    def refused(label, args, repo, expect_rule, where=None, leak=None, **kw):
        code, output = cli(args, repo, **kw)
        check(code == FINDINGS, label + ": exit 1 (got %d)" % code)
        check(("FAIL %s " % expect_rule) in output, label + ": names rule " + expect_rule)
        if where:
            check(where in output, label + ": names the place")
        for leaked in [leak, term, regex_hit]:
            if leaked:
                check(leaked.lower() not in output.lower(), label + ": output does not repeat the match")
        return output

    try:
        # ---- D1 ----------------------------------------------------------
        repo = new_repo()
        commit(repo, {"src/a.cs": "// one\n// two\n// see " + term.upper() + " here\n"})
        refused("D1 diff line, case-insensitive", ["--history"], repo, "D1", "src/a.cs:3")

        repo = new_repo()
        commit(repo, {"src/a.cs": "// fine\n"}, message="subject\n\nbody mentions " + term)
        refused("D1 commit message", ["--history"], repo, "D1", "message:3")

        repo = new_repo()
        commit(repo, {"src/a.cs": "// fine\n"})
        refused("D1 branch name", ["--branch", "feature/" + term], repo, "D1", "branch ref#1")

        repo = new_repo()
        commit(repo, {"src/" + term + ".cs": "// fine\n"})
        refused("D1 path name", ["--history"], repo, "D1", "<name-matches-denylist>")

        repo = new_repo()
        commit(repo, {"src/a.cs": "// fine\n"}, GIT_AUTHOR_NAME="Person " + term)
        refused("D1 author", ["--history"], repo, "D1", "author:1")

        repo = new_repo()
        commit(repo, {"src/a.cs": "// " + regex_hit + "\n"})
        refused("D1 regular expression entry", ["--history"], repo, "D1", "src/a.cs:1")
        repo = new_repo()
        commit(repo, {"src/a.cs": "// quuxnet-x\n"})
        code, _ = cli(["--history"], repo)
        check(code == OK, "D1 regular expression entry does not match other text")

        repo = new_repo()
        first = commit(repo, {"src/a.cs": "// " + term + "\n"})
        commit(repo, {"src/a.cs": "// fine\n"})
        output = refused("D1 history: added then removed", ["--history"], repo, "D1", "commit " + first[:12])
        check("tree" not in output, "D1 history: the final tree is clean")

        repo = new_repo()
        commit(repo, {"src/a.cs": "// " + term + "\n"})
        refused("D1 tree only", ["--tree"], repo, "D1", "tree ")

        text_file = os.path.join(base, "message.txt")
        with open(text_file, "w", encoding="utf-8") as handle:
            handle.write("a title\n\nthe body has " + term + " in it\n")
        refused("D1 free text", ["--text", text_file], None, "D1", "text message.txt:3")
        named_file = os.path.join(base, term + "-note.txt")
        with open(named_file, "w", encoding="utf-8") as handle:
            handle.write("a title\n\nthe body has " + term + " in it\n")
        refused("D1 free text in a file named after a term", ["--text", named_file], None, "D1",
                "text <name-matches-denylist>:3")
        with open(text_file, "w", encoding="utf-8") as handle:
            handle.write("a title\n\nnothing to see\n")
        code, _ = cli(["--text", text_file])
        check(code == OK, "free text: clean text passes")

        # ---- clean, and the unpushed range ---------------------------------
        origin = os.path.join(base, "origin.git")
        os.makedirs(origin)
        git(origin, "init", "-q", "--bare", "-b", "main")
        repo = new_repo()
        first = commit(repo, {"src/a.cs": "// fine\n"})
        git(repo, "remote", "add", "origin", origin)
        git(repo, "push", "-q", "origin", "main")
        git(repo, "fetch", "-q", "origin")
        code, output = cli(["--unpushed"], repo)
        check(code == OK, "unpushed: nothing to check is clean (got %d)" % code)
        second = commit(repo, {"src/b.cs": "// " + term + "\n"})
        output = refused("D1 unpushed", ["--unpushed"], repo, "D1", "commit " + second[:12])
        check(first[:12] not in output, "unpushed: a commit on origin is not rescanned")
        lines = "refs/heads/main %s refs/heads/main %s\n" % (second, first)
        refused("pre-push input", ["--pre-push", "origin", "url"], repo, "D1", "commit " + second[:12], stdin=lines.encode())
        deletion = "(delete) %s refs/heads/old %s\n" % (ZERO_OID, first)
        code, _ = cli(["--pre-push", "origin", "url"], repo, stdin=deletion.encode())
        check(code == OK, "pre-push: a deletion pushes no content")

        # ---- the check fails closed ---------------------------------------
        repo = new_repo()
        commit(repo, {"src/a.cs": "// fine\n"})
        for label, kw in (("missing", {"denylist": None}), ("empty", {"denylist": "\n  \n"})):
            code, _ = cli(["--history"], repo, **kw)
            check(code == ERROR, "denylist %s: exit 2 (got %d)" % (label, code))
        code, output = cli(["--history"], repo, denylist="re:(unclosed\n")
        check(code == ERROR and "unclosed" not in output, "invalid regular expression: exit 2, not repeated")
        inside = os.path.join(repo, "list.txt")
        with open(inside, "w") as handle:
            handle.write(term + "\n")
        code, _ = cli(["--history"], repo, denylist=None, SEATRIALS_DENYLIST_FILE=inside)
        check(code == ERROR, "denylist file inside a working tree: exit 2")
        outside = os.path.join(base, "outside-list.txt")
        with open(outside, "w") as handle:
            handle.write(term + "\n")
        code, _ = cli(["--history"], repo, denylist=None, SEATRIALS_DENYLIST_FILE=outside)
        check(code == OK, "denylist file outside a working tree is accepted")
        code, _ = cli(["--history", "--no-denylist"], repo, denylist=None)
        check(code == ERROR, "--no-denylist is refused outside GitHub Actions")
        code, output = cli(["--history", "--no-denylist"], repo, denylist=None, GITHUB_ACTIONS="true")
        check(code == OK and "NOT checked" in output, "--no-denylist in GitHub Actions says D1 was not checked")
        code, _ = cli([], repo)
        check(code == ERROR, "no mode: exit 2")

        # ---- P1 ------------------------------------------------------------
        allowed = ["src/A/B.cs", "LICENSE", "README.md", "CLAUDE.md", "THIRD-PARTY-NOTICES.txt",
                   ".gitignore", "scripts/hooks/pre-push", "samples/a.seatrials.json",
                   ".github/workflows/ci.yml", "global.json", "tests/x/Foo.cs", ".claude/settings.json",
                   "Directory.Build.props", "tools/stamp.py", "App.xaml"]
        refused_paths = ["docs/a.cs", "src/docs/a.cs", "Docs/a.cs", "notes/x.json", "runs/a.json",
                         "results/x.json", "artifacts/a.json", "sub/README.md", "NOTES.md", "x.txt",
                         "a.log", "a.zip", "a.exe", "a.msi", "a.dll", "run.json", "sub/run.json",
                         "summary.txt", "tests/a.seatrials.json", "samples/sub/a.seatrials.json",
                         "bin/x.json", "src/obj/project.assets.json", "foo.md", ".github/CODEOWNERS"]
        for path in allowed:
            check(path_is_allowed(path), "P1 allows " + path)
        for path in refused_paths:
            check(not path_is_allowed(path), "P1 refuses " + path)
        repo = new_repo()
        commit(repo, {"docs/plan.md": "text\n"})
        refused("P1 end to end", ["--history"], repo, "P1", "docs/plan.md")
        repo = new_repo()
        commit(repo, {"src/data.json": b"\x00\x01\x02binary"})
        refused("P1 binary content", ["--history"], repo, "P1", "src/data.json")
        repo = new_repo()
        commit(repo, {"src/a.cs": "// fine\n"})
        commit(repo, {"src/a.cs": None, "elsewhere/notes.md": "x\n"})
        refused("P1 on the tree", ["--tree"], repo, "P1", "elsewhere/notes.md")

        # ---- S1, M1, R1: positive and negative samples ----------------------
        def j(*pieces):
            return "".join(pieces)

        rules = Rules(Denylist([term]))
        positives = {
            "S1": [
                j("gh", "p_", "a" * 36), j("github_", "pat_", "A" * 24), j("AK", "IA", "B" * 16),
                j("-----BEGIN ", "RSA PRIVATE", " KEY-----"), j("Authorization: Bearer ", "q" * 24),
                j("pass", "word = ", '"', "hunter22", '"'), j("api_", "key: ", "'", "abc12345xyz", "'"),
                j("TOK", "EN=", "ab12cd34ef56"),
            ],
            "M1": [
                j("C", ":", "\\Users\\", "zed", "\\file.txt"), j("C", ":/", "Users/", "zed/x"),
                j("/", "Users/", "zed/x"), j("see /", "home/", "zed/x"),
                j("zed", "@", "corp-example.dev"), j("host ", ".".join(["10", "1", "2", "3"])),
                j("DESK", "TOP-", "ABC1234"),
            ],
            "R1": [
                j("https://github.com/", "someone/else"), j("git@github.com:", "someone/else.git"),
                j("see ", "a" * 40), j("uses: ", "someone/tool@v1"),
            ],
        }
        negatives = [
            "password: ${{ secrets.EXAMPLE }}", j("pass", "word = ", '"', '<your value>', '"'),
            "var token = cancellationToken;", "TokenType: \"Bearer\"", "\"secret\": true",
            "Authorization: Bearer ${{ env.VALUE }}",
            j("C", ":", "\\Users\\", "<user>", "\\x"), j("C", ":", "\\Users\\", "%USERNAME%", "\\x"),
            j("/", "home/", "runner/work"), j("/", "Users/", "{name}/x"), "see /home/(regex)",
            j("noreply", "@", "example.invalid"), j("a", "@", "example.com"), "uses: actions/checkout@v4.2.2",
            j("noreply", "@", "corp-example.dev"), j("12345+zed", "@", "users.noreply.github.com"),
            j("no-reply", "@", "corp-example.dev"),
            "package@1.2.3", j("addr ", ".".join(["192", "0", "2", "7"])), "version 1.2.3",
            "https://github.com/dev-blackbeard/BlackBeard.SeaTrials/actions",
            "https://example.com/acme/sample-repo.git", j("0" * 39, "1"), "a" * 64, "desktop-application",
        ]
        for rule, samples in positives.items():
            for sample in samples:
                path = "ci.yml" if sample.startswith("uses:") else None
                found = [r for r, _ in rules.check_line(sample, path)]
                check(rule in found, "%s catches %r" % (rule, sample[:12]))
        for sample in negatives:
            path = "ci.yml" if sample.startswith("uses:") else None
            found = rules.check_line(sample, path)
            check(not found, "no rule fires on %r (got %r)" % (sample[:20], found))
        sample = j("Authorization: Bearer ", "q" * 24)
        repo = new_repo()
        commit(repo, {"src/a.cs": "// " + sample + "\n"})
        refused("S1 end to end", ["--history"], repo, "S1", "src/a.cs:1", leak="q" * 24)
        repo = new_repo()
        commit(repo, {"src/a.cs": "// fine\n"}, GIT_AUTHOR_EMAIL=j("zed", "@", "corp-example.dev"))
        refused("M1 author address", ["--history"], repo, "M1", "author:1", leak="corp-example")

        # ---- the output never repeats a match, on any rule --------------------
        repo = new_repo()
        commit(repo, {"src/a.cs": "// " + j("https://github.com/", "someone/else") + "\n"})
        refused("R1 end to end", ["--history"], repo, "R1", "src/a.cs:1", leak="someone/else")
    finally:
        shutil.rmtree(base, ignore_errors=True)

    if failures:
        for label in failures:
            print("SELFTEST FAIL: " + label)
        print("selftest: %d passed, %d FAILED" % (passed[0], len(failures)))
        return FINDINGS
    print("selftest: %d checks passed" % passed[0])
    return OK


if __name__ == "__main__":
    sys.exit(main())
