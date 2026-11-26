xdelta3 in the Windows package
==============================

The Windows release package includes tools\xdelta3.exe, the official xdelta3 3.2.2
program for Windows x64. Berwick Workshop runs it as a separate program to make and
apply ISO patches. It is not linked into Berwick Workshop, and the executable is not
stored in this repository.

The exact program
-----------------
Release:        xdelta3 3.2.2, https://github.com/jmacd/xdelta/releases/tag/v3.2.2
Download:       xdelta3-3.2.2-windows-x86_64.zip
                SHA-256 d48f7651d86b78fdca397057b46601f89131f37219675df736db70af44ec3379
Program:        xdelta3.exe from that zip, SHA-256 in xdelta3.exe.sha256
Built by:       the project's "Release Xdelta 3" workflow, run 37879746185, from tag
                commit 5f8f5a553181c139d91b0289c009e5c276c5318c
Build options:  cmake -DCMAKE_BUILD_TYPE=Release -DXD3_LZMA_FETCH=ON
                -DXD3_BUILD_TESTS=OFF -A x64
Included:       liblzma from xz 5.8.3 (commit 4b73f2ec19a99ef465282fbce633e8deb33691b3)
                and BLAKE3 1.8.5 (commit 93a431c78a52d7ccf0f366f106467f5070e6075e),
                both linked statically

Berwick Workshop accepts only a program with that SHA-256. It checks the hash when the
ISO patches window opens and again, through a locked handle, each time it runs the
program. It never downloads anything.

Licences
--------
xdelta3, copyright 2016 Joshua MacDonald
    Apache License 2.0: xdelta3-LICENSE.txt
liblzma (XZ Utils), copyright the XZ Utils authors and contributors
    BSD Zero Clause License (0BSD): liblzma-COPYING.0BSD.txt
BLAKE3, by Jack O'Connor and Samuel Neves
    Apache License 2.0, chosen from the project's CC0-1.0, Apache-2.0 and
    Apache-2.0 with LLVM exception options: BLAKE3-LICENSE_A2.txt

The licence texts are copied unchanged from the xdelta3 3.2.2 source archive and the
pinned xz and BLAKE3 commits. xdelta3 has no NOTICE file. The package's Licenses.txt
contains all three texts.

Source
------
xdelta3: https://github.com/jmacd/xdelta/archive/refs/tags/v3.2.2.tar.gz
xz:      https://github.com/tukaani-project/xz/tree/4b73f2ec19a99ef465282fbce633e8deb33691b3
BLAKE3:  https://github.com/BLAKE3-team/BLAKE3/tree/93a431c78a52d7ccf0f366f106467f5070e6075e

Updating
--------
To take a newer xdelta3 release, for example one built with a later xz, change the
hash in xdelta3.exe.sha256 and in XdeltaTool.Release322WindowsX64Sha256, update this
file and the licence texts, and run the xdelta tests and the release checker again.
