Parallel-marking Mono runtime for Oxygen Not Included
======================================================

This package replaces one file in the game:

    <game>\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll

It is Unity's Mono runtime, built from the same source commit as the game's own, with the
garbage collector modified to mark on several threads. That shortens the game's periodic
garbage-collection freeze. The changes and the licence notices are in NOTICE.txt and licenses\.

Before installing: compare the SHA-256 of mono-2.0-bdwgc.dll with the one on the release page
(PowerShell: Get-FileHash .\mono-2.0-bdwgc.dll -Algorithm SHA256). If they differ, do not install.

Installing: close the game, double-click install.cmd. It finds the game through Steam (or run
.\install.ps1 -GamePath "<folder holding OxygenNotIncluded.exe>"), proceeds only over a runtime
listed in supported-runtimes.txt, keeps the game's own runtime as mono-2.0-bdwgc.dll.vanilla
(checked), and puts the replacement in place (checked). If Windows refuses to write, right-click
install.cmd and choose "Run as administrator".

Uninstalling: close the game, double-click uninstall.cmd. Steam's "Verify integrity of game
files" also restores the game's own runtime, and so does a game update.

Other options:
    .\install.ps1 -Status      show what is installed, change nothing
    .\install.ps1 -Force       install over an unlisted runtime (not recommended)
