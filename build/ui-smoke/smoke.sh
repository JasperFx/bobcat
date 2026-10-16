#!/usr/bin/env bash
# The bobcat ui smoke (bobcat#524): drive the PACKED tool the way a person does.
#
#   build/ui-smoke/smoke.sh <path to the installed bobcat executable>
#
# `./build.sh UiSmoke` packs the tool exactly as publish.yml does (EmbedFrontend=true), installs it
# into a tool path and calls this; ui-smoke.yml runs that target. The unit and spec suites test the
# host in-process and the SPA under Vitest, so neither notices a tool that ships without its page,
# a hashed asset the server will not serve, a deep link that 404s, or a renderer that throws in a
# real browser. This does — and the same for the HTML results page the tool writes (bobcat#541).
#
# Nothing here touches ~/.bobcat: the UI's data directory is a temp directory (BOBCAT_MONITOR_DATA)
# and the port is a free one, so it runs beside a developer's own `bobcat ui` or Stoat.
#
# BOBCAT_UI_SMOKE_SKIP_BROWSER=1 skips the Playwright leg.
set -euo pipefail

bobcat="${1:?usage: smoke.sh <path to the installed bobcat executable>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
run_fixture="$repo/src/Bobcat.Ui.Tests/Fixtures/crittercrush-supervised-bobcat-0.60.1.ndjson"
model_fixture="$here/event-model.json"
results_fixture="$repo/src/Bobcat.Ui.FrontEnd/src/results/__tests__/results.fixture.json"

work="$(mktemp -d)"
export BOBCAT_MONITOR_DATA="$work/data"
mkdir -p "$BOBCAT_MONITOR_DATA"

port="$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')"
url="http://localhost:$port"

fail() {
  echo "::error::ui smoke: $*" >&2
  echo "--- bobcat ui log ---" >&2
  cat "$work/ui.log" >&2 || true
  exit 1
}

