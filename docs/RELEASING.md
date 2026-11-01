# Release Corewright

Only the owner publishes releases. The public repository and published release
assets are readable anonymously; Corewright contains no GitHub credentials.

1. Bump `<Version>` in `Directory.Build.props` and keep the UI package version in
   `ui/package.json` and its lockfile consistent. Commit and merge the reviewed
   change to main.
2. The owner creates and pushes a matching tag, for example `v0.1.0`. Agents do
   not tag or publish. The **Release** workflow also supports manual dispatch
   with an existing version tag; selecting a branch is not sufficient.
3. CI checks the tag against `<Version>`, builds on `windows-latest` with .NET 10,
   current Node LTS and NSIS, then runs `scripts/package.ps1`. It uploads
   `Corewright-<version>-setup.exe` and `SHA256SUMS.txt` to a **draft** GitHub
   release with generated notes. The build and release jobs have separate
   permissions; only the draft job receives repository write permission.
4. The owner reviews the draft, notes and artifacts, validates the installed
   upgrade/uninstall path, and publishes it in GitHub. CI never publishes a
   release. A repeat run fails if that release already exists; the owner reviews
   and removes an unwanted draft before recreating it.

`SHA256SUMS.txt` uses sha256sum format: a lowercase 64-digit SHA-256, two spaces,
and the exact setup filename. These two asset names are the dashboard updater's
contract. Preserve them when publishing. A prerelease tag creates a draft
prerelease; published prereleases and drafts are excluded from the stable updater.
No published release yet is a normal state and appears as such in Settings.

The dashboard checks `/repos/rennerdo30/corewright/releases/latest` on opening,
at most once a day, or on **Check now**. It downloads only after **Install update**
is chosen, verifies SHA-256 immediately before requesting administrator consent,
and lets setup perform the silent upgrade. Development copies link to the
published release instead of offering installation. Closing the dashboard cancels
network activity. The service never checks for updates.

Checksums protect download integrity; they are served by the same trusted GitHub
release as the installer. Authenticode and independent update signatures are not
implemented. No signing secret is needed by this workflow. The app never receives
the short-lived GitHub Actions token used to create the draft.
