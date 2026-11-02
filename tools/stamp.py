#!/usr/bin/env python3
"""Adds the stamp (the "generated" block) to a SeaTrials request file.

Python 3, standard library only. A requesting session vendors this one file.

    python3 stamp.py request.seatrials.json --by "a session"

The stamp records who wrote the request and its baseline: the plan (the whole
file except the "generated" key) in canonical form, kept as a string, with the
SHA-256 of that string. The program that runs the request compares the plan as
it then stands with the baseline, and reports every difference, so that a
request that was changed on the way is never taken for the one that was written.

Canonical form (the same bytes in every implementation):
  * UTF-8; object keys sorted by Unicode code point; no whitespace;
  * strings escaped as little as JSON allows: only the quote, the backslash and
    the control characters below U+0020 (as \\b \\f \\n \\r \\t or \\u00xx in
    lower case); every other character, non-ASCII included, is left as it is;
  * integers only. A number with a fraction or an exponent, NaN, a duplicate
    key, or an integer that does not fit in 64 bits is refused.

In Python this is json.dumps(plan, sort_keys=True, separators=(',', ':'),
ensure_ascii=False) once the plan has passed the checks above.

The file must be strict JSON (no comments, no trailing commas). It is rewritten
with two-space indentation and the "generated" key last; nothing else changes.
"""

import argparse
import datetime
import hashlib
import json
import sys

TOOL = "stamp.py 1"
FORMAT = "seatrials/1"
INT64_MIN, INT64_MAX = -(2 ** 63), 2 ** 63 - 1


class StampError(Exception):
    """The request cannot be stamped. The message says why."""


def _refuse_constant(name):
    raise StampError("%s is not allowed: the plan holds integers only" % name)


def _refuse_float(text):
    raise StampError("the number %s is not an integer: the plan holds integers only" % text)


def _object_without_duplicates(pairs):
    seen = {}
    for key, value in pairs:
        if key in seen:
            raise StampError("duplicate key %r" % key)
        seen[key] = value
    return seen


def load_strict(text):
    """Parse JSON, refusing duplicate keys, floats, NaN and Infinity."""
    if text.startswith("\ufeff"):
        text = text[1:]
    try:
        return json.loads(
            text,
            object_pairs_hook=_object_without_duplicates,
            parse_float=_refuse_float,
            parse_constant=_refuse_constant,
        )
    except json.JSONDecodeError as error:
        raise StampError("not valid JSON (line %d): %s" % (error.lineno, error.msg)) from None


def _check(value, path):
    if isinstance(value, bool) or value is None or isinstance(value, str):
        return
    if isinstance(value, int):
        if not INT64_MIN <= value <= INT64_MAX:
            raise StampError("%s: the integer does not fit in 64 bits" % path)
        return
    if isinstance(value, list):
        for index, item in enumerate(value):
            _check(item, "%s[%d]" % (path, index))
        return
    if isinstance(value, dict):
        for key, item in value.items():
            _check(item, "%s.%s" % (path, key) if path else key)
        return
    raise StampError("%s: a value that JSON cannot hold" % path)


def canonical(value):
    """The canonical form of a JSON value, as a string."""
    _check(value, "")
    text = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    try:
        text.encode("utf-8")
    except UnicodeEncodeError:
        raise StampError("a string holds an unpaired surrogate and cannot be written as UTF-8") from None
    return text


def sha256_hex(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def now_utc():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def stamp(request, by, at=None, tool=TOOL):
    """Return the request with a fresh "generated" block (last). The input is not changed."""
    if not isinstance(request, dict):
        raise StampError("a request is a JSON object")
    if request.get("format") != FORMAT:
        raise StampError('the "format" must be "%s"' % FORMAT)
    if not isinstance(by, str) or not by.strip():
        raise StampError('"--by" must say who wrote the request')
    plan = {key: value for key, value in request.items() if key != "generated"}
    baseline = canonical(plan)
    stamped = dict(plan)
    stamped["generated"] = {
        "by": by,
        "at": at or now_utc(),
        "tool": tool,
        "baselineSha256": sha256_hex(baseline),
        "baseline": baseline,
    }
    return stamped


def main(argv=None):
    parser = argparse.ArgumentParser(description="Add the stamp to a SeaTrials request file.")
    parser.add_argument("file", help="the request file (*.seatrials.json)")
    parser.add_argument("--by", required=True, help="who wrote the request: a session or a person")
    parser.add_argument("--at", help="the time, as 2026-11-02T10:00:00Z (default: now, UTC)")
    parser.add_argument("--output", help="write here instead of rewriting the file")
    args = parser.parse_args(argv)
    try:
        with open(args.file, encoding="utf-8") as handle:
            request = load_strict(handle.read())
        stamped = stamp(request, args.by, args.at)
    except OSError as error:
        print("stamp.py: cannot read %s: %s" % (args.file, error.strerror), file=sys.stderr)
        return 3
    except StampError as error:
        print("stamp.py: %s" % error, file=sys.stderr)
        return 3
    text = json.dumps(stamped, indent=2, ensure_ascii=False) + "\n"
    with open(args.output or args.file, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print("stamped %s (baseline %s)" % (args.output or args.file, stamped["generated"]["baselineSha256"][:12]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
