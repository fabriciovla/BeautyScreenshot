# Contributing

Thanks for helping make Beauty Screenshot better.

## Before you start

For anything larger than a small fix, open an issue first so we can agree on the approach. Beauty Screenshot aims to
stay simple: fewer options done well.

## Development

1. Install Visual Studio 2022 or its Build Tools, with MSBuild.
2. Build and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   .\BeautyScreenshot.exe
   ```

Only one instance runs at a time. Quit any installed Beauty Screenshot from its tray icon before running a
development build.

## Guidelines

- Code, comments and commit messages are in English.
- Keep comments few and technical: explain why, not what.
- User-facing text goes through `L.T("Español", "English")` in C#. Fixed XAML texts are written in English and listed
  in the window's Spanish table.
- The app targets .NET Framework 4.8 so it needs no runtime besides Windows itself.
- Follow the style in `.editorconfig`.

## Branches

| Branch | Purpose |
| --- | --- |
| `main` | Released versions only. Every merge is tagged (`v0.1.1`, `v0.2.0`…). |
| `develop` | Integration branch for the next release. Pull requests target it. |
| `feat/…`, `fix/…`, `docs/…` | One change each, branched from `develop` and deleted after merging. |
| `release/x.y.z` | Final polish of a release, branched from `develop` and merged into `main` and `develop`. |
| `hotfix/x.y.z` | Urgent fixes to a release, branched from `main` and merged into `main` and `develop`. |

## Commits and pull requests

- Branch from `develop` (`feat/…`, `fix/…`, `docs/…`) and open the pull request against it.
- Use [Conventional Commits](https://www.conventionalcommits.org): `feat(sidebar): …`, `fix(kit): …`, `docs: …`.
- Keep pull requests focused on one change and describe how you tested it.
