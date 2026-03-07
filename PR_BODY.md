## Summary
Adds production-grade CI/CD focused only on PR validation and protected staging deployment.

## What Runs on PR
- Workflow: `.github/workflows/pr-validation.yml`
- Trigger: `pull_request` to `main` on relevant .NET/Python/workflow path changes
- Steps:
  - .NET setup from `global.json` if present, else .NET 10 LTS fallback
  - `dotnet restore`, `dotnet build --configuration Release`, `dotnet test --configuration Release`
  - Python syntax smoke (`compileall`) when Python files exist
  - concise `GITHUB_STEP_SUMMARY`
- Controls:
  - least-privilege `permissions: contents: read`
  - PR concurrency cancel-in-progress

## What Runs on Main (Staging)
- Workflow: `.github/workflows/deploy-staging.yml`
- Trigger: `push` to `main` on relevant paths, plus `workflow_dispatch`
- Hard gates:
  - only runs on protected branch (`github.ref_protected == true`)
  - deploys to GitHub Environment `staging` (supports manual approval gate)
- Steps:
  - restore/build/test before deploy
  - Azure OIDC login (`azure/login@v2`)
  - immutable image build + push tagged with `${{ github.sha }}`
  - deploy to existing Azure Container Apps target
  - post-deploy smoke check (`/health` default or `STAGING_HEALTHCHECK_URL`)
  - publish deployment metadata (image, revision, commit, actor) in summary + artifact
- Controls:
  - least-privilege `permissions` (`contents:read`, `id-token:write`)
  - deploy concurrency group to avoid overlap
  - explicit artifact retention (`retention-days: 30`)
  - no deploys on PRs/forks

## Required Secrets
- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

## Required Repository Variables
- `ACR_NAME`
- `ACR_LOGIN_SERVER`
- `ACR_IMAGE_REPOSITORY`
- `ACA_RESOURCE_GROUP`
- `ACA_APP_NAME`
- `STAGING_HEALTHCHECK_URL` (optional)

## Repo Docs Updated
- `README.md` CI/CD section includes secrets/variables, required branch protections/checks, fork safety, and rollback runbook.

## Break-Glass Rollback
1. Identify last known-good SHA-tagged image in ACR.
2. Run `az containerapp update --image <known-good-image>` against staging app.
3. Verify staging health endpoint returns 200.
4. Record actor/commit/reason in incident notes.
