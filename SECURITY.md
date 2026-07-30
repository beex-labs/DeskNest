# Security Policy

## Supported versions

BeeX DeskNest is under active development. Security fixes are applied to the
latest released version and the `main` branch. Older builds are not maintained.

| Version | Supported |
| --- | --- |
| Latest release / `main` | ✅ |
| Older builds | ❌ |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Report vulnerabilities privately through **GitHub Security Advisories**:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability** (Private vulnerability reporting).
3. Provide a clear description, affected version/commit, reproduction steps and
   any proof-of-concept, and the potential impact.

We will acknowledge your report as soon as reasonably possible, keep you updated
on progress, and coordinate a disclosure timeline with you. Please give us a
reasonable window to release a fix before any public disclosure.

## Scope

The following are in scope:

- The main application (`BeeX DeskNest.exe`).
- The OCR sidecar project (`src/OCR/`, `BeeX_OCR.exe` / `BeeX_Formula.exe`).

Some behavior is expected and **not** considered a vulnerability by itself:

- The system cleaner (BeeXCleaner) performs privileged operations (uninstalling
  programs, deleting residual files, registry backup/restore, free-space wiping)
  by design and requests elevation before doing so.
- Registry access is limited to reading a machine identifier, registering the
  app for startup, and setting the AppUserModelID.
- User data is stored outside the repository under the configured BeeX data root
  (default `D:\BeeX` / `C:\BeeX`) and `%LocalAppData%\BeeX`.

## Third-party components

Vulnerabilities in third-party dependencies (see [NOTICE](NOTICE)) should be
reported upstream to their respective projects. If a dependency issue affects
BeeX DeskNest specifically, feel free to let us know as well.
