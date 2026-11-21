# Writing for Berwick Workshop

This guide covers the text people read on the repository, in the editor and on release pages. The aim is to make it easy to tell what Berwick Workshop can do, how a feature works and which parts have been tested. The technical notes should be just as useful to someone returning to the code a year later.

## Where information belongs

| Location | Reader and purpose |
| --- | --- |
| `README.md` | Someone finding or installing the editor. Explain the supported game editions, how to obtain and open the program, what the main editors do and where to learn more. |
| Application Help | Someone using a specific control. Give the actual steps, constraints and consequences of the action shown on screen. |
| `docs/DEVELOPMENT.md` | Someone changing the source. Explain module boundaries, project and branch conventions, test commands and technical constraints. |
| `docs/RESEARCH.md` | Someone checking game formats. Keep offsets, sample values, observations, hypotheses, corrections and the evidence behind them. |
| `docs/ROADMAP.md` | Someone checking which features exist, which are being investigated and which are still planned. |
| `docs/RELEASING.md` | Someone preparing a version. Document the checks and packaging procedure, not a list of advertised features. |
| `docs/RELEASE_HISTORY.md` and GitHub Releases | Someone deciding whether to install a version. Describe the features that shipped with that version. |
| Issues | Someone investigating a problem or implementing a specific change. Keep the relevant behavior, known facts and outstanding questions together. |
| Pull requests | Someone reviewing code. Explain the problem, what changed, how it was checked and what is still uncertain. |

A reader should not need to understand the project's internal research or development process to download and use the editor. Conversely, the README should not become the only place where implementation details are recorded.

## README

The first screen should answer what the editor is for, which version of Berwick Saga it supports and where to get it. Then show how the program is used, beginning with opening an ISO and saving a project. Describe the main capabilities by the work a user can do, not as a catalogue of internal modules.

For example, a person looking for map editing needs to know whether Maps displays starting positions, whether those positions can be changed and how its limits differ from the Events editor. They do not need the archive number or event opcode in that introduction. That belongs in the research notes or the relevant issue.

Keep build instructions near the bottom or link to the development guide. Give exact commands, required versions and file safety details. Avoid a banner of badges or a gallery of screenshots unless the images add useful information and their source is known.

## Help text

Help should match the current interface. Use the actual button and page labels, and say when a change affects a whole class, a shared army record or another battle. When describing a command, explain what the user selects and what happens after applying it. A limitation that no longer exists should be removed rather than left beside newer instructions.

Some Help entries are already very long. When extracting them from `MainWindow.xaml.cs`, keep the original technical detail but separate topics and shorten repeated explanations. Compare the revised text against the controls in the current build.

## Issues and pull requests

Use the problem as the opening when there is a specific failure. A good issue identifies where it happens and what is known. It then gives enough detail for another developer to reproduce the failure or continue the investigation.

For the computed event calls in #28, the essential facts are the calling events, the variable values that remain unknown, and the risk of changing shared placements without knowing the possible targets. The hexadecimal offsets belong there because they distinguish the calls. A lengthy introduction about the editor's general goals does not.

A pull request describes the change actually present in the diff. Mention the relevant issue once. Give test commands or results where they affect review, but distinguish those from work still outstanding. A readable description does not have to follow a fixed heading order.

For example:

> The event reader treated the first `evdata.dat` entry as a battle record and rejected both retail editions. The game uses a separate packed type table, so the reader now checks the complete 4,096 slot catalogue before collecting battle references. Synthetic tests and read only checks against both originals pass. Placement itself remains unavailable until the computed calls in events 133 and 151 are understood.

This is a code review explanation, not a release announcement. It records the failure, the repair and the remaining limitation.

Use short, descriptive titles such as `Read typed event catalogue records` or `Keep unit selection when redrawing Maps`. There is no need to enumerate every routine changed in a title.

## Release notes

Start with what the installed version lets people do that the preceding version did not. Provide the download and installation instructions, then explain the most useful changes. Technical details matter when they affect files a user can open or the behavior they will see. Pure refactors, internal test numbers and speculative future work do not need to appear in user release notes.

A release description can be two or three paragraphs for a small release. A longer version can group changes under a few meaningful headings. Lists are useful when readers are comparing several distinct changes, but they do not need to be the shape of every announcement.

