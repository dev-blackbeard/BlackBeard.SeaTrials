#!/bin/sh
# Enable this repository's git hooks (the pre-push sanity check) for this clone.
set -e
cd "$(dirname "$0")/.."
git config core.hooksPath scripts/hooks
echo "git hooks enabled: core.hooksPath = scripts/hooks"
