# Versioning and release policy

## Semantic versioning

Every package follows [SemVer 2.0](https://semver.org/). All packages are versioned and released together, from the
single `VersionPrefix` in `Directory.Build.props`, so a set of `Msynk.DotNetQuery*` packages with the same version
always works together.

| Change | Before 1.0 | From 1.0 |
| --- | --- | --- |
| Breaking change to a public API or to documented behavior | minor (`0.8` -> `0.9`) | major |
| New feature, new public API, new optional parameter on a new overload | minor | minor |
| Bug fix, performance improvement, documentation | patch | patch |

"Public API" means everything recorded in the packages' `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`
baselines, which the build enforces: a change to the public surface fails the build until the baseline is updated
in the same pull request, so every API change is visible in review.

"Documented behavior" includes the guarantees in the README (one fetch per key, ordered notifications, no stranded
observers, safe writes, offline awareness) and the semantics described in `docs/`. A fix that changes behavior
somebody may rely on is listed under **Changed** in the changelog and in the [migration guide](migration.md), even
when it ships in a minor release.

## What counts as breaking

- Removing or renaming a public type or member, changing a signature, or adding an abstract member to a public
  interface or abstract class that users implement.
- Changing a default (`StaleTime`, retry count, network mode, ...) or the meaning of an option.
- Changing the persisted format so that existing stored data cannot be read (persisted envelopes are versioned; use
  `Buster` and `Migrations` for application data).
- Raising the minimum supported target framework or a dependency's minimum major version.

Not breaking: adding members to concrete types, new overloads that do not create ambiguity, new optional features
that are off by default, internal changes, and new diagnostics IDs (`DNQ0xx`) at warning level.

## Supported frameworks

The libraries target the supported .NET LTS and STS releases (currently `net8.0` and `net10.0`; the Windows
adapters target `net10.0-windows`). Support for a framework is dropped only in a release that may break (minor
before 1.0, major after), after the framework is out of Microsoft support.

## Deprecation

A public API is first marked `[Obsolete("Use X instead.")]` (a warning) in a minor release, kept for at least one
further minor release, and removed in the next breaking release. The changelog lists deprecations.

## Release checklist

1. All CI jobs green on `main`: build, tests (core and Blazor on both target frameworks, generator, Playwright
   browser tests), vulnerability scan, sample smoke tests, AOT harness, MAUI build.
2. Move the `PublicAPI.Unshipped.txt` entries of every package into `PublicAPI.Shipped.txt`.
3. Update `VersionPrefix` in `Directory.Build.props`.
4. Move the changelog's "Unreleased" section under the new version with the date; update the migration guide.
5. Tag the commit `vX.Y.Z`. The CI run for that commit uploads the packed `.nupkg` and `.snupkg` files (with
   SourceLink metadata) as the `nuget-packages` and `nuget-maui` artifacts.
6. Push those packages to nuget.org (symbols included) and publish the GitHub release with the changelog section.

## Pre-releases

Previews use SemVer pre-release labels (`0.9.0-preview.1`) built from the same `VersionPrefix` plus a
`VersionSuffix`. Pre-release APIs may change between previews; they are frozen when the release is cut.
