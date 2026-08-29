# Changelog

## 0.19.0

- Added automatic World/Avatar project detection, multi-avatar target selection, and avatar metadata editing.
- Added saved-session selection and management for scripts, Windows Credential Manager, and macOS Keychain.
- Added atomic bridge replacement and one-operation-per-project locking.
- Added post-upload server verification, phase timings, artifact hashes, version provenance, and resumable upload recovery manifests.
- Added native Windows, Apple silicon, and Intel macOS CI/release builds.
- Added VPM listing publication, SPDX SBOMs, SHA-256 checksums, and GitHub artifact attestations.
- Added clean-project compatibility compilation for Worlds and Avatars SDK 3.9.0 and latest.

## 0.11.0

- Removed all compatibility CLI aliases; legacy spellings now fail as unknown options.
- Removed the `VRCLI_WORLD_ID` fallback, the `world` configuration key, environment-variable-name options, and abbreviated platform values.
- Accepts any maximum or recommended capacity of at least 1 instead of enforcing an upper limit of 80.
- Updated the interactive wizard, ownership diagnostics, documentation, and security guidance to use only the canonical interface.

## 0.10.0

- Renamed the primary existing-world option to `--blueprint <wrld_id>`; the former `--world` spelling remains a compatibility alias.
- Changed `--new` into a standalone flag and requires the new world's name through `--title <name>`.
- Fails before Unity starts when a new deployment is missing `--title` or `--thumbnail` metadata.
- Added `blueprint` and boolean `new` configuration fields plus the primary `VRCLI_BLUEPRINT_ID` environment variable.
- Updated the interactive wizard and CI samples to use the same deployment syntax.

## 0.9.0

- Existing-world deployments can selectively update the title, description, thumbnail, maximum capacity, recommended capacity, and tags while preserving unspecified server metadata.
- Existing tags are merged instead of replacing SDK-managed tags, and capacity reductions clamp an unchanged recommended capacity when necessary.
- Simplified `--help` to a single parameter-and-description list without quick starts or examples.
- Documents `--login <username-or-email>` together with `--password <password>` and warns that command-line secrets can appear in shell history.
- Uses the full `StandaloneWindows64` and `Android` platform names in help, documentation, configuration, and CI samples.

## 0.8.0

- Introduced a concise automation syntax: `--project`, `--world`, `--new`, `--login`, `--plain`, and `--yes` replace combinations of similarly named long-form options.
- Uses the current directory as the default project and Windows as the default platform; accepts the short `windows` and `quest` platform names.
- Reads account, password, optional TOTP secret, world ID, project, and platform from fixed `VRCLI_*` environment variables without requiring environment-variable-name options.
- Added strict `vrcli.json` project configuration with automatic discovery, command-line override precedence, relative-path resolution, comments, and trailing-comma support.
- Simplified `--help`, README examples, GitHub Actions workflows, and Jenkins pipelines around the concise non-TUI workflow.
- Preserved existing long-form arguments as compatibility aliases so existing automation continues to parse.

## 0.7.2

- Replaced the redundant `NEW DEPLOYMENT` header label with the responsive account/deployment/review route, removing the duplicate route row below it.
- Added guarded double Ctrl+C cancellation: the first press shows an in-screen warning and the second press within 30 seconds follows the same clean cancellation path as Esc.

## 0.7.1

- Makes content ownership certification default to Yes in the interactive wizard.
- Cancels the wizard immediately when ownership certification is declined, preventing an already-recorded server consent from allowing that declined deployment to continue.

## 0.7.0

- Replaced the line-oriented local UI with a Vim-style alternate-screen application spanning account setup, deployment configuration, review, build, and upload.
- Uses differential row updates after the initial screen transition, avoiding whole-screen clears and reducing cursor jumps and flicker.
- Keeps CI and redirected output in plain append-only log mode without terminal control sequences.
- Checks the SDK's saved session before credential login and displays two-factor controls only when VRChat actually returns a challenge.
- Removed the wizard's up-front two-factor selection; a configured `VRCLI_TOTP_SECRET` is used automatically only when needed, otherwise the challenge opens an in-screen method/code dialog.
- Hardened primary credential validation by requiring either a complete verified user or an authentication cookie with a two-factor challenge, so malformed challenge-shaped responses are rejected.

