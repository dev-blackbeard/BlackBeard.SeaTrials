#!/usr/bin/env python3
"""Tests of tools/stamp.py against the shared canonical-form vector.

The C# stamper reads the same vector file (tests/vectors/canonical-vectors.json), so the two
implementations are pinned to the same bytes. Python 3, standard library only:

    python3 tools/test_stamp.py
"""

import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import stamp  # noqa: E402

VECTORS = os.path.join(os.path.dirname(HERE), "tests", "vectors", "canonical-vectors.json")
STAMP = os.path.join(HERE, "stamp.py")


def load_vectors():
    with open(VECTORS, encoding="utf-8") as handle:
        return json.load(handle)


class SharedVectorTests(unittest.TestCase):
    def test_every_canonical_case(self):
        cases = load_vectors()["canonical"]
        self.assertGreater(len(cases), 5)
        for case in cases:
            with self.subTest(case["name"]):
                text = stamp.canonical(case["value"])
                self.assertEqual(text, case["expected"])
                self.assertEqual(stamp.sha256_hex(text), case["sha256"])

    def test_every_stamp_case(self):
        cases = load_vectors()["stamp"]
        self.assertGreater(len(cases), 3)
        for case in cases:
            with self.subTest(case["name"]):
                baseline = stamp.canonical(case["request"])
                self.assertEqual(baseline, case["baseline"])
                self.assertEqual(stamp.sha256_hex(baseline), case["baselineSha256"])
                stamped = stamp.stamp(case["request"], "a test", "2026-11-02T10:00:00Z")
                generated = stamped["generated"]
                self.assertEqual(generated["baseline"], case["baseline"])
                self.assertEqual(generated["baselineSha256"], case["baselineSha256"])

    def test_everything_the_vector_refuses_is_refused(self):
        cases = load_vectors()["refused"]
        self.assertGreater(len(cases), 4)
        for case in cases:
            with self.subTest(case["name"]):
                with self.assertRaises(stamp.StampError):
                    stamp.canonical(stamp.load_strict(case["json"]))

    def test_minus_zero_is_zero(self):
        self.assertEqual(stamp.canonical(stamp.load_strict("[-0, 0]")), "[0,0]")


class StampTests(unittest.TestCase):
    def request(self):
        return json.loads(json.dumps(load_vectors()["stamp"][0]["request"]))

    def test_the_stamp_has_the_five_keys_and_comes_last(self):
        stamped = stamp.stamp(self.request(), "a session", "2026-11-02T10:00:00Z")
        self.assertEqual(list(stamped)[-1], "generated")
        generated = stamped["generated"]
        self.assertEqual(sorted(generated), ["at", "baseline", "baselineSha256", "by", "tool"])
        self.assertEqual(generated["by"], "a session")
        self.assertEqual(generated["at"], "2026-11-02T10:00:00Z")
        self.assertEqual(generated["tool"], "stamp.py 1")
        self.assertEqual(len(generated["baselineSha256"]), 64)

    def test_the_input_is_not_changed_and_an_old_stamp_is_replaced(self):
        original = self.request()
        original["generated"] = {"by": "old", "at": "x", "tool": "x", "baselineSha256": "x", "baseline": "x"}
        before = json.dumps(original, sort_keys=True)
        stamped = stamp.stamp(original, "new", "2026-11-02T10:00:00Z")
        self.assertEqual(json.dumps(original, sort_keys=True), before)
        self.assertEqual(stamped["generated"]["by"], "new")
        self.assertNotIn("generated", stamped["generated"]["baseline"])

    def test_the_stamp_is_the_same_whatever_the_key_order(self):
        request = self.request()
        reversed_request = dict(reversed(list(request.items())))
        first = stamp.stamp(request, "a", "2026-11-02T10:00:00Z")["generated"]
        second = stamp.stamp(reversed_request, "a", "2026-11-02T10:00:00Z")["generated"]
        self.assertEqual(first, second)

    def test_a_request_must_be_an_object_in_the_right_format_with_an_author(self):
        with self.assertRaises(stamp.StampError):
            stamp.stamp([], "a")
        with self.assertRaises(stamp.StampError):
            stamp.stamp({"format": "seatrials/2"}, "a")
        with self.assertRaises(stamp.StampError):
            stamp.stamp(self.request(), "   ")

    def test_a_float_a_duplicate_key_and_a_huge_integer_are_refused(self):
        for text in ('{"a": 1.5}', '{"a": 1, "a": 2}', "[99999999999999999999]", "NaN", "{ not json"):
            with self.subTest(text):
                with self.assertRaises(stamp.StampError):
                    stamp.canonical(stamp.load_strict(text))

    def test_an_unpaired_surrogate_is_refused(self):
        with self.assertRaises(stamp.StampError):
            stamp.canonical({"a": "\ud800"})

    def test_a_byte_order_mark_is_tolerated(self):
        self.assertEqual(stamp.load_strict("\ufeff{}"), {})


class CommandLineTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = directory.name
        self.request = load_vectors()["stamp"][-1]["request"]
        self.path = os.path.join(self.directory, "sample.json")
        self.write(self.path, json.dumps(self.request, ensure_ascii=False, indent=2))

    @staticmethod
    def write(path, text):
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)

    def run_stamp(self, *args):
        return subprocess.run([sys.executable, STAMP] + list(args), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def read(self, path):
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)

    def test_it_rewrites_the_file_with_the_stamp_last(self):
        result = self.run_stamp(self.path, "--by", "a session", "--at", "2026-11-02T10:00:00Z")
        self.assertEqual(result.returncode, 0, result.stderr)
        stamped = self.read(self.path)
        self.assertEqual(list(stamped)[-1], "generated")
        expected = stamp.canonical(self.request)
        self.assertEqual(stamped["generated"]["baseline"], expected)
        self.assertEqual(stamped["generated"]["baselineSha256"], stamp.sha256_hex(expected))
        text = open(self.path, encoding="utf-8").read()
        self.assertTrue(text.endswith("}\n"))
        self.assertNotIn("\r", text)

    def test_stamping_again_replaces_the_stamp_and_keeps_the_baseline(self):
        self.run_stamp(self.path, "--by", "first", "--at", "2026-11-02T10:00:00Z")
        first = self.read(self.path)["generated"]
        result = self.run_stamp(self.path, "--by", "second", "--at", "2026-11-02T11:00:00Z")
        self.assertEqual(result.returncode, 0, result.stderr)
        second = self.read(self.path)["generated"]
        self.assertEqual(second["by"], "second")
        self.assertEqual(second["baseline"], first["baseline"])
        self.assertEqual(second["baselineSha256"], first["baselineSha256"])

    def test_output_leaves_the_input_alone(self):
        before = open(self.path, encoding="utf-8").read()
        target = os.path.join(self.directory, "out.json")
        result = self.run_stamp(self.path, "--by", "a", "--output", target)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(open(self.path, encoding="utf-8").read(), before)
        self.assertIn("generated", self.read(target))

    def test_a_bad_file_gives_exit_code_3_and_a_message(self):
        self.write(self.path, "{ not json")
        result = self.run_stamp(self.path, "--by", "a")
        self.assertEqual(result.returncode, 3)
        self.assertIn("stamp.py:", result.stderr)
        missing = self.run_stamp(os.path.join(self.directory, "missing.json"), "--by", "a")
        self.assertEqual(missing.returncode, 3)

    def test_the_author_is_required(self):
        self.assertEqual(self.run_stamp(self.path).returncode, 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
