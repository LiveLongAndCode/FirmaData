#!/usr/bin/env python3
"""Manual/live test for FirmaData's per-dependency Polly circuit breaker.

Config under test (docs/reference/adr/0003-polly-in-memory-cache-resilience.md,
FirmaData.Cvr/FirmaData.Statbank ServiceCollectionExtensions.cs):
FailureRatio 0.5, MinimumThroughput 10, SamplingDuration 30s, BreakDuration 30s.

Neither apicvr.dk nor api.statbank.dk is mocked in this project, so there is no
way to make the real upstream fail on demand. This script starts one local stub
HTTP server that serves *both* dependencies (a CVR lookup response and a
Statbank CSV response), and drives the *running* FirmaData API (started
separately, with BOTH Cvr__BaseUrl and Statbank__BaseUrl pointed at the stub)
until the target dependency's circuit trips -- confirmed by scraping /metrics
for firmadata_circuit_state, not by guessing from response latency. Pointing
both at the stub matters even when testing only one breaker: a company lookup
resolves CVR before it ever calls Statbank, so testing "statbank" without also
stubbing CVR would still send every request to the real apicvr.dk.

Only the dependency under test starts out failing; the other starts healthy
from the first request, so it never talks to its real upstream either. Once
the breaker is confirmed open, the script heals the failing side too and waits
out BreakDuration to confirm recovery (Open -> Half-Open -> Closed) -- by then
nothing in the run has touched apicvr.dk or api.statbank.dk at all.

Not hitting the real APIs is the default, and it's enforced, not just
documented: before firing anything at the enrichment endpoint, the script
calls /health/ready (a cheap, Polly-free reachability probe per dependency --
see CvrApiHealthCheck/StatbankApiHealthCheck) and checks the stub's own hit
counter to confirm both BaseUrls actually route there. If they don't -- e.g.
the API process was already running before the env vars were set -- it
refuses to continue rather than silently sending a burst of real requests to
apicvr.dk / api.statbank.dk. Pass --no-stub to explicitly opt out of the stub
(and this check) and test against whatever the API is already configured with.

Usage:
    python tools/test-circuit-breaker.py --dependency cvr
    python tools/test-circuit-breaker.py --dependency statbank

The script prints both env vars to set and the stub URL to point them at
before you start the API. For a local `dotnet run` (from
solution/src/Backend/FirmaData.Api):

    Cvr__BaseUrl=http://localhost:8199/ Statbank__BaseUrl=http://localhost:8199/ dotnet run   # bash
    $env:Cvr__BaseUrl="http://localhost:8199/"; $env:Statbank__BaseUrl="http://localhost:8199/"; dotnet run   # PowerShell

For docker compose, add both overrides to firmadata-api's `environment:` in
docker-compose.yml (host.docker.internal instead of localhost) and
`docker compose up -d --build firmadata-api`.
"""

import argparse
import http.server
import re
import sys
import threading
import time
import urllib.error
import urllib.request

STATE_NAMES = {0: "closed", 1: "open", 2: "half-open"}

CVR_BODY_TEMPLATE = (
    '{{"vat":{cvr},"name":"LB FORSIKRING A/S","address":"Amerika Plads 15",'
    '"zipcode":2100,"city":"København Ø","employees":1010,'
    '"industrycode":"651200","industrydesc":"Anden forsikring","bankrupt":false,'
    '"status":"NORMAL"}}'
)
STATBANK_CSV_BODY = (
    "BRANCHE07;TAL;TID;INDHOLD\n"
    "651200;ARBSTED;2022;166\n"
    "651200;ANSATTE;2022;15206\n"
    "651200;FULDBESK;2022;13458\n"
    "651200;LØNSUM;2022;10380\n"
)


class StubHits:
    """Thread-safe counter of every request the stub has received, regardless of
    path -- used to confirm the API is actually routing to the stub at all,
    including the bare-root GET CvrApiHealthCheck/StatbankApiHealthCheck make."""

    def __init__(self):
        self._count = 0
        self._lock = threading.Lock()

    def record(self):
        with self._lock:
            self._count += 1

    def snapshot(self):
        with self._lock:
            return self._count


