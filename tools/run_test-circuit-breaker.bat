@echo off
setlocal

rem Complete circuit-breaker test run against the docker compose stack.
rem
rem The test needs firmadata-api pointed at test-circuit-breaker.py's local stub rather than the
rem real apicvr.dk / api.statbank.dk -- the script refuses to fire any traffic otherwise. That
rem routing is what this wrapper sets up, runs the test under, and (always) tears down again.

set "REPO_ROOT=%~dp0.."
set "OVERLAY=%~dp0tests\docker-compose.circuit-test.yml"
set "TEST_EXIT=1"

pushd "%REPO_ROOT%" || (echo Could not enter repo root "%REPO_ROOT%".& pause & exit /b 1)

echo.
echo === [1/3] Pointing firmadata-api at the local stub ===
docker compose -f docker-compose.yml -f "%OVERLAY%" up -d firmadata-api
if errorlevel 1 (
    echo.
    echo Failed to start firmadata-api with the test overlay. Is Docker Desktop running?
    goto :restore
)

echo.
echo === [2/3] Running the circuit breaker test ===
rem --no-prompt: this wrapper already did the routing the prompt asks the user to do by hand.
rem The script's own safety check still runs and still aborts if the routing didn't take effect.
python "%~dp0tests\test-circuit-breaker.py" --dependency cvr --api-url http://localhost:8080 --no-prompt
set "TEST_EXIT=%errorlevel%"

:restore
echo.
echo === [3/3] Restoring firmadata-api to the real APIs ===
rem Always runs, including after a failed test -- leaving the container pointed at a stub that
rem died with the script would break the app for normal use.
docker compose -f docker-compose.yml up -d --force-recreate firmadata-api

popd

echo.
if "%TEST_EXIT%"=="0" (
    echo RESULT: circuit breaker test PASSED.
    echo Metrics are in Grafana at http://localhost:3000 -- see the "Circuit breaker state" panel.
) else (
    echo RESULT: circuit breaker test FAILED ^(exit code %TEST_EXIT%^).
)

echo.
pause
exit /b %TEST_EXIT%
