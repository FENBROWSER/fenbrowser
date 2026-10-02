#!/usr/bin/env bash
# Headless YouTube playback probe: loads a watch page through `FenBrowser.Tooling debug-site`,
# asks the player to play, waits, and prints the media element's state before and after.
#
#   scripts/yt_playback_probe.sh [repo-root] [video-id] [settle-ms]
#
# repo-root defaults to the current directory, so the same probe can run against another
# worktree's build (bisecting a regression). Output goes to stdout; the run's logs land in
# that repo's logs/ directory.
set -euo pipefail

ROOT="${1:-.}"
VIDEO="${2:-jNQXAC9IVRw}"
SETTLE="${3:-15000}"
TOOLING="$ROOT/FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe"

STATE='var p=document.querySelector("#movie_player");var v=document.querySelector("video");return "state="+(p&&p.getPlayerState?p.getPlayerState():"none")+" t="+(v&&v.currentTime)+" rs="+(v&&v.readyState)+" paused="+(v&&v.paused)+" err="+(v&&v.error&&v.error.code)+" buffered="+(v&&v.buffered.length?v.buffered.end(0):0);'

cd "$ROOT"
# A headless run has no user activation, so play() is refused unless autoplay is
# allowed; a caller can still pass FEN_MEDIA_AUTOPLAY=muted or blocked to test the policy.
FEN_MEDIA_AUTOPLAY="${FEN_MEDIA_AUTOPLAY:-allowed}" \
FEN_DEBUG_SITE_PRESCRIPT="(function(){var p=document.querySelector(\"#movie_player\");if(p&&p.playVideo)p.playVideo();$STATE})()" \
FEN_DEBUG_SITE_PRESCRIPT_SETTLE_MS="$SETTLE" \
FEN_DEBUG_SITE_POSTSCRIPT="(function(){$STATE})()" \
  timeout 600 "$TOOLING" debug-site "https://www.youtube.com/watch?v=$VIDEO" 25000 2>&1 \
  | grep -E "pre-screenshot|post-settle" | cut -c1-300
