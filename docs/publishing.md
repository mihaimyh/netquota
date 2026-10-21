# Publishing

Packages are published by `.github/workflows/release.yml` when a version tag is pushed. Versioning
is derived from the tag by GitVersion; no version is committed in the source.

## Cut a release

```bash
git tag v1.2.3
git push origin v1.2.3
```

The workflow:

1. checks out with full history,
2. runs GitVersion (`GitVersion.yml`, `GitHubFlow/v1`, tag prefix `v`),
3. restores/builds/tests,
4. packs **one package per project** (`NetQuota`, `NetQuota.AspNetCore`, `NetQuota.Billing`,
   `NetQuota.Firestore`, `NetQuota.Postgres`, `NetQuota.Redis`) with `-p:Version=<semVer>`,
5. fails if the package count is not 6,
6. exchanges a GitHub OIDC token for a short-lived NuGet API key (`NuGet/login@v1`),
7. pushes `.nupkg` and `.snupkg` to nuget.org with `--skip-duplicate`,
8. uploads the artifacts and creates a GitHub release.

`+semver:major|minor|patch|none` in a commit message is honoured by GitVersion for non-tagged
builds.

## Trusted Publishing (no long-lived API key)

Configure once on nuget.org:

1. Sign in, open **Trusted Publishing** (username menu) → **Add a new policy**.
2. **Repository Owner**: `mihaimyh`
3. **Repository**: `netquota`
4. **Workflow File**: `release.yml` (file name only, no `.github/workflows/` prefix)
5. **Environment**: leave empty (the workflow does not use a GitHub environment)
6. Scopes: allow `Push new packages and package versions`, glob `NetQuota*`.

Then add the GitHub secret:

- `NUGET_USER` = your nuget.org **profile name** (not the email address).

The first successful publish permanently activates the policy (policies can start with a 7-day
activation window). The temporary key lives for one hour, so the login step runs right before the
push.

## Manual publish (fallback)

API keys still work for local publishing:

```powershell
$env:NUGET_API_KEY = '<key>'
dotnet pack NetQuota.slnx -c Release -o artifacts/packages -p:Version=1.2.3
Get-ChildItem artifacts/packages/*.nupkg, artifacts/packages/*.snupkg | ForEach-Object {
    dotnet nuget push $_.FullName --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate
}
```

## Local build

Without `-p:Version`, MSBuild defaults to `1.0.0`. Use the property to preview a package:

```bash
dotnet pack NetQuota.slnx -c Release -o artifacts/packages -p:Version=1.2.3-local
```
