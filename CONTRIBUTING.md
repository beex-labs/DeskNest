# Contributing to BeeX DeskNest

Thanks for your interest in contributing! This document explains how to build
the project, the coding conventions we follow, and how to submit changes.

## Getting started

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows
   10 (build 19041+).
2. Fork and clone the repository.
3. Build and test:

   ```powershell
   dotnet build src/BeeX.DeskNest.csproj -c Debug
   dotnet test  test/BeeX.DeskNest.Tests.csproj
   ```

The OCR sidecar (`src/OCR/BeeX_OCR.csproj`) is a separate project and
is intentionally excluded from the main build. Build it on its own only when you
are working on OCR.

## Coding conventions

- Target the existing style; a shared [`.editorconfig`](.editorconfig) is
  provided. In short: 4-space indentation for C#, `nullable` enabled, implicit
  usings, and file-scoped namespaces.
- Keep changes focused and match the surrounding code's density and naming.
- Prefer `Environment.GetFolderPath(...)` and relative paths over hard-coded
  absolute paths; never commit machine-specific or personal paths.
- Do not add build artifacts (`bin/`, `obj/`, `publish/`, `*.pdb`) or IDE files
  to commits — they are covered by [`.gitignore`](.gitignore).

## Project boundaries (please preserve)

- **Keep OCR isolated.** The main application must not reference the OCR project
  or any inference package; all OCR access goes through the sidecar process
  (`OcrSidecarService`, stdin/stdout protocol). This keeps the OCR engine's
  dependencies decoupled from the main app.
- **No third-party injection / hijacking.** Do not add code that injects into,
  hijacks, or otherwise modifies the runtime of third-party software (e.g. DLL
  proxying, remote-debugging attach, in-process hooks). Integrate only through
  documented, supported public APIs.

## Submitting changes

1. Create a topic branch from `main`.
2. Make your change with clear, self-contained commits. Reference any related
   issue in the description.
3. Ensure `dotnet build` and `dotnet test` pass, and that any file you modified
   carries a note of the change where appropriate.
4. Open a pull request describing **what** changed and **why**.

### Developer Certificate of Origin (sign-off)

By contributing, you certify that you wrote the change or otherwise have the
right to submit it under the project's license. Please sign off your commits:

```
git commit -s -m "your message"
```

## License of contributions

BeeX DeskNest is licensed under the **Apache License 2.0**. Unless you state
otherwise, any contribution you intentionally submit for inclusion in the work
is licensed under the same terms, per Section 5 of the Apache License 2.0. Do
not contribute code whose license is incompatible with Apache-2.0.
