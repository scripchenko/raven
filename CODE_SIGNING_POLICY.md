# Code signing policy

## Status

raven is preparing an application to the SignPath Foundation Open Source Code Signing program. The project has not been accepted, SignPath integration is not active, and no raven release is signed by SignPath. The published raven v0.1.0 release is unsigned and will remain so.

The required attribution below applies only after the project is accepted and only to releases that are actually signed:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

## Project and build origin

- Source repository: <https://github.com/scripchenko/raven>
- Releases intended for signing must originate from reviewed source code and build scripts in this repository.
- Any future integration is intended to use a trusted GitHub build workflow and the origin verification required by the SignPath Foundation program.
- Every release signing request requires explicit manual approval. Signing is not approved automatically.

## Team roles

This is currently a one-person project. The same maintainer fills each role:

- **Committer:** Dmitry Skripchenko / GitHub [@scripchenko](https://github.com/scripchenko)
- **Reviewer:** Dmitry Skripchenko / GitHub [@scripchenko](https://github.com/scripchenko)
- **Signing approver:** Dmitry Skripchenko / GitHub [@scripchenko](https://github.com/scripchenko)

Changes proposed by external contributors must be reviewed by the maintainer before merge. A signing approver must explicitly inspect and approve each signing request. Only artifacts built from raven source and build scripts in the repository may be submitted for signing.

## Signing scope

If the project is accepted, the intended signing scope is limited to:

- raven Windows application binaries produced from this repository;
- the raven Windows installer produced from this repository.

Third-party or upstream binaries are not raven-owned artifacts and will not be signed as raven binaries. They may remain separately signed or unsigned according to their upstream distribution and license. The project will configure signing restrictions so that only raven-owned artifacts with the expected raven product identity and consistent release version are signed.

## Privacy

Raven's network behavior and local data handling are described in the [Privacy policy](PRIVACY.md). The policy covers third-party services used by the application as well as raven's own update check.

## Release status

The raven v0.1.0 installer and application binaries are unsigned. This policy does not imply that SignPath Foundation has accepted the project or that any signed release exists. Future release notes will identify signing only when that specific release has actually been signed.
