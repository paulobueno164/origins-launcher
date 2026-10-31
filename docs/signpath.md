# SignPath Foundation setup

Status on October 31, 2026: reconsideration requested; Foundation approval, organization/project details, API token and the actual release-certificate subject remain pending. Local and CI checks can be completed without any signing credential. Do not create an arbitrary identity or use another product's certificate.

## Scope and build origin

The artifact is only `OriginsLauncher.exe`, from `src/OriginsLauncher/OriginsLauncher.csproj`, version 2.5.8. The code and assets are MIT-licensed launcher files. The associated game service sells virtual currency and optional items; this relationship was disclosed to the Foundation. The game client is not part of the signing request.

The build and signing jobs in `.github/workflows/ci.yml` use GitHub-hosted Windows runners. The unsigned executable is uploaded as `OriginsLauncher-unsigned`; its artifact ID is passed to the official SignPath action for origin verification. Actions are pinned to full commit SHA. Pull requests build and test but cannot request signatures.

## Configure after Foundation approval

1. Use the organization and project identifiers provided/approved by SignPath. Add the predefined GitHub.com Trusted Build System to that organization/project and authorize the repository using the required official integration. Any new access must be reviewed by the owner.
2. Import `signpath/artifact-configuration.xml` as the project's default artifact configuration. The uploaded artifact is a ZIP containing exactly `OriginsLauncher.exe`. Restrictions enforce product `Origins Conquer Launcher`, company `Origins Conquer`, original filename, product version `${version}` and file version `${version}.0`. For 2.5.8 those versions are `2.5.8` and `2.5.8.0`.
3. Create/use a `release-signing` policy with the Foundation release certificate and **manual owner approval for every request**. Do not use a test certificate for a public release. Enforce repository ownership, GitHub origin and the permitted branch/tag in SignPath's own policy as well as CI.
4. In GitHub Actions configure only the provider-issued secret/variables:

| Type | Name | Value source |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | Restricted CI submitter token from SignPath |
| Variable | `SIGNPATH_ORGANIZATION_ID` | Approved organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | Approved project slug |
| Variable | `ORIGINS_SIGNER_SUBJECT` | Exact full subject of the actual Foundation release certificate |

Never put secret values in issues, documentation, logs, commits or workflow inputs. The configuration check reports names only. A local self-signed certificate is not a replacement for public trust.

## Request and verify a release

- Review the source, require green CI, check the complete version metadata, and keep all maintainers' GitHub/SignPath accounts protected by MFA.
- Run the `build` workflow manually on `main` with `sign_release=true`, or push the matching immutable version tag (for this snapshot, `v2.5.8`). Do not create the tag until signing approval/configuration is ready. A missing field makes the signing job fail explicitly.
- Inspect the request and manually approve it in SignPath. CI waits for the signed artifact. The request uses the same unsigned build's GitHub artifact ID and supplies its version parameter.
- CI downloads the unsigned artifact too. `check_signed_payload.py` refuses changes to the executable other than PE checksum/security-directory fields and the appended certificate. `Test-ReleaseTrust.ps1` then checks the Windows trust policy, exact publisher, code-signing purpose and timestamp.
- Only after verification succeeds does CI publish a GitHub release with the signed EXE, final SHA-256 and verification receipt. It never edits the website, downloads, launcher manifest, game server, database or API. Promotion to production requires a separate explicit owner request and validation of the signed output in dev.

Unsigned build artifacts are clearly named and retained for CI/provenance only. They are never used as a fallback if signing fails. Existing unsigned historical releases are not rewritten or represented as signed.

## Official references

- Eligibility and manual approval: https://signpath.org/terms
- GitHub origin verification and action inputs: https://docs.signpath.io/trusted-build-systems/github
- Artifact syntax: https://docs.signpath.io/artifact-configuration/syntax
- PE metadata restrictions: https://docs.signpath.io/artifact-configuration/reference#file-metadata-restrictions
