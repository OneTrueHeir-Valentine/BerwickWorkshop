# Licensing

Berwick Workshop's original application source code is made available under the **GNU General Public License, version 3 or any later version**. The full license is in [LICENSE](LICENSE). The corresponding SPDX identifier is `GPL-3.0-or-later`.

You may copy, modify and distribute the covered code under those terms. If you distribute a modified covered program, the GPL sets conditions for licensing the distributed work and providing its corresponding source. Making changes privately does not, by itself, require publishing them.

Contributors keep the copyright to their original contributions. No copyright transfer is required; accepted contributions are licensed to recipients under the same GPL terms. Preserve existing author and copyright notices.

## Material with different rights

The MinN-11 reference material is credited under its own MIT license in [reference/MintX-LICENSE.txt](reference/MintX-LICENSE.txt). That copyright notice must remain with copies and relevant distributions.

Game images, extracted art, script contents, translations and other material belonging to the respective *Berwick Saga* rights holders are **not** relicensed by Berwick Workshop. The repository's reference data contains information obtained from the game and should not be mistaken for original project artwork or a separate grant of game-content rights.

The application emblem adapts the [Tabler Icons hexagons icon](https://github.com/tabler/tabler-icons/blob/455d76e7c5e9cc75c389ac69447580c6353b3f59/icons/outline/hexagons.svg), copyright (c) 2020-2026 Paweł Kuna, under the MIT license. The [editable artwork](App/workshop.svg) and [license](App/Tabler-LICENSE.txt) are included in the source tree.

The Windows package includes the official xdelta3 3.2.2 program (`tools\xdelta3.exe`), which Berwick Workshop runs as a separate program for ISO patches. xdelta3 is under the Apache License 2.0 and statically includes liblzma (0BSD) and BLAKE3 (used here under the Apache License 2.0). The exact program, its provenance, licence texts and source links are in `third-party/xdelta3/README.txt`.

Microsoft .NET, Windows components and any other redistributed dependencies retain their own license terms.

## Distributing a build

Each Windows package should include the complete GPL text, the retained MinN-11 MIT notice and any other notices required for bundled components, including the three xdelta3 licence texts. The release checker verifies the repository license texts in `Licenses.txt` and that `tools/xdelta3.exe` is the pinned program, but an automated check cannot establish the origin and license of every bundled asset.

The source code corresponding to a binary release must be available in the form required by the GPL, including the build files and other materials needed under its definition of Corresponding Source. Releases should identify the exact source revision used to build them. GitHub's generated source archives can assist, but do not replace a review of what the binary actually contains.
