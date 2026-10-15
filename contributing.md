# Contributing to DS4Windows

Use [this project's issues](https://github.com/hbashton/DS4Windows/issues) and
[pull requests](https://github.com/hbashton/DS4Windows/pulls).
Credit upstream contributors when importing fixes; keep upstream issue links
explicit so readers know which repository they belong to.

## Report a bug

Check [Troubleshooting](docs/troubleshooting.md) and search existing reports.
Include app version, Windows build, controller model, USB/Bluetooth connection,
virtual output, installation type, steps and relevant logs. Review logs for
personal data. A reproducible small case is more useful than a long speculation.

## Propose a change

Start from the current default branch. Explain the concrete problem, resulting
behaviour and validation. Keep unrelated changes separate. For controller and
audio changes, describe ownership, ordering and performance implications where
relevant. Tests must exercise behaviour rather than merely duplicate implementation.

Use [Development](docs/development.md) for build/test commands and
[Technical reference](docs/reference.md) for runtime contracts. State which
hardware was tested and which checks remain unverified. Documentation-only
changes need link and content checks; they do not require installing software.

## Documentation and releases

- Keep README short and point readers to the documentation index.
- Put user instructions in the maintained guide rather than another root-level copy.
- Keep release history in CHANGELOG.md; retain dated validation as evidence.
- Mark design proposals and historical records clearly. Age alone does not make
  an implementation contract obsolete.
- Keep source, package and installer versions coordinated through the
  [release workflow](.github/workflows/release.yml).
- Preserve licensing notices and credits. Do not imply that an unsigned build is signed.