class StubHandler(http.server.BaseHTTPRequestHandler):
    # Set by start_stub(): one threading.Event per dependency, independently
    # clear (failing) or set (succeeding) -- so the dependency under test can
    # fail while the other one stays healthy and never needs the real API.
    cvr_healthy = None
    statbank_healthy = None
    cvr = "16500836"
    hits = None

    def do_GET(self):
        self._respond()

    def do_POST(self):
        self._respond()

    def _respond(self):
        self.hits.record()

        if self.path.startswith("/api/v1/"):
            if not self.cvr_healthy.is_set():
                self._send(500, "application/json", b'{"error":"stub always fails"}')
                return
            body = CVR_BODY_TEMPLATE.format(cvr=self.cvr).encode("utf-8")
            self._send(200, "application/json", body)
        elif self.path.startswith("/v1/data") or self.path.startswith("/v1/tableinfo"):
            if not self.statbank_healthy.is_set():
                self._send(500, "application/json", b'{"error":"stub always fails"}')
                return
            self._send(200, "text/csv", STATBANK_CSV_BODY.encode("utf-8"))
        else:
            self._send(200, "application/json", b"{}")

    def _send(self, status, content_type, body):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):  # noqa: A002 -- stdlib signature
        pass  # keep the burst output readable; requests are logged by the caller instead


def start_stub(host, port, cvr, dependency_under_test):
    """Both dependencies are served locally; only `dependency_under_test` starts
    failing -- the other starts healthy so it never needs the real upstream."""
    cvr_healthy = threading.Event()
    statbank_healthy = threading.Event()
    events = {"cvr": cvr_healthy, "statbank": statbank_healthy}
    failing_event = events[dependency_under_test]
    for name, event in events.items():
        if name != dependency_under_test:
            event.set()

    hits = StubHits()
    handler = type("BoundStubHandler", (StubHandler,), {
        "cvr_healthy": cvr_healthy,
        "statbank_healthy": statbank_healthy,
        "cvr": cvr,
        "hits": hits,
    })
    try:
        server = http.server.ThreadingHTTPServer((host, port), handler)
    except OSError as ex:
        print(f"Could not bind stub server to {host}:{port}: {ex}", file=sys.stderr)
        sys.exit(1)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, failing_event, hits


def http_get(url, timeout):
    start = time.monotonic()
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:
            return resp.status, dict(resp.headers), resp.read(), time.monotonic() - start
    except urllib.error.HTTPError as ex:
        return ex.code, dict(ex.headers or {}), ex.read(), time.monotonic() - start
    except urllib.error.URLError as ex:
        return None, {}, str(ex).encode(), time.monotonic() - start


