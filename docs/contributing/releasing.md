# Releasing Cove

A release publishes to four places: GitHub Releases (the standalone downloads and the manifest
Cove's built-in updater reads), the `ghcr.io` Docker images, and the `Cove.Core`, `Cove.Plugins` and
`Cove.Sdk` packages on NuGet.org. NuGet.org never takes the same version twice, so a version whose
packages went out can only be followed by a new one, never redone.

So `.github/workflows/release.yml` builds everything first and publishes nothing until every build
has succeeded. Then it publishes in order of how hard each step is to undo.

## Steps

1. Check that CI is green on the commit to release. CI builds the Docker images for amd64 only;
   the [Nightly](../../.github/workflows/nightly.yml) also builds them for arm64.
2. In `CHANGELOG.md`, add the `## [x.y.z] - YYYY-MM-DD` heading under `## [Unreleased]`, through a
   PR or a commit on `main`.
3. Push a signed tag on that commit: `git tag -s vX.Y.Z -m "Cove X.Y.Z"`, then
   `git push origin vX.Y.Z`. Stable tags must be in `main` history, or in `release/X.Y` for a
   maintenance release. `vX.Y.Z-rc.N` publishes a prerelease that does not move `latest`.
4. The Release run builds everything. Nothing is public while it does: the images go to the
   registry untagged, by digest. If any build fails, `publish` does not run.
5. Once every build has succeeded, `publish` checks, then publishes, stopping at the first failure:
   1. the build outputs are complete, this version is not already released, its packages are not
      already on NuGet.org, and NuGet.org gives the run a key through trusted publishing;
   2. the GitHub release, as a draft;
   3. the NuGet packages;
   4. the Docker tags: `X.Y.Z`, `X.Y` and `latest` for stable releases, `X.Y.Z-rc.N` for prereleases;
   5. the release, which makes it Latest and offers it to the built-in updater.
6. Once the packages are on NuGet.org, bump `CoveExtensionAbiBaselineVersion` in
   `Directory.Build.props`.

## When something fails

- **In a build, before `publish`.** Nothing was published. Fix the problem, delete the tag
  (`git push origin :refs/tags/vX.Y.Z`), and tag again, or re-run the failed jobs if the fix was
  outside the repository.
- **In `publish`, before the NuGet push** (a check failed, or creating the draft did). Nothing is
  public; a draft may exist, which only maintainers see. Fix the cause, for example a trusted
  publishing policy that does not match, and use **Re-run failed jobs**. A re-run replaces the draft.
- **In `publish`, at or after the NuGet push.** Some packages may be on NuGet.org, so this version
  can't be built again from another commit. Fix the cause and use **Re-run failed jobs** on the same
  run: it pushes the same files, skips packages already there, and carries on. If the run can't be
  re-run, for example because its artifacts have expired after 7 days, release the next patch
  version instead.

`.github/workflows/publish-plugins-package.yml` pushes the NuGet packages by hand, from a tag. It
is not part of a normal release, and needs its own trusted publishing policy (below).

## Repository setup

These need doing once, by a repository admin and by the owner of the Cove packages on NuGet.org:

- An environment named `release` (Settings → Environments), with its deployment tags limited to
  `v*` so that only release runs can use it. It needs no reviewers. Adding required reviewers later
  would make `publish` wait for one of them to approve before it publishes anything, without a
  workflow change. GitHub creates the environment itself on the first run that uses it, but without
  the tag limit.
- A [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) policy on
  the NuGet.org account that owns the `Cove.*` packages, with repository owner `yourcove`,
  repository `cove`, workflow file `release.yml` and environment `release`. There is no stored
  NuGet.org key: each run exchanges a GitHub token for a key that lasts an hour. A policy names one
  workflow file, so the manual `publish-plugins-package.yml` needs a second policy for that file, also
  with environment `release`. If no policy matches, the `publish` checks fail before anything is
  published.
- The repository variable `NUGET_USER`, set to that account's NuGet.org profile name.
