#!/usr/bin/env python3
"""Tests that the pre-push hook and the Claude Code guard refuse what they must.

Real git is used: a temporary repository with a bare remote, the real hook and
the real check, and a synthetic denylist. Nothing is pushed anywhere else.
Python 3, standard library only:   python3 scripts/test-hooks.py
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SCRIPTS)
TERM = "zorblax-sample"


class SandboxCase(unittest.TestCase):
    """A working repository whose hooks path points at the real hook."""

    def setUp(self):
        self.base = tempfile.mkdtemp(prefix="hook-test-")
        self.addCleanup(shutil.rmtree, self.base, ignore_errors=True)
        self.remote = os.path.join(self.base, "remote.git")
        self.work = os.path.join(self.base, "work")
        os.makedirs(self.remote)
        os.makedirs(self.work)
        self.git_in(self.remote, "init", "-q", "--bare", "-b", "main")
        self.git("init", "-q", "-b", "main")
        self.git("remote", "add", "origin", self.remote)
        # The real scripts are copied in but never committed.
        with open(os.path.join(self.work, ".git", "info", "exclude"), "a", encoding="utf-8") as handle:
            handle.write("scripts/\n")
        shutil.copytree(os.path.join(SCRIPTS, "hooks"), os.path.join(self.work, "scripts", "hooks"))
        shutil.copy2(os.path.join(SCRIPTS, "sanity-check.py"), os.path.join(self.work, "scripts"))
        os.chmod(os.path.join(self.work, "scripts", "hooks", "pre-push"), 0o755)
        self.git("config", "core.hooksPath", "scripts/hooks")
        self.commit({"src/first.cs": "// a clean file\n"})
        result = self.push("main")
        self.assertEqual(result.returncode, 0, "a clean push must be accepted:\n" + result.stdout)

    def env(self, denylist=TERM + "\n", **extra):
        env = {k: v for k, v in os.environ.items()
               if not k.startswith("SEATRIALS_") and k != "GITHUB_ACTIONS"}
        env.update({
            "GIT_AUTHOR_NAME": "Sample Author", "GIT_AUTHOR_EMAIL": "noreply@example.invalid",
            "GIT_COMMITTER_NAME": "Sample Author", "GIT_COMMITTER_EMAIL": "noreply@example.invalid",
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull, "GIT_TERMINAL_PROMPT": "0",
            "HOME": self.base, "CLAUDE_PROJECT_DIR": self.work,
        })
        if denylist is not None:
            env["SEATRIALS_DENYLIST"] = denylist
        env.update(extra)
        return env

    def git_in(self, directory, *args, **env_args):
        return subprocess.run(
            ["git", "-C", directory] + list(args), env=self.env(**env_args),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False,
        )

    def git(self, *args, **env_args):
        result = self.git_in(self.work, *args, **env_args)
        self.assertEqual(result.returncode, 0, "git %s failed:\n%s" % (args[0], result.stdout))
        return result

    def commit(self, files, message="sample change"):
        for name, content in files.items():
            target = os.path.join(self.work, *name.split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            if content is None:
                os.remove(target)
            else:
                with open(target, "w", encoding="utf-8") as handle:
                    handle.write(content)
        self.git("add", "-A")
        self.git("commit", "-q", "-m", message)

    def push(self, refspec, **env_args):
        return self.git_in(self.work, "push", "origin", refspec, **env_args)

    def remote_has(self, ref):
        return self.git_in(self.remote, "rev-parse", "--verify", "-q", ref).returncode == 0


class PrePushHookTests(SandboxCase):
    def test_a_failing_commit_is_refused_and_nothing_reaches_the_remote(self):
        self.commit({"src/second.cs": "// mentions " + TERM + "\n"})
        before = self.git_in(self.remote, "rev-parse", "main").stdout
        result = self.push("main")
        self.assertNotEqual(result.returncode, 0, "the push must be refused")
        self.assertIn("FAIL D1", result.stdout)
        self.assertNotIn(TERM, result.stdout, "the output must not repeat the match")
        self.assertEqual(self.git_in(self.remote, "rev-parse", "main").stdout, before)

    def test_a_new_branch_with_a_failing_commit_is_refused(self):
        self.commit({"src/second.cs": "// mentions " + TERM + "\n"})
        result = self.push("HEAD:refs/heads/topic")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.remote_has("refs/heads/topic"))

    def test_removing_the_text_in_a_later_commit_is_not_enough(self):
        self.commit({"src/second.cs": "// mentions " + TERM + "\n"})
        self.commit({"src/second.cs": "// clean again\n"})
        result = self.push("main")
        self.assertNotEqual(result.returncode, 0, "the earlier commit is part of what would be pushed")
        self.assertIn("FAIL D1 commit", result.stdout)

    def test_a_failing_commit_message_is_refused(self):
        self.commit({"src/second.cs": "// clean\n"}, message="mentions " + TERM)
        self.assertNotEqual(self.push("main").returncode, 0)

    def test_a_failing_branch_name_is_refused(self):
        self.commit({"src/second.cs": "// clean\n"})
        result = self.push("HEAD:refs/heads/feature/" + TERM)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn(TERM, result.stdout)

    def test_a_path_outside_the_allow_list_is_refused(self):
        self.commit({"docs/plan.md": "text\n"})
        result = self.push("main")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("FAIL P1", result.stdout)

    def test_a_missing_denylist_blocks_the_push(self):
        self.commit({"src/second.cs": "// clean\n"})
        result = self.push("main", denylist=None)
        self.assertNotEqual(result.returncode, 0, "the check fails closed")
        self.assertIn("no denylist", result.stdout)

    def test_a_clean_commit_is_accepted(self):
        self.commit({"src/second.cs": "// clean\n"})
        self.assertEqual(self.push("main").returncode, 0)

    def test_the_hook_is_executable_in_git(self):
        listed = subprocess.run(
            ["git", "-C", ROOT, "ls-files", "-s", "scripts/hooks/pre-push"],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, check=False,
        )
        if not listed.stdout.strip():
            self.skipTest("not a git checkout that tracks the hook")
        self.assertTrue(listed.stdout.startswith("100755"), "git would ignore a hook without the executable bit")

    def test_the_session_start_hook_enables_the_hooks(self):
        with open(os.path.join(ROOT, ".claude", "settings.json"), encoding="utf-8") as handle:
            settings = json.load(handle)
        command = settings["hooks"]["SessionStart"][0]["hooks"][0]["command"]
        self.git("config", "--unset", "core.hooksPath")
        subprocess.run(["sh", "-c", command], env=self.env(), check=True)
        self.assertEqual(self.git("config", "core.hooksPath").stdout.strip(), "scripts/hooks")


class ClaudeGuardTests(SandboxCase):
    def guard(self, tool, arguments, **env_args):
        call = json.dumps({"hook_event_name": "PreToolUse", "tool_name": tool, "tool_input": arguments})
        return subprocess.run(
            [sys.executable, os.path.join(SCRIPTS, "claude-guard.py")], input=call,
            env=self.env(**env_args), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False,
        )

    def test_a_push_is_blocked_while_a_commit_fails(self):
        self.commit({"src/second.cs": "// mentions " + TERM + "\n"})
        result = self.guard("Bash", {"command": "git push -u origin main"})
        self.assertEqual(result.returncode, 2)
        self.assertNotIn(TERM, result.stdout)

    def test_a_push_is_allowed_when_everything_is_clean(self):
        self.commit({"src/second.cs": "// clean\n"})
        self.assertEqual(self.guard("Bash", {"command": "git push -u origin main"}).returncode, 0)

    def test_skipping_the_hook_is_blocked(self):
        for command in ("git push --no-verify origin main", "git -c core.hooksPath=/dev/null push"):
            self.assertEqual(self.guard("Bash", {"command": command}).returncode, 2, command)

    def test_other_commands_are_not_touched(self):
        self.commit({"src/second.cs": "// mentions " + TERM + "\n"})
        self.assertEqual(self.guard("Bash", {"command": "ls -la"}).returncode, 0)

    def test_the_github_file_tools_are_blocked_for_this_repository_only(self):
        for tool in ("mcp__github__push_files", "mcp__github__create_or_update_file"):
            self.assertEqual(self.guard(tool, {"owner": "o", "repo": "BlackBeard.SeaTrials"}).returncode, 2, tool)
            self.assertEqual(self.guard(tool, {"owner": "o", "repo": "Some.Other.Repo"}).returncode, 0, tool)

    def test_text_sent_to_github_is_checked(self):
        this_repo = {"owner": "o", "repo": "BlackBeard.SeaTrials", "head": "topic", "base": "main"}
        clean = dict(this_repo, title="A title", body="A body")
        self.assertEqual(self.guard("mcp__github__create_pull_request", clean).returncode, 0)
        dirty = dict(this_repo, title="A title", body="the body mentions " + TERM)
        result = self.guard("mcp__github__create_pull_request", dirty)
        self.assertEqual(result.returncode, 2)
        self.assertNotIn(TERM, result.stdout)
        bad_branch = dict(clean, head="feature/" + TERM)
        self.assertEqual(self.guard("mcp__github__create_pull_request", bad_branch).returncode, 2)
        other = dict(dirty, repo="Some.Other.Repo")
        self.assertEqual(self.guard("mcp__github__create_pull_request", other).returncode, 0)

    def test_text_is_refused_when_the_denylist_is_missing(self):
        arguments = {"owner": "o", "repo": "BlackBeard.SeaTrials", "body": "clean"}
        self.assertEqual(self.guard("mcp__github__add_issue_comment", arguments, denylist=None).returncode, 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