def wait_for_liveness(api_url, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status, _, _, _ = http_get(f"{api_url}/health/live", timeout=3)
        if status == 200:
            return True
        time.sleep(1)
    return False


LABEL_PAIR = re.compile(r'(\w+)="([^"]*)"')


def parse_metrics(text, metric_name):
    """Yield (labels, value) for every exposition line for metric_name (Prometheus text format)."""
    results = []
    for line in text.splitlines():
        if line.startswith("#") or not (
            line.startswith(metric_name + "{") or line.startswith(metric_name + " ")
        ):
            continue
        if "{" in line:
            name, rest = line.split("{", 1)
            labels_part, value_part = rest.rsplit("}", 1)
            labels = dict(LABEL_PAIR.findall(labels_part))
        else:
            name, value_part = line.split(None, 1)
            labels = {}
        if name != metric_name:
            continue
        try:
            results.append((labels, float(value_part.strip())))
        except ValueError:
            continue
    return results


def confirm_routed_through_stub(api_url, hits, timeout=10):
    """Hits /health/ready, which makes CvrApiHealthCheck and StatbankApiHealthCheck each
    do a cheap, Polly-free GET against their configured BaseUrl root -- outside the
    resilience pipeline entirely, so this can't accidentally feed the breaker under test.
    Returns how many of those probes landed on our stub (0, 1, or 2)."""
    before = hits.snapshot()
    http_get(f"{api_url}/health/ready", timeout=timeout)
    time.sleep(0.3)
    return hits.snapshot() - before


def read_circuit_state(api_url, dependency, timeout=10):
    status, _, body, _ = http_get(f"{api_url}/metrics", timeout=timeout)
    if status != 200:
        return None
    text = body.decode("utf-8", errors="replace")
    for labels, value in parse_metrics(text, "firmadata_circuit_state"):
        if labels.get("dependency") == dependency:
            return int(value)
    return None


def trip_breaker(api_url, dependency, cvr, year, max_requests, request_timeout):
    url = f"{api_url}/api/v1/companies/{cvr}?year={year}"
    print(f"\nFiring requests at {url} until the '{dependency}' circuit opens "
          f"(max {max_requests})...")
    for i in range(1, max_requests + 1):
        status, headers, _, elapsed = http_get(url, timeout=request_timeout)
        warning = headers.get("Warning", "")
        note = f" Warning={warning!r}" if warning else ""
        print(f"  [{i:2}] status={status} elapsed={elapsed:5.2f}s{note}")

        state = read_circuit_state(api_url, dependency)
        if state == 1:
            print(f"firmadata_circuit_state{{dependency=\"{dependency}\"}} == open after {i} request(s).")
            return True
    print("Circuit never opened within --max-requests. Check that Cvr__BaseUrl / "
          "Statbank__BaseUrl actually points at the stub printed above, and that the "
          "API process was restarted after setting it.")
    return False


def verify_fails_fast(api_url, dependency, cvr, year, request_timeout):
    url = f"{api_url}/api/v1/companies/{cvr}?year={year}"
    status, headers, _, elapsed = http_get(url, timeout=request_timeout)
    warning = headers.get("Warning", "")
    print(f"\nProbe while open: status={status} elapsed={elapsed:.2f}s"
          + (f" Warning={warning!r}" if warning else ""))
    print("(fast + no retry backoff is expected here -- Polly's BrokenCircuitException "
          "short-circuits before the request ever reaches the stub)")


def wait_for_recovery(api_url, dependency, cvr, year, request_timeout, break_duration, poll_interval=3):
    url = f"{api_url}/api/v1/companies/{cvr}?year={year}"
    deadline = time.monotonic() + break_duration + poll_interval * 3
    print(f"\nWaiting up to {break_duration + poll_interval * 3:.0f}s for BreakDuration "
          f"({break_duration}s) to elapse, sending a probe every {poll_interval}s -- Polly only "
          f"checks whether BreakDuration has elapsed when a call actually goes through the "
          f"pipeline, so recovery can't be observed by reading /metrics alone.")
    last_state = None
    while time.monotonic() < deadline:
        status, _, _, elapsed = http_get(url, timeout=request_timeout)
        state = read_circuit_state(api_url, dependency)
        if state != last_state:
            print(f"  probe: status={status} elapsed={elapsed:.2f}s -> circuit_state = {STATE_NAMES.get(state, state)}")
            last_state = state
        if state == 0:
            print("Breaker recovered to Closed.")
            return True
        time.sleep(poll_interval)
    print(f"Breaker did not close within the wait window (last state: "
          f"{STATE_NAMES.get(last_state, last_state)}).")
    return False


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dependency", choices=["cvr", "statbank"], default="cvr",
                         help="Which dependency's circuit breaker to test (default: cvr)")
    parser.add_argument("--api-url", default="http://localhost:8080",
                         help="Base URL of the running FirmaData.Api (default: %(default)s)")
    parser.add_argument("--cvr", default="16500836",
                         help="CVR number to query -- default is LB Forsikring A/S, the fixture used throughout the test suite")
    parser.add_argument("--year", default="2022", help="Statistics year to request (default: %(default)s)")
    parser.add_argument("--stub-host", default="0.0.0.0",
                         help="Interface the local always-fails stub binds to (default: %(default)s; "
                              "use 0.0.0.0 so a Dockerized API can reach it via host.docker.internal)")
    parser.add_argument("--stub-port", type=int, default=8199, help="Port for the local stub (default: %(default)s)")
    parser.add_argument("--no-stub", action="store_true",
                         help="Skip starting the local stub -- use when the target dependency's "
                              "BaseUrl is already pointed at a failing endpoint")
    parser.add_argument("--max-requests", type=int, default=20,
                         help="Safety cap on requests fired while trying to trip the breaker (default: %(default)s)")
    parser.add_argument("--request-timeout", type=float, default=20.0,
                         help="Per-request client timeout in seconds (default: %(default)s; "
                              "above the pipeline's 15s TotalRequestTimeout)")
    parser.add_argument("--break-duration", type=float, default=30.0,
                         help="BreakDuration configured on the breaker, for the recovery wait (default: %(default)s)")
    parser.add_argument("--skip-recovery", action="store_true",
                         help="Stop once the breaker is confirmed open; skip healing the stub and waiting for recovery")
    parser.add_argument("--no-prompt", action="store_true",
                         help="Skip the manual 'point the env vars at the stub, then press Enter' step -- "
                              "for callers that have already routed the API at the stub themselves "
                              "(see tools/run_test-circuit-breaker.bat). The safety check below still runs.")
    args = parser.parse_args()

    stub_url = f"http://localhost:{args.stub_port}/"

    healthy = None
    hits = None
    if not args.no_stub:
        _, healthy, hits = start_stub(args.stub_host, args.stub_port, args.cvr, args.dependency)
        other = "statbank" if args.dependency == "cvr" else "cvr"
        print(f"Stub server listening on {args.stub_host}:{args.stub_port}: '{args.dependency}' requests fail "
              f"(500) until healed, '{other}' requests succeed from the start.")
        if not args.no_prompt:
            print("Point BOTH env vars at it -- a company lookup resolves CVR before it ever calls Statbank, "
                  "so leaving the other BaseUrl unset still sends real traffic to it -- then (re)start the API:\n")
            print(f"    Cvr__BaseUrl={stub_url} Statbank__BaseUrl={stub_url} dotnet run"
                  f"    # bash, from solution/src/Backend/FirmaData.Api")
            print(f"    $env:Cvr__BaseUrl=\"{stub_url}\"; $env:Statbank__BaseUrl=\"{stub_url}\"; dotnet run"
                  f"    # PowerShell")
            print(f"\n(docker compose: same two env vars under firmadata-api.environment in docker-compose.yml, "
                  f"using http://host.docker.internal:{args.stub_port}/ instead of localhost)")
            try:
                input("\nPress Enter once the API is running against the stub... ")
            except EOFError:
                print("(non-interactive stdin -- continuing immediately)")
    else:
        print("--no-stub set: skipping the stub and the real-API safety check below -- "
              "this run will use whatever Cvr__BaseUrl / Statbank__BaseUrl the API already has, "
              "which may be the real apicvr.dk / api.statbank.dk.")

    print(f"\nWaiting for {args.api_url}/health/live ...")
    if not wait_for_liveness(args.api_url, timeout=30):
        print(f"API not reachable at {args.api_url} after 30s.", file=sys.stderr)
        sys.exit(1)
    print("API is live.")

    if hits is not None:
        routed = confirm_routed_through_stub(args.api_url, hits)
        if routed == 0:
            print(
                "\nERROR: the API does not appear to be routing to the local stub at all "
                "(0 of 2 expected /health/ready probes landed on it). It's most likely still "
                "pointed at the real apicvr.dk / api.statbank.dk -- a running dotnet/docker "
                "process does not pick up new env vars, so this usually means the API needs "
                "to be (re)started *after* setting Cvr__BaseUrl and Statbank__BaseUrl.\n"
                "Refusing to continue: by default this script never sends traffic to the real "
                "APIs. Fix the env vars and restart the API, or pass --no-stub to explicitly "
                "test against whatever it's currently configured with.",
                file=sys.stderr,
            )
            sys.exit(1)
        if routed == 1:
            print(
                "\nWARNING: only 1 of 2 expected /health/ready probes reached the stub -- one "
                "dependency's BaseUrl is likely still pointed at its real upstream and may "
                "receive real traffic during this run. Continuing anyway."
            )
        else:
            print(f"Confirmed: both dependencies route to the stub ({routed} probes received).")

    opened = trip_breaker(args.api_url, args.dependency, args.cvr, args.year,
                           args.max_requests, args.request_timeout)
    if not opened:
        sys.exit(1)

    verify_fails_fast(args.api_url, args.dependency, args.cvr, args.year, args.request_timeout)

    if args.skip_recovery:
        print("\n--skip-recovery set; done.")
        return

    if healthy is not None:
        healthy.set()
        print("\nStub healed (now returns 200) so the Half-Open probe can succeed.")
    else:
        print("\n--no-stub was set: point the dependency back at something healthy now "
              "if you want to see it recover.")

    recovered = wait_for_recovery(args.api_url, args.dependency, args.cvr, args.year,
                                   args.request_timeout, args.break_duration)
    sys.exit(0 if recovered else 1)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nInterrupted.")
        sys.exit(130)
