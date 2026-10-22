# Build-time Help Documents Implementation Plan

> **For agentic workers:** Execute inline with superpowers:executing-plans. User authorized direct implementation; no further design approval is needed.

**Goal:** Preserve offline help while excluding Markdig and its notice from desktop distributions.
**Architecture:** A build-only console compiler converts the allowlisted Markdown files into an embedded JSON document model. The native Avalonia reader consumes this model. Model source is shared with the tool; the tool has no runtime reference from Desktop.
**Tech Stack:** .NET 10, MSBuild, System.Text.Json, build-only Markdig 1.4.0, Avalonia 12.
**Spec:** `docs/superpowers/specs/2026-10-22-build-time-help-documents-design.md`

## Global Constraints

- Keep the thirteen packaged documents, their source paths, existing rendering, link policy, search and copy behavior.
- No WebView; no runtime Markdown parser; no release or push in this task.
- Generation must run automatically during a normal desktop build and fail on invalid inputs.
- The converter runs on the build host, including when publishing for a different runtime.

## Review Focus

- Clean/locked restore and cross-runtime publish must build the converter for the host.
- Editing a document must regenerate the payload; missing sources must fail the build.
- Nested formatting, hard breaks, tables and repeated heading anchors must survive conversion.
- Test-only parser references must not flow into the desktop publish graph.
- Existing output folders must not cause an obsolete notice or assembly to be shipped.

### Task 1: Compiler and document contract

Files: `src/BadmintonDraw.Desktop/Help/HelpDocumentModel.cs`, `Help/manifest.json`, `tools/BadmintonDraw.HelpCompiler/*`, `tests/BadmintonDraw.Desktop.Tests/CompiledHelpTests.cs`.
Interfaces: compiler produces versioned `CompiledHelpLibrary` containing `HelpDocument` metadata, Markdown, plain text and `HelpBlock`/`HelpInline` trees; shared model source has no external dependency.

- [x] Add a failing test proving the desktop currently references Markdig.
- [x] Implement the converter and deterministic JSON serialization; test headings, tables, inline styles, links, hard breaks, missing sources, and invalid payload versions.

### Task 2: Build integration and native reader

Files: Desktop project, catalog, reader, help window, solution and lock files, existing help tests.
Interfaces: `HelpMarkdownView.SetDocument(IReadOnlyList<HelpBlock>, double)` consumes generated blocks; catalog loads the embedded library.

- [x] Wire host-tool build and generation before resource compilation; keep the compiler out of desktop runtime dependencies.
- [x] Adapt native rendering and existing test fixtures to the owned model, preserving selection code.
- [x] Remove runtime Markdig and published notice; run desktop help tests.

### Task 3: Artifact checks and verification

Files: packaging verification script, CI, macOS packaging script, architecture/build docs.

- [x] Add an executable publish check covering forbidden dependencies and notice files, including single-file bundles.
- [x] Run full solution tests, locked restore, incremental generation checks and Windows/macOS publish smoke tests in new output folders.
- [x] Review final diff and dependency/artifact results; report concrete results and any unverified platform behavior.

## Execution notes

- Reuse the user's clean working directory so their normal run command sees the change. Do not create another project copy.
- Test-only compiler source and Markdig can be included in the desktop test project to preserve the existing Markdown-driven UI regression cases without adding a parser to product code.

## Verification record — 2026-10-22

- Locked solution restore passed. After cleaning the desktop project, Release solution build passed with zero warnings and errors.
- Full solution tests passed: Desktop 841, Core/workflows 1021 (1862 total). The 54 help tests also passed after final integration checks.
- Python checks passed: 7 help publish tests and 11 packaging preflight tests.
- A normal repeat build preserved the generated payload timestamp and bytes when source content was unchanged. Embedded document/source equality tests passed after editing architecture/build documentation.
- Temporarily pointing the manifest at a nonexistent source caused a normal desktop build to fail with that source path. The manifest was restored and a normal solution build passed again.
- Independent review identified stale files in reused publish directories as a gap. Added an after-publish directory check and the Windows local guard command. An actual Windows publish seeded with an obsolete `ThirdPartyNotices/Markdig.txt` failed as expected and retained the fixture; a clean publish passed.
- Windows compressed single-file publish, Intel macOS publish, and Apple Silicon macOS packaging passed their help dependency checks. Markdig runtime, compiler, and notice were absent from those outputs.
- Apple Silicon application signature and DMG checksum verification passed. Local macOS help was opened; native display, body search, and document navigation worked. Windows and Intel macOS GUI execution were not performed.
- Latest local DMG: `artifacts/macos/osx-arm64/5.0.2/run-48f5c3d43aba4632bebd00798a6084f5/SZU-Badminton-Draw_5.0.2_osx-arm64.dmg`.
- Source work remains local and uncommitted; no push or GitHub Release was performed. Build-tool licensing remains under `tools/BadmintonDraw.HelpCompiler/ThirdPartyNotices/`.