ui_pid=""
cleanup() {
  if [[ -n "$ui_pid" ]] && kill -0 "$ui_pid" 2>/dev/null; then
    kill "$ui_pid" 2>/dev/null || true
    wait "$ui_pid" 2>/dev/null || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

echo "== bobcat ui on $url (data: $BOBCAT_MONITOR_DATA)"
"$bobcat" ui --no-browser --port "$port" > "$work/ui.log" 2>&1 &
ui_pid=$!

for _ in $(seq 1 120); do
  if curl -sf "$url/api/runs" > /dev/null 2>&1; then break; fi
  kill -0 "$ui_pid" 2>/dev/null || fail "bobcat ui exited before it answered"
  sleep 0.5
done
curl -sf "$url/api/runs" > /dev/null || fail "bobcat ui never answered on $url"
echo "ok   bobcat ui answers"

# --- The page: the SPA is embedded, every asset it references is served, and deep links work.
curl -sf -D "$work/home.headers" "$url/" -o "$work/home.html" || fail "GET / failed"
grep -qi '^content-type: text/html' "$work/home.headers" || fail "GET / is not text/html"
grep -q '<div id="app"' "$work/home.html" || fail "GET / is not the SPA shell (no #app) — was the frontend embedded?"
grep -q '/src/main.ts' "$work/home.html" && fail "GET / serves the dev index.html, not the Vite build"
echo "ok   / serves the built SPA"

assets="$(grep -oE '(src|href)="/[^"/][^"]*"' "$work/home.html" | sed -E 's/^(src|href)="//; s/"$//' | sort -u)"
[[ -n "$assets" ]] || fail "the SPA shell references no local assets"
while IFS= read -r asset; do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$url$asset")"
  [[ "$code" == "200" ]] || fail "asset $asset answered $code"
  echo "ok   $asset"
done <<< "$assets"

for route in /runs /event-model "/runs/00000000-0000-0000-0000-000000000000"; do
  curl -sf "$url$route" | grep -q '<div id="app"' || fail "deep link $route does not serve the SPA"
done
echo "ok   deep links serve the SPA"

# --- The wire: an empty console answers JSON, then a recorded run and a model go in the way
#     publishers send them.
[[ "$(curl -sf "$url/api/runs" | jq -r 'type')" == "array" ]] || fail "/api/runs is not a JSON array"
echo "ok   /api/runs is JSON"

run_id="$(head -n 1 "$run_fixture" | jq -r '.runId')"
code="$(jq -c -s '{events: .}' "$run_fixture" \
  | curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data-binary @- "$url/api/ingest")"
[[ "$code" == "202" ]] || fail "POST /api/ingest answered $code"
curl -sf "$url/api/runs" | jq -e --arg id "$run_id" 'any(.[]; .runId == $id)' > /dev/null \
  || fail "the ingested run $run_id is not in /api/runs"
echo "ok   a recorded run ingests"

code="$(curl -s -o /dev/null -w '%{http_code}' -X PUT -H 'Content-Type: application/json' --data-binary @"$model_fixture" "$url/api/event-model")"
[[ "$code" == "204" ]] || fail "PUT /api/event-model answered $code"
[[ "$(curl -sf "$url/api/event-model" | jq -r '.name')" == "Wallets" ]] || fail "GET /api/event-model does not serve the pushed model"
echo "ok   an Event Model publishes"

# --- The CLI queries, against the running UI.
"$bobcat" list-runs --url "$url" > "$work/list-runs.json" || fail "bobcat list-runs exited non-zero"
grep -q "$run_id" "$work/list-runs.json" || fail "bobcat list-runs does not list $run_id"
"$bobcat" event-model --url "$url" > "$work/event-model.json" || fail "bobcat event-model exited non-zero"
grep -q 'CreditWallet' "$work/event-model.json" || fail "bobcat event-model does not show CreditWallet"
echo "ok   CLI queries answer"

# --- The HTML results page (bobcat#541): the packed tool writes one self-contained file from a results
#     file, with a step's screenshot kept beside the results the way a run writes attachments.
results_dir="$work/results"
mkdir -p "$results_dir/attachments/run/spec"
cp "$results_fixture" "$results_dir/run.json"
cp "$repo/src/Bobcat.Ui.FrontEnd/public/favicon.png" "$results_dir/attachments/run/spec/01-screen.png"
results_page="$work/page/results.html"
"$bobcat" results-html "$results_dir" --out "$results_page" > "$work/results-html.log" 2>&1 \
  || { cat "$work/results-html.log" >&2; fail "bobcat results-html exited non-zero"; }
[[ -f "$results_page" ]] || fail "bobcat results-html wrote no page"
grep -q '/src/results/main.ts' "$results_page" && fail "the results page is the dev template, not the Vite build"
grep -qE '<(script|link)[^>]*(src|href)=' "$results_page" && fail "the results page references a file beside it"
grep -q 'Bobcat.Xunit.Samples' "$results_page" || fail "the results page does not carry the run's results"
echo "ok   bobcat results-html writes a self-contained page"

# --- A second UI on the same port refuses, clearly, rather than hanging or dying with a stack trace.
"$bobcat" ui --no-browser --port "$port" > "$work/second.log" 2>&1 &
second_pid=$!
for _ in $(seq 1 60); do
  kill -0 "$second_pid" 2>/dev/null || break
  sleep 0.5
done
if kill -0 "$second_pid" 2>/dev/null; then
  kill "$second_pid" 2>/dev/null || true
  fail "a second bobcat ui on port $port did not exit"
fi
set +e; wait "$second_pid"; second_exit=$?; set -e
[[ "$second_exit" != "0" ]] || fail "a second bobcat ui on port $port exited 0"
grep -q "Could not listen on $url" "$work/second.log" || { cat "$work/second.log" >&2; fail "a second bobcat ui did not say why it stopped"; }
echo "ok   a second bobcat ui on the same port refuses (exit $second_exit)"

# --- A real browser: the run board and the canvas render without console errors.
if [[ "${BOBCAT_UI_SMOKE_SKIP_BROWSER:-0}" == "1" ]]; then
  echo "skip browser leg (BOBCAT_UI_SMOKE_SKIP_BROWSER=1)"
else
  (
    cd "$here/e2e"
    npm ci --no-audit --no-fund --loglevel=error
    if [[ "${CI:-}" == "true" ]]; then
      npx playwright install --with-deps chromium
    else
      npx playwright install chromium
    fi
    BOBCAT_UI_URL="$url" BOBCAT_UI_RUN_ID="$run_id" BOBCAT_RESULTS_PAGE="$results_page" npx playwright test
  ) || fail "the browser leg failed"
fi

echo "== bobcat ui smoke passed"