## 0.6.1

- Redesigned the pre-deployment wizard with a responsive framed header, numbered account/deployment/review sections, consistent prompts, and a compact confirmation card.
- Added a responsive deployment overview before the activity timeline begins, showing project, scene, target world, and platform when terminal height permits.
- Shows zero to four contextual detail records beneath each active stage based on terminal height, while truncating new records and active messages to the current width.
- Preserves the append-only rendering model: details are emitted once and the renderer still never moves the cursor into previous terminal rows or clears the screen.

## 0.6.0

- Reordered the local wizard around account-first setup and validates primary VRChat credentials before collecting project and world deployment details.
- Added an explicit local authentication-method choice: prompt on challenge, automatic Base32 TOTP, or a current authenticator code.
- Added a private per-process named-pipe channel so Unity can request TOTP, email OTP, or recovery-code input without failing and restarting the deployment.
- Replaced full-frame terminal repainting with an append-only activity timeline that updates only the current line, eliminating cursor jumps and whole-screen flicker.
- Added compact stage timing, context details, upload progress, stable cursor visibility, and cleaner success/failure summaries.

## 0.5.3

- Resolves the first enabled build scene before authentication, or safely selects the only `.unity` scene under `Assets` when Build Settings is empty.
- Makes the interactive wizard display discovered scenes and require a valid explicit selection before deployment confirmation.
- Reports post-login context failures under Project context instead of Authentication and filters a harmless Unity client-listener warning from the TUI error tail.
- Preserves the provisional Blueprint ID for a new world across the local two-factor authentication retry.

## 0.5.2

- When the local deployment wizard encounters a VRChat TOTP challenge without configured 2FA, it securely prompts for the current authenticator code and retries authentication before any build or upload begins.
- Keeps unattended CI non-interactive; CI must continue to supply `--totp-secret-env` or another explicit two-factor option.
- Hides the unrelated Mono debugger-listener warning from the interactive error tail.

## 0.5.1

- Automatically initializes the `UDON` and `VRC_SDK_VRCSDK3` scripting defines required by the Worlds SDK before Unity compiles the VRCLI bridge.
- Fails early with a clear diagnostic when the Worlds SDK editor package is unavailable after dependency restore.
- Separates Unity startup and compilation from bridge installation in the terminal UI and preserves complete compiler-error lines in the final error tail.

## 0.5.0

- Added an interactive ANSI terminal UI with a spinner, stage checklist, deployment context, and upload progress bar.
- Added a guarded interactive deployment wizard when `VRCLI deploy` is run without options in a local terminal.
- Added automatic CI/redirection detection plus explicit `--tui` and `--no-tui` modes.
- Keeps raw Unity diagnostics in append-only mode and shows a short error tail when an interactive deployment fails.

## 0.4.0

- Added timestamped authentication, context, preparation, build, signature, upload, and completion phase logs.
- Added authenticated project, scene, Unity, SDK, platform, world-version, bundle-size, and upload-plan details.
- Added throttled component and overall upload progress reporting without logging credential or signature values.

## 0.3.0

- Added unattended RFC 6238 TOTP generation from a protected environment variable.
- Reuses a valid VRChat SDK session before accessing password or TOTP authentication.
- Updated GitHub Actions and Jenkins examples for TOTP-backed deployments.

## 0.2.0

- Added explicit new-world creation with name, description, thumbnail, capacities, and tags.
- Added generated Blueprint IDs to structured output and optional Blueprint output files.
- Added first-release CI examples that create on Windows before uploading Android to the same world.

## 0.1.0

- Added username/password and one-time two-factor authentication.
- Added explicit scene and blueprint selection.
- Added Windows and Android world build/upload support.
- Added structured results and CI exit codes.
