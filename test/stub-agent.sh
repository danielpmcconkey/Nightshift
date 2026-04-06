#!/usr/bin/env bash
# Stub agent for smoke testing the Nightshift engine.
# Simulates Claude CLI by returning canned JSON responses based on the step name.
#
# Usage: Replace 'claude' in PATH with a symlink to this script, or set
#        NIGHTSHIFT_CLAUDE_PATH to point here.
#
# The script reads the prompt (last positional arg) and extracts the step name
# from the "Current Step:" line.

set -euo pipefail

# Find the prompt — it's the last argument
PROMPT="${@: -1}"

# Extract step name from prompt
STEP=$(echo "$PROMPT" | grep -oP '(?<=\*\*Current Step:\*\* )\S+' || echo "unknown")

# Extract artifact path from prompt
ARTIFACT_PATH=$(echo "$PROMPT" | grep -oP '(?<=`)[^`]+/process/[^`]+\.json(?=`)' || echo "")

# Build response based on step
case "$STEP" in
    rte_setup|po_kickoff|planner|gherkin_writer|builder|qe|builder_response)
        OUTCOME="SUCCESS"
        ;;
    reviewer|governor)
        OUTCOME="APPROVED"
        ;;
    po_signoff)
        OUTCOME="APPROVED"
        ;;
    rte_merge)
        OUTCOME="SUCCESS"
        ;;
    *)
        OUTCOME="SUCCESS"
        ;;
esac

RESPONSE="{\"outcome\": \"$OUTCOME\", \"reason\": \"stub agent auto-$OUTCOME\", \"notes\": \"stub\"}"

# Write artifact file if path was found
if [ -n "$ARTIFACT_PATH" ]; then
    mkdir -p "$(dirname "$ARTIFACT_PATH")"
    echo "$RESPONSE" > "$ARTIFACT_PATH"
fi

# Also output to stdout (Claude CLI --output-format json wraps this)
echo "{\"result\": $(echo "$RESPONSE" | python3 -c 'import sys,json; print(json.dumps(sys.stdin.read().strip()))')}"
