# Maintenance roadmap

[Documentation index](README.md) · [Contributing](../contributing.md)

This is a set of maintenance priorities and proposals, not a release promise.
Track concrete work in [the issue tracker](https://github.com/hbashton/DS4Windows/issues)
with reproduction steps, scope and acceptance criteria.

## Priorities

- Reproduce and triage new controller, startup and update reports against the
  published build, recording controller model and connection type.
- Expand hardware verification for feedback, audio, reconnection and multi-controller
  use. Preserve the distinction between unit tests and hardware evidence.
- Preserve signing, dependency and installer validation requirements when preparing releases.
- Keep installer, portable package, updater and backend compatibility aligned.
- Review proposed fixes individually and preserve attribution.
- Maintain user guides and translations alongside changes to the UI.

## Proposals needing design

The old TODO mentioned action-set support. Special Actions and profile switching
already exist, but a broader action-set design needs a concrete use case, a
definition of how it differs, and profile compatibility requirements before implementation.

The former completed-task list is available in Git history. Release changes are
recorded in [CHANGELOG.md](../CHANGELOG.md); dated investigations remain in
[the validation archive](validation/README.md).
