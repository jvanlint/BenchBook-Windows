# Start here: building BenchBook for Windows

This is the brief for whoever builds the Windows app, including a Claude Code
session on a Windows PC. Copy it into the Windows project as `CLAUDE.md`, and
keep it up to date there.

## What BenchBook is

A private reference library for scale modellers. Each build is a project, and
the user saves photos, PDFs (kit instructions), links, videos, highlighted text
and notes to it, then uses them at the bench. It also tracks paints (with a
shopping list) and aftermarket parts. Everything stays on the user's computer.

BenchBook for Mac 1.0 is released. Its code is public at
https://github.com/jvanlint/BenchBook. The Windows app is a separate, native app
that reads and writes the same backups.

## Read these first, in order

1. `..\BenchBook\spec\README.md`: what the spec covers, and the terms it uses.
2. `..\BenchBook\spec\windows-app.md`: the plan: technology, screens, phases, open decisions.
3. `..\BenchBook\spec\library-format.md`: the backup format. It's the contract with the Mac
   app, so follow it exactly.
4. `..\BenchBook\spec\behaviour.md`: the rules. The ones marked **(stored)** must give
   exactly the same results as the Mac.
5. `..\BenchBook\spec\screens.md` and `..\BenchBook\spec\screens\`: every screen and its wording.

If the spec doesn't answer a question, read the Swift code it points to. If
the code and the spec disagree, the code is right: say so, so the spec gets
fixed. Don't guess.

## Before writing Windows code

Phase 1 happens on the Mac: the Mac app writes and reads format 2 backups,
and a sample backup is added under `..\BenchBook\spec\fixtures\`. Check that it's done
before building backup or restore, because the Windows tests rely on that
sample.

Then decide the open questions in `windows-app.md` with the user, starting with
the PDF library and the repository.

## Decisions

Decided with the user on 2026-11-13, for the open questions in
`..\BenchBook\spec\windows-app.md`:

1. **PDF library**: PDFium's native builds from `bblanchon.PDFium.Win32`
   (x64, Arm64 and x86 DLLs; Apache-2.0 packaging, BSD PDFium), with
   BenchBook's own small P/Invoke layer for the functions it needs. No other
   wrapper. Agreed with the user on 2026-11-13.
2. **Markdown**: Markdig (BSD-2) parses it, and BenchBook draws it with
   WinUI's own RichTextBlock. The Toolkit Markdown control isn't on nuget.org
   for Windows App SDK 2.x, only Labs. Agreed with the user on 2026-11-13.
3. **Mac ZIP writing**: the Mac app gets its own small ZIP reader and writer
   on the Compression framework (option B), not `ditto`. That's Mac work in
   phase 1, and the Mac code is read-only from here. The Windows app uses
   `System.IO.Compression` and writes only what `library-format.md` allows:
   Deflate or Stored entries, ZIP64 only when needed, `/` and UTF-8 names, and
   no extra entries. Write to a seekable file so .NET doesn't add data
   descriptors. If a ZIP from the Mac doesn't open on Windows, or the other
   way round, report it to the user. Don't work around it.
4. **Distribution**: direct download only, from GitHub Releases with an
   `.appinstaller` file. No Store listing. Signing (such as Azure Trusted
   Signing) is set up by the user.
5. **Repository**: this separate repo, public at
   https://github.com/jvanlint/BenchBook-Windows. `gh` is the GitHub CLI.
6. **Demand**: not being gauged.
7. **Library location**: the real `%LOCALAPPDATA%\BenchBook`, as the spec
   proposes. MSIX would otherwise redirect AppData writes into a private
   package folder that's deleted on uninstall. `Package.appxmanifest` declares
   the `unvirtualizedResources` restricted capability, with
   `virtualization:ExcludedDirectories` (Windows 11) and
   `desktop6:FileSystemWriteVirtualization` disabled (Windows 10, which ignores
   the exclusion). Fine for a direct download; the Store would question it.

Never change anything in the `..\BenchBook` folder. Read it only. If the spec
there is wrong or out of date, tell the user.

## The project

```text
BenchBook.slnx
src\BenchBook.Core\         no UI: records, store, backup and restore, clean-up, settings (net10.0)
src\BenchBook.Pdf\          PDFium bindings, PDF opening, rendering, Find and marked-up export (net10.0)
src\BenchBook.App\          the WinUI 3 app, packaged as MSIX (x64 and Arm64)
tests\BenchBook.Core.Tests\ xUnit v3 tests, each with its own temporary library
tests\BenchBook.Pdf.Tests\  PDF tests, on PDFs made with PDFium itself
```

- .NET 10 (the LTS), C# 14, Windows App SDK 2.6.4 (self-contained in the
  package), Microsoft.Data.Sqlite 10.0.14, CommunityToolkit.Mvvm 8.4.2, xUnit
  v3 4.0.2. Pick package versions at least two weeks old.
- Build: `dotnet build src\BenchBook.App -p:Platform=x64` (or `ARM64`).
- Test: `dotnet test --project tests\BenchBook.Core.Tests` and
  `dotnet test --project tests\BenchBook.Pdf.Tests` (Microsoft Testing
  Platform, set in `global.json`).
- PDF markups are stored in the page's own PDF coordinates (points, y up),
  as PDFKit gives them on the Mac, with the crop box as the page's bounds.
- Run: open `BenchBook.slnx` in Visual Studio and start `BenchBook.App` (x64).
  Deploying a development package needs Developer Mode.
- The store's tables mirror `library.json`, with camelCase column names taken
  from the record properties (`Store\TableMap.cs`). Schema changes are new
  numbered steps in `Store\Migrations.cs`; never edit a released step.
- The app icons in `Assets\` are made from the Mac icon
  (`..\BenchBook\BenchBook\Assets.xcassets\AppIcon.appiconset\BenchBook-1024.png`)
  at each scale and target size, with its square drawn with rounded corners
  (18% of the side), and `BenchBook.ico` for the windows and the `.exe`. A
  changed manifest needs a higher package version before
  `Add-AppxPackage -Register` accepts it.
- Sharing: choosing BenchBook in the Windows share sheet starts a second
  process that shows only `Share\ShareWindow`. It never opens the store or
  finishes a restore. It reads `Inbox\Projects.json` (published by the running
  app) and commits an envelope folder to `%LOCALAPPDATA%\BenchBook\Inbox\`, as
  the Mac's `CaptureEnvelope` does. The running app (`Share\ShareInbox`) imports
  envelopes at launch and as they arrive.

## Catching up with the Mac

`..\BenchBook\spec\changes.md` lists what the Mac has gained since 1.0. Note
here what's been brought across (the spec asks for it to be noted in the
Windows project, not there). As of Mac 1.3.0 (`9c68bc9`, 2026-11-24):

- **Done:** library schema 6.0.0 in the store, backups and restore: build
  photos and their tags and notes, kits, kits in projects (project
  manufacturer, scale and kit number moved into the notes), paint standards
  as a system and a code with match and type, and mixes with recipes.
  Older stores are copied to `BenchBook Before 6.0.0.db` and upgraded by
  migration step 2; older backups are upgraded as they're read
  (`Model\SchemaUpgrades.cs`). Dates round to the millisecond.
- **Done:** paint CSV with Standard, Standard Code, Match and Type columns,
  and re-import updating paints already in the catalogue (#24). The paint
  editor has Type, Standard, Standard Code and Match. The paint picker lists
  paints with the colour's standard first. The project summary comes from
  its kits. A tag only build photos use isn't unused.
- **Done:** the "last updated" rule for every edit (`Store\LibraryStore.Activity.cs`;
  link details pass `userEdit: false`), and sorting projects in the
  sidebar (Name, Date Created, Last Updated, remembered in Settings).
- **Done:** Kits (`Core\Kits\`): the Kits catalogue with its filters, hidden
  columns and CSV import (Scalemates and BenchBook) and export, the kit
  editor (the picture well is shared with the aftermarket editor,
  `Views\PictureWell.cs`), a project's Kits tab (Ctrl+1; References,
  Paint and Aftermarket move to Ctrl+2 to Ctrl+4), Add Kits to…, kits in
  the new-project sheet and New Project from Kit…, status following the
  project, needed kits and mix recipe paints on the Shopping List, and kits
  in Search.
- **Done:** mixes (`Core\Paints\PaintCatalogue.cs` works out a mix's
  ownership and recipe text): Add Mix, the mix editor's recipe with Add
  Paints to Recipe (a flyout, as a dialog can't open over the editor), and
  a mix's Owned box. The Paints screen's Match and Type columns, Type filter
  and Import ▸ menu with Import from Scalemates… (`ScalematesPaints.cs`).
  The picker's Type menu and match labels. The Paint tab's Standard menu,
  Code and Notes columns, and Add Colour with a Standard menu. Clear paints
  drawn as glass (`Views\PaintSwatch.cs`).
- **Done:** Build Photos (`Core\BuildPhotos\`, `Views\BuildPhotosView.cs`):
  the tab (Ctrl+5) grouped as Completed then by day, adding by drop, paste
  and Add Photos… (Ctrl+O there), the date taken from EXIF, stages, details,
  tags, the project photo in the header, box notes, the image viewer and
  comparison (both take a `Viewers\ViewerImage`, a reference or a build
  photo), export with location data removed in the file's own bytes
  (`Capture\PhotoMetadata.cs`: EXIF GPS and XMP GPS blanked, same size, PNG
  checksums redone), and build photos in Search, tag pages and the Tags
  screen ("Used By"). Windows leaves out Import from Photos… and Continuity
  Camera; Space opens the photo in its default app, as there's no Quick Look.
- **Not yet:** project export and import
  (`.benchbookproject`), the safety changes to link previews and restore
  (#15), one link per project (#7), and PDF markup badges kept on the page
  (#39).

## Releasing

Decided with the user on 2026-11-14: releases are direct downloads from GitHub
Releases. There's no Store, and for now no certificate. The user is an
individual in Australia, so Azure Artifact Signing isn't available; signing
would need a paid OV certificate with cloud signing.

- Releases are unpackaged, unsigned zips, one for x64 and one for Arm64, each a
  `BenchBook` folder with `BenchBook.exe`, plus `SHA256SUMS.txt`. Windows warns
  the first time the app runs. Without a package there's no share target and
  no automatic updates.
- `build\Release.ps1 -Version 0.2.0` builds them into `artifacts\`, the same on
  a PC as on GitHub. It publishes with `-p:BenchBookUnpackaged=true` and stamps
  the `.exe` as `0.2.0.<commit count>`. Settings shows "0.2.0 (123)".
- `.github\workflows\release.yml` runs on a pushed `v*.*.*` tag: it runs the
  tests, builds the zips and makes a **draft** release, marked as a
  pre-release for 0.x. The user publishes it. Run by hand, it keeps the zips as
  a workflow artifact instead.
- Versions stay 0.x until Phase 1 lets the two apps share backups, then follow
  the Mac's 1.x.y.
- Day-to-day builds stay MSIX (`Package.appxmanifest`, development package).

## Working rules

These are the user's rules for the Mac app, and they apply here too.

**Git**

- One branch per issue or feature, named `issue-N-short-name`.
- Commit or merge only when asked. "Commit this" means commit on the branch.
  "Commit to main" means: commit on the branch, `git merge --ff-only` into
  main, delete the branch, and push main. No pull requests.
- Commit messages say what changed for the user, and end with "Fixes #N" when
  there's an issue.

**Code**

- Native Windows controls, system fonts, system colours and Fluent icons.
  Nothing custom-drawn where a standard control exists.
- No new dependency (NuGet package, native library or tool) without
  discussing it with the user first, including why the built-in option isn't
  enough.
- Don't add sample records to the user's library. Tests use their own temporary
  libraries.
- Never delete or recreate the user's store or files to recover from an error.
  Report the error and leave the data as it is.
- Keep the user's original files byte-for-byte.
- Write in British English ("colour"), in short, plain sentences, as the Mac
  app does.

**Honesty**

- When a check can't be run, such as a build, a test or trying something on a
  real device, say so plainly. Don't report it as done.
- Report test failures as they are.

**Privacy and the web**

- No telemetry, analytics, accounts or cloud services.
- Web requests only fetch link details, as `behaviour.md` describes. The
  User-Agent stays the honest `BenchBook (link preview)`. Never get around a
  site's bot checks.
- Don't send the user's email address or other personal details to any
  service.
- Signing certificates and Store credentials are handled by the user only.
  Never ask for them, store them, or put them on a command line.

**Testing with the user**

- Run builds and unit tests yourself.
- The user tries the UI themselves. Don't drive the app with synthetic clicks
  or keystrokes.
- Screenshots: capture only BenchBook's own window, never the whole screen.

## When the Mac app changes

The Mac app is the reference. If a change on the Mac affects the format or the
rules, its commit updates `..\BenchBook\spec\`. Check `git -C ..\BenchBook log -- spec/` for
changes since the last time you looked.
