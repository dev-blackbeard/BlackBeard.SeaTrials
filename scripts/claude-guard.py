#!/usr/bin/env python3
"""PreToolUse guard for Claude Code sessions: a second line behind the pre-push hook.

Claude Code runs it before a Bash command or a GitHub tool call and passes the
call as JSON on standard input. Exit code 2 blocks the call and shows standard
error to the session; exit code 0 lets it through. Python 3, standard library only.

It blocks:
  * `git push` while the sanity check fails or cannot run, and any push that
    tries to skip the hook (--no-verify, core.hooksPath);
  * the GitHub tools that write files into this repository, because they would
    publish without any git hook running (use `git push`);
  * a GitHub call that sends text to this repository (a pull request, a comment,
    an issue) while that text fails the sanity check.
"""

import json
import os
import re
import subprocess
import sys
import tempfile

BLOCK = 2
HERE = os.path.dirname(os.path.abspath(__file__))
CHECK = os.path.join(HERE, "sanity-check.py")
THIS_REPOSITORY = "blackbeard.seatrials"

FILE_WRITING_TOOLS = {
    "mcp__github__push_files",
    "mcp__github__create_or_update_file",
    "mcp__github__delete_file",
}
TEXT_SENDING_TOOLS = {
    "mcp__github__create_pull_request",
    "mcp__github__update_pull_request",
    "mcp__github__add_issue_comment",
    "mcp__github__update_issue_comment",
    "mcp__github__issue_write",
    "mcp__github__sub_issue_write",
    "mcp__github__add_reply_to_pull_request_comment",
    "mcp__github__add_comment_to_pending_review",
    "mcp__github__pull_request_review_write",
    "mcp__github__create_branch",
}
BRANCH_FIELDS = ("head", "branch", "from_branch", "base")


def refuse(message):
    print("BLOCKED by scripts/claude-guard.py: " + message, file=sys.stderr)
    return BLOCK


def run_check(args, repo):
    proc = subprocess.run(
        [sys.executable, CHECK] + args + ["--repo", repo],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False,
    )
    return proc.returncode, proc.stdout.decode("utf-8", "replace")


def guard_bash(command, repo):
    if not re.search(r"\bgit\b[\s\S]*\bpush\b", command):
        return 0
    if re.search(r"--no-verify|core\.hooksPath", command):
        return refuse("a push may not skip the hook (--no-verify, core.hooksPath).")
    code, output = run_check(["--unpushed"], repo)
    if code != 0:
        return refuse("the sanity check failed or could not run; nothing may be pushed.\n" + output)
    return 0


def strings_in(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings_in(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings_in(item)


def guard_github(tool, arguments, repo):
    if str(arguments.get("repo", "")).lower() != THIS_REPOSITORY:
        return 0  # another repository: not this guard's business
    if tool in FILE_WRITING_TOOLS:
        return refuse("this tool would publish without the pre-push hook; commit and use `git push`.")
    if tool not in TEXT_SENDING_TOOLS:
        return 0
    text = "\n".join(s for key, v in arguments.items() if key not in ("owner", "repo")
                     for s in strings_in(v))
    branches = [str(arguments[key]) for key in BRANCH_FIELDS if arguments.get(key)]
    handle, path = tempfile.mkstemp(prefix="guard-text-", suffix=".txt")
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            stream.write(text)
        args = ["--text", path]
        for name in branches:
            args += ["--branch", name]
        code, output = run_check(args, repo)
    finally:
        os.remove(path)
    if code != 0:
        return refuse("the text for GitHub failed the sanity check or it could not run.\n" + output)
    return 0


def main():
    try:
        call = json.load(sys.stdin)
        tool = call.get("tool_name", "")
        arguments = call.get("tool_input") or {}
    except (ValueError, AttributeError):
        return 0  # not a call this guard understands
    repo = os.environ.get("CLAUDE_PROJECT_DIR") or call.get("cwd") or os.getcwd()
    if tool == "Bash":
        return guard_bash(str(arguments.get("command", "")), repo)
    if tool.startswith("mcp__github__"):
        return guard_github(tool, arguments, repo)
    return 0


if __name__ == "__main__":
    sys.exit(main())