Preserve the limits of a feature. For example, the 0.118.0 animation exporter writes transparent source frames and effect layers; it does not reproduce the game's final runtime composite. Describe that distinction near the export feature.

The repository's older release descriptions should be revised carefully without changing the tag, attached download, release date or supported feature claims. See [Release history](RELEASE_HISTORY.md) for edited descriptions of 0.114.0 and 0.118.0.

## Language and presentation

Use ordinary nouns and verbs. Prefer `Maps shows the starting positions` to `The Maps module provides comprehensive starting position visualization`. Use the game's real vocabulary, including `army`, `group`, `slot`, `battle` and `event`, where those distinctions matter.

Do not turn every paragraph into three evenly balanced claims. Remove sentences that only tell readers the documentation is thorough, the implementation is conventional or the review was careful. Describe the relevant result instead. Vary paragraph length according to the information, not a predetermined layout.

Keep Markdown simple. Headings separate genuinely different topics, tables compare related facts and lists help with procedures or distinct options. Do not use long dashes as sentence punctuation. A hyphen is still appropriate in a compound term, a command option, a filename or an identifier. Do not alter commands or game data names to satisfy prose preferences.

Use links with meaningful labels. Prefer relative paths for other files in this repository. Avoid repeating the same issue link in every paragraph.

## Evidence and accuracy

Different checks establish different things. Keep these distinctions clear when documenting a feature:

| Evidence | What it establishes |
| --- | --- |
| Game data and executable inspection | The observed format, instruction sequence or stored values on the inspected edition |
| Tests with constructed data | The application's behavior for the cases represented by the fixtures |
| Desktop interaction | The actual controls, selection and display behavior in a tested build |
| Export and reopen | The output image contains the expected edits and preserves the checked data |
| PCSX2 gameplay | The game behaves as observed after loading that edited image in the tested scenario |

State which edition was checked, particularly when the distinction matters. A build that passes does not prove an edit works in a running game. A successful PCSX2 test on one route or battle does not establish that every other use of a shared event is safe. Unknowns can remain unknown in public text.

Do not invent contributor names, test results, artwork origins or licenses. Keep established credits. The game artwork comes from a user's local disc and should not be placed in source control or release archives. Any extra art distributed with the editor requires traceable permission. The source and reuse rights for `App/workshop.ico` remain unverified.

## Editorial review

Before publishing or changing user text, open the affected page and check the names of its controls. Compare claims about supported features with the current build or source, and distinguish planned work from working features. For release notes, verify the claimed changes against the tagged version rather than current `develop`.

Read the description once without headings or formatting. If two paragraphs do the same job, combine them. If a sentence can be deleted without losing information, delete it. Preserve any technical details necessary to reproduce a bug, understand a restriction or maintain compatibility.

## Reference projects

These repositories informed the division between user information, development procedures and release notes. They are examples to compare rather than templates to reproduce.

| Project | Useful reference |
| --- | --- |
| [Visual Studio Code README](https://github.com/microsoft/vscode/blob/main/README.md) | Public introduction, downloads and links to separate contribution guidance |
| [Godot README](https://github.com/godotengine/godot/blob/master/README.md) and [contribution guide](https://github.com/godotengine/godot/blob/master/CONTRIBUTING.md) | Separation of product documentation from development process; focused pull requests and meaningful commit titles |
| [OBS Studio README](https://github.com/obsproject/obs-studio/blob/master/README.rst) | Short product description leading to detailed support, build and developer resources |
| [PCSX2 README](https://github.com/PCSX2/pcsx2/blob/master/README.md) | Plain explanation of supported platforms, prerequisites and download location |
| [Dolphin README](https://github.com/dolphin-emu/dolphin/blob/master/Readme.md) | Practical platform requirements and separate operating system build instructions |
| [x64dbg README](https://github.com/x64dbg/x64dbg/blob/development/README.md) | Windows tooling, download and contribution references |
| [GitHub Desktop release note guide](https://github.com/desktop/desktop/blob/development/docs/process/writing-release-notes.md) | Release descriptions based on visible changes for users, rather than a raw change list |
| [GitHub README guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes) | What a repository landing page should help a visitor find |

The goal is not to make Berwick Workshop look exactly like any of these projects. It is to make the documentation accurate, useful and easy to keep current.
