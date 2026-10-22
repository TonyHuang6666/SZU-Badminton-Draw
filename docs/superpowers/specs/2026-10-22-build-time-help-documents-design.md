# Build-time help document compilation

## Purpose and success criteria

The desktop application must continue to display its thirteen packaged Markdown help documents offline with the existing native Avalonia reading experience: headings, tables, code, lists, links, chapter navigation, search, font sizing, text selection and copying. The published Windows and macOS applications must not distribute Markdig or its `ThirdPartyNotices/Markdig.txt` notice. Markdig may remain a build-only tool dependency because the published application will not contain its code. This change does not alter the licenses or notices required by other dependencies.

## Approach

Add a small build-only .NET document compiler that uses Markdig to parse the allowlisted Markdown files and writes a deterministic, versioned help-document payload into `obj`. The compiler is not referenced by the desktop application at runtime and is never copied into publish output. The desktop build embeds the generated payload alongside the original Markdown sources. The runtime help catalog reads that payload and exposes a project-owned document model; the existing Avalonia reader renders that model without parsing Markdown.

This is preferred to writing a new Markdown parser because it preserves the current documents' formatting and link interpretation while removing the third-party parser from the distributable. A WebView or another runtime Markdown package is outside scope.

## Build and data flow

1. Keep the explicit help allowlist and its repository-relative source paths in one authoritative build manifest, including README and the three release notes. The document compiler uses that manifest. Runtime catalog metadata comes from the generated payload so the two lists cannot drift.
2. On build, compile each Markdown file with the same Markdig options now used at runtime: pipe tables, GitHub-style heading identifiers, and disabled HTML. Convert only the document nodes and attributes actually rendered by the application into a versioned, project-owned model. Preserve visible link labels and hrefs, heading anchors, plain text, and the source Markdown text.
3. Serialize the payload deterministically with stable document order and UTF-8. The build fails if a listed source is missing, conversion encounters an unsupported visible construct, or output cannot be generated. No stale payload may be silently reused.
4. Embed the generated payload in the desktop assembly. Its runtime load validates the payload version and that all declared documents are present. The application does not read repository files, invoke the converter, or load Markdig at runtime.
5. Keep the current link allowlist/security behavior: relative links resolve only within packaged documents; only explicit HTTP(S) links may open externally; unsupported schemes remain blocked.

## Runtime changes

`HelpCatalog` supplies document metadata, source Markdown for existing search behavior, compiled plain text for the copy button, and project-owned block/inline nodes for rendering. `HelpMarkdownView` receives those nodes instead of calling `Markdown.Parse`. Its existing Avalonia layout, selection model, search highlighting, theme brushes, and link click behavior remain intact. Tests that currently inject ad-hoc Markdown into the reader will construct small model fixtures or use a packaged document; arbitrary Markdown parsing in the published app is not a supported feature.

The supported rendered constructs are the ones the current reader handles: headings with anchors, paragraphs and hard/soft breaks, emphasis, inline code, links and image labels, code blocks, tables, ordered/unordered lists, quotations, thematic breaks, and plain leaf text. If an existing or future document uses a new visible construct, the build must fail with the source path and construct name instead of dropping content.

## Verification and release boundary

- Compare all thirteen generated documents to their Markdown sources and verify deterministic regeneration.
- Preserve existing offline-help tests for category search, plain-text copy, secure navigation, anchor scrolling, tables, code, font sizing, and cross-paragraph selection in light and dark themes. Add tests for generated-payload validation and unsupported constructs.
- Run the full solution test suite and publish smoke tests for the supported release targets.
- Inspect the publish directory and packaged Windows/macOS artifacts for Markdig assemblies, dependency references, and the Markdig notice. A missing standalone notice is not by itself evidence that Markdig was removed; the dependency graph and artifact contents must also be checked.
- Update `docs/architecture.md` to describe build-time parsing and native runtime rendering.

## Scope limits

No UI redesign, help-document content rewrite, runtime editing of Markdown, or change to third-party components other than Markdig. Existing published releases remain unchanged; only a new build can omit the Markdig notice after the runtime dependency is removed and verified.
