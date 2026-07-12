# Testing-first release workflow

Pushing a version tag builds the plugin once, publishes that exact ZIP as a GitHub prerelease, and advertises it through Dalamud's Testing channel. Promotion waits at the `release-approval` GitHub Environment. Approval changes the existing release and `repo.json`; it does not rebuild or replace the ZIP.

## One-time GitHub setup

1. Open **Settings > Environments** and create an environment named `release-approval`.
2. Enable **Required reviewers** and select your own GitHub account.
3. Do not enable **Prevent self-review** if the tag is normally pushed by your account.
4. Under **Settings > Actions > General > Workflow permissions**, allow **Read and write permissions** so the workflow can create releases and push `repo.json` commits.

Only users who enable testing plugins in Dalamud will see the `Testing*` build. This is an opt-in channel, not an access-control mechanism: if somebody else uses this custom repository and opts into testing plugins, they can also receive it.

## Release

Create and push one semantic-version tag. The tag is the build's version source, so the project file does not need a separate version edit.

```powershell
git tag v0.3.2
git push origin v0.3.2
```

The workflow then:

1. Injects `0.3.2` into the build and produces `EventHorizon.zip` once.
2. Creates the `v0.3.2` GitHub Release as a prerelease.
3. Leaves the stable fields in `repo.json` unchanged and writes only `TestingAssemblyVersion`, `TestingChangelog`, `TestingDalamudApiLevel`, and `DownloadLinkTesting`.
4. Waits for approval on the `release-approval` Environment.
5. On approval, marks the same GitHub Release as stable, copies the generated manifest into the stable fields, and removes the `Testing*` fields.

Before approving, enable plugin testing globally in Dalamud and opt Event Horizon into its testing version. Then update Event Horizon in place, restart the client, and exercise the high-risk paths changed by the release.

## Rejecting a build

Rejecting the Environment deployment stops the promotion job. The GitHub prerelease and its Testing-channel metadata remain unchanged. GitHub does not run a rejection cleanup step for a rejected Environment deployment.

Because the workflow uses a workflow-level concurrency group, reject or cancel the pending release workflow before publishing another version.

Once a testing build has been installed, do not reuse its version or publish a lower replacement. Increment the version for every replacement candidate:

- rejected: `0.3.2`
- replacement testing build: `0.3.3`
- eventual stable build: `0.3.3` or later

Publishing a higher testing version automatically replaces the Testing-channel metadata in `repo.json`. The old prerelease may be retained for diagnosis or deleted manually. Removing Testing metadata does not downgrade clients that already installed the rejected build.

Do not force-move or reuse a published version tag. Every candidate version is immutable once its prerelease assets have been created.
