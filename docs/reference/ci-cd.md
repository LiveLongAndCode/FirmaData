# CI/CD

## GitHub Actions — the main pipeline

[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) runs on every push to `main` and every
pull request against it, in two jobs:

```
PR / push to main
  │
  ▼
build-test    restore (NuGet cached) → build Release (warnings-as-errors)
  │           → dotnet test --filter "Category!=Live"
  │           → test results + coverage summary posted to the job summary
  ▼
docker        matrix over api + web: build both Dockerfiles
              → push to GHCR — main only
  ▼
ghcr.io/<owner>/<repo>-{api,web}:latest  and  :sha-<short-sha>
```

Two details worth knowing:

* **Pull requests build the images but never push them.** The registry login, the push, and the
  Trivy scan are all gated on `github.ref == 'refs/heads/main'`, so an untrusted PR branch can
  prove the Dockerfile still works without landing an image in the registry.
* **Trivy is informational.** The image scan runs with `continue-on-error` and `exit-code: "0"` —
  it reports, it never blocks the pipeline.

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` come from `Directory.Build.props`, so
analyzer warnings fail the build without any vendor-specific flag in the workflow.

## Live smoke tests

[`.github/workflows/live-smoke.yml`](../../.github/workflows/live-smoke.yml) runs the
`Category=Live` tests against the real `apicvr.dk` / `api.statbank.dk` on `workflow_dispatch` plus
a nightly cron (05:00 UTC).

It is deliberately kept out of the PR gate: a failure there means the upstream contract changed,
not that the code is broken, so it must never block a merge. See
[ADR-0005](adr/0005-hermetic-tests-with-opt-in-live-smoke.md) and [testing](testing.md).

## Azure Pipelines — the portability demo

[`azure-pipelines.yml`](../../azure-pipelines.yml) exists to prove the same solution builds and tests
cleanly on a second CI vendor. It mirrors `ci.yml`'s `build-test` job using Azure Pipelines' own
native tasks, and adds a Docker **build** for the same two Dockerfiles — no push, no registry
credentials needed. `live-smoke.yml` is not mirrored; out of scope for the demo.

It uses `pool: Default` (a self-hosted agent) rather than `vmImage: ubuntu-latest`: new Azure
DevOps organisations no longer get an automatic free grant of Microsoft-hosted parallel jobs, while
a self-hosted agent gets one free parallel job with no approval. Switch back to
`vmImage: ubuntu-latest` if a hosted grant is ever approved.

## Platform-agnosticism

The application layer is platform-agnostic; the CI/CD layer is not.

* **Application layer.** No cloud-vendor SDKs anywhere in `Directory.Packages.props` — no AWS,
  Azure, or GCP client libraries. Cache is `IMemoryCache` (in-process), resilience is Polly (a
  library, not infrastructure), observability is OpenTelemetry + Prometheus/Grafana (vendor-neutral
  OSS, not CloudWatch or Azure Monitor), and configuration is `appsettings.json` + environment
  variables only. The Dockerfiles produce standard OCI images that run unmodified on Docker,
  Kubernetes, ECS, Azure Container Apps, Render, Fly.io, or anywhere else that runs a container.
* **CI/CD layer.** `ci.yml` is written in GitHub Actions syntax and pushes to GHCR specifically —
  running it on GitLab CI or similar would need translating, which is exactly what
  `azure-pipelines.yml` demonstrates. Only the *pipeline* is vendor-bound, though; the *image* it
  produces is still a portable OCI artifact any registry or host can run.
