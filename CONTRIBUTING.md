# Contributing to Berwick Workshop

Berwick Workshop edits *Berwick Saga* game data for the Japanese retail disc and the English v2.3 translation. Contributions are welcome, particularly fixes to game format readers, editor behavior, test coverage and source organization.

## Before changing the code

Search the [open issues](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/issues) for related work. For a change that affects saved projects, executable patches or game data, describe the expected behavior and the editions it will affect before starting a large implementation.

Keep changes focused. Source code for game formats and editing belongs in `Core`; Windows interface work belongs in `App`. Add regression tests alongside the feature they exercise, and explain any game addresses or assumptions that are not obvious from the code.

## Testing a change

Run the offline tests before submitting a pull request:

```powershell
dotnet build Tests -c Release
dotnet Tests\bin\Release\net10.0\Berwick.Tests.dll
```

The repository also includes `tools/run-local-checks.ps1` for additional checks. If the change involves original game data, test each supported edition separately. A successful build or fixture test is not proof that an edited disc will work in PCSX2. State which checks you actually performed and what remains untested.

Open a pull request describing the problem, the code changed and the results of your checks. Keep private working notes, game images, extracted artwork, save files and local test evidence outside the public repository.

## Copyright and contributions

Contributors retain copyright in their original work. No copyright assignment to the project is required.

By submitting a contribution for inclusion in Berwick Workshop, you agree to license that contribution under the [GNU General Public License, version 3 or later](LICENSE). You must have the right to grant that license. Retain existing notices, and identify any borrowed code or other third-party materials and their licenses in the pull request.

The MinN-11 reference material keeps its separate MIT notice. Original *Berwick Saga* assets and translations are not covered by the project's GPL grant. See [Licensing](LICENSING.md) for details.
