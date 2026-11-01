# Release Corewright

Releases publish automatically (owner, 2026-11-02: "the release should auto publish, i dont need to say anything,
you decide"). The agent working on Corewright decides when a state is ready: betas freely, a stable release only
after the beta it comes from was tested. Required reviews and checks still apply. The public repository and
published release assets are readable anonymously; Corewright contains no GitHub credentials.

1. Bump `<Version>` in `Directory.Build.props` (for example `0.1.0-beta.2` or `0.1.0`) and keep the UI package
   version in `ui/package.json` and its lockfile consistent. Commit the reviewed change to main.
2. Create and push a matching annotated tag, for example `git tag -a v0.1.0-beta.2 -m "…"` and
   `git push origin refs/tags/v0.1.0-beta.2`. The **Release** workflow also supports manual dispatch with an
   existing version tag; selecting a branch is not sufficient.
3. CI checks the tag against `<Version>`, builds on `windows-latest` with .NET 10, current Node LTS and NSIS, then
   runs `scripts/package.ps1`. It publishes `Corewright-<version>-setup.exe` and `SHA256SUMS.txt` as a GitHub
   release with generated notes; a tag with a suffix (`-beta.N`) becomes a pre-release. The build and release
   jobs have separate permissions; only the release job receives repository write permission. A repeat run fails
   if that release already exists.
4. Deploy the website when its text changed (Actions → "Website Pages"); the download section and `/beta/` read
   the newest releases from the GitHub API, so a new release needs no website change.

`SHA256SUMS.txt` uses sha256sum format: a lowercase 64-digit SHA-256, two spaces, and the exact setup filename.
These two asset names are the dashboard updater's contract.

Update channels (Settings → Updates): **Stable** checks `/repos/rennerdo30/corewright/releases/latest` (published
stable releases only); **Beta** takes the newest published release, beta or stable. Until the user picks one, the
channel follows the installed version, so a beta setup updates on Beta. The dashboard checks on opening, at most
once a day, or on **Check now**. It downloads only after **Install update** is chosen; the elevated updater
downloads and verifies SHA-256 in an administrator-only folder before it runs setup. Development copies link to
the published release instead of offering installation. Closing the dashboard cancels network activity. The
service never checks for updates.

Checksums protect download integrity; they are served by the same GitHub release as the installer. Authenticode
and independent update signatures are not implemented. No signing secret is needed by this workflow. The app
never receives the short-lived GitHub Actions token used to publish.
