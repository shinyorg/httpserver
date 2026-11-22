# Shiny.Net.HttpServer — Working Notes

Guidance for maintaining this repo. Code lives in `src/`, tests in `tests/`, samples in `samples/`,
the published Claude Code skill in `skills/`, and the public documentation site in a **separate**
repo at `~/Desktop/dev/documentation` (rendered to https://shinylib.net/httpserver).

This is a dependency-light, AOT/trim-clean HTTP/1.1 & HTTP/2 server that runs anywhere .NET runs,
including .NET MAUI, where ASP.NET Core cannot. The core is `Shiny.Net.HttpServer`; the source
generator, JWT, Azure Relay, SSH and MCP pieces are separate packages under `src/`.

## After every new feature or fix

A change is not "done" until the four artifacts below are in sync. Do all of them in the same
change unless there's a reason not to.

1. **Code + tests** (`src/`, `tests/`)
   - New behavior that lives on the core server should work across every transport and protocol
     version it plausibly touches (HTTP/1.1, HTTP/2, WebSockets, and each `ITunnelProvider`) — or be
     explicitly scoped away from them, with the scope stated in the release note.
   - The trim/AOT analyzers are on for every shipping project. A change that introduces reflection or
     an unannotated dynamic dependency is a regression, not a warning to suppress.

2. **Documentation site** (`~/Desktop/dev/documentation/src/content/docs/httpserver/`)
   - Update the relevant feature page.
   - Add a **release note** — see the release-note rules below.
   - Pages are `.mdx`; release notes use the `<RN>` component
     (`import RN from '/src/components/ReleaseNote.astro'`), with `type="feature|enhancement|fix|breaking"`.
   - A brand new page also needs a sidebar entry in `src/sidebar-topics.mjs` in that repo (the httpserver
     sidebar lives there, not in `astro.config.mjs`), and a new package needs a row in the package
     table on `httpserver/index.mdx`.

3. **Skill** (`skills/shiny-httpserver/SKILL.md`)
   - This is the source of the published `shiny-httpserver` Claude Code skill — the agent-facing
     "how to generate correct code" doc.
   - Keep `SKILL.md` aligned with the code. Update the `triggers:` keyword list near the top when a
     new public type / attribute / package is introduced.
   - If the default or recommended pattern changes, the skill's default guidance must change too.
     The four tiers (raw delegate → routing → middleware → generated typed endpoints) are the spine
     of the skill; a new API belongs to one of them, so say which.

4. **readme.md** (repo root)
   - This file is packed into the NuGet package (`PackageReadmeFile` in `Directory.Build.props`).
     Update the feature list, the package table, and any inline guidance when behavior changes.

## Release notes

Release notes live in the documentation repo at
`~/Desktop/dev/documentation/src/content/docs/httpserver/release-notes.mdx`.

**Which version does a note go against?** Releases come from git tags on `main` (`v2.3.0`, or
`v2.3.0-beta.1` for a prerelease; a tag on a commit that is not on `main` fails the release): `.github/workflows/release.yml` builds, tests and publishes at the tag's version,
and there is nothing to bump in the repo (local and CI builds are `0.0.0-dev`). So a note goes against
the **next version to be tagged** — the version of the `## <version> TBD` section at the top of the
release notes if one exists; otherwise the next version after the newest tag (`git tag --sort=-v:refname`), or
after the newest dated section in the release notes when that is newer (releases before 2.3 were
versioned by Nerdbank.GitVersioning and never tagged), minor for a feature, patch for a fix-only release. Never put a note under a dated section that has
already shipped.

**Heading style — match the existing file.** Feature/minor releases are headed by `major.minor`
(`## 1.1 - June 13, 2026`); patch releases use the full `major.minor.patch` (`## 1.0.2 - May 30,
2026`). Pick the heading that matches the kind of release you're cutting.

**If the version isn't released yet (beta / prerelease, or work-in-progress for the next version):**
- If a `## <version> TBD` heading already exists, **add the note under that existing section**. If
  you're modifying a feature that hasn't shipped yet (already an entry under a `TBD` section), edit
  that existing entry in place rather than adding a duplicate.
- If no section exists for that version yet, **create a new `## <version> TBD` heading** at the top
  and add the note there.

**When cutting a release**, promote the `TBD` section to a dated one (`## 2.3 - November 21, 2026`)
in the same change that precedes the tag.

Each note is a single `<RN>` line. Use `type="breaking"` for breaking changes (it's its own note
type here, not a flag). Newest version section stays at the top of the file.
