# FFACiv Recorder

A small Windows program that records your Civilization V multiplayer games so they can be watched as replays on
[ffaciv.com](https://ffaciv.com). It copies Civ's autosaves after each turn and uploads them when you choose. If you
turn it on, it can also note unit moves and fights between saves for smoother replays.

This repository is the recorder's full source code. It's public so you can see exactly what the program you download
does.

## Download

Get the installer from the [Releases page](https://github.com/Viktor819231/ffaciv-recorder/releases/latest): `FFACiv-Recorder-Setup-<version>.exe`.
It installs for your Windows user only and needs no administrator rights. It needs Windows 10/11 64-bit and the
Microsoft Edge WebView2 Runtime, which most PCs already have.

The installer has no code signature, so Windows SmartScreen may warn about an unknown publisher. Choose
**More info → Run anyway** only if the file came from this Releases page or from ffaciv.com.

### Check that the download is this code

GitHub builds every release from this repository by itself; see
[`.github/workflows/release.yml`](.github/workflows/release.yml). Nobody builds it on a private PC. Each release lists
the installer's SHA-256 checksum, and GitHub keeps a signed build record that ties the file to the exact commit.

* **Checksum:** in PowerShell, `Get-FileHash .\FFACiv-Recorder-Setup-<version>.exe`. The result must equal the
  SHA-256 on the release page.
* **Build record:** with the [GitHub CLI](https://cli.github.com/), run
  `gh attestation verify FFACiv-Recorder-Setup-<version>.exe --repo Viktor819231/ffaciv-recorder`.
  It names the workflow run and the commit the file came from.

## What it does, and what it doesn't

* **Saves.** It watches Civ's autosave folder and copies each finished save into its own folder. It never changes or
  deletes Civ's own saves.
* **Civ's settings file.** It's the only Civ file the recorder can change, and only when you click **Fix it for me**
  with Civ closed. That sets Civ to autosave every turn and keep enough saves. It changes just those two numbers and
  backs up the old file first ([`AutosaveCheck.cs`](OutsideRecorder/OutsideRecorder.Shell/AutosaveCheck.cs)).
* **Reading the game (optional, off until you turn it on).** "Experimental action logs" look at Civ's memory up to
  20 times a second to see unit moves, fights and city changes. The recorder opens Civ with **read-only** access
  (`PROCESS_VM_READ`, see [`ProcessMemory.cs`](OutsideRecorder/OutsideRecorder.Core/ProcessMemory.cs)). There is no
  code that writes into the game, injects anything or changes how the game plays. The memory addresses it reads are in
  [`Layouts.cs`](OutsideRecorder/OutsideRecorder.Core/Layouts.cs). [`layouts/`](OutsideRecorder/layouts) records how
  each address was confirmed from the game's DLL. This works on Civ V Brave New World 1.0.3.279, LEKMOD v35.4.003 and
  Snowve 8.1.1–8.3.0. Other versions record saves only.
* **Uploads.** Saves and logs go to ffaciv.com, the only server in the settings. With automatic uploads on, a finished game is sent once you're
  back in Civ's main menu or have closed Civ. The replay then stays hidden for 2 hours before it goes public. You
  can keep any game on your PC instead, delete it, or make it public sooner.
* **Not recorded:** single-player games (unless you turn that on) and KEK Mod games, which the KEK league collects
  itself.
* **It stays out of Civ's way.** It runs at below-normal priority, slows down when the PC is busy and pauses work
  during turn changes.

[`PRIVACY.txt`](OutsideRecorder/release/PRIVACY.txt) lists everything that can leave your PC.
[`READ-ME.txt`](OutsideRecorder/release/READ-ME.txt) is the player guide that comes with the installer.

## Where things are

| Folder | What's in it |
|---|---|
| [`OutsideRecorder/OutsideRecorder.Core`](OutsideRecorder/OutsideRecorder.Core) | Watching saves, reading the game, recordings, uploads |
| [`OutsideRecorder/OutsideRecorder.Shell`](OutsideRecorder/OutsideRecorder.Shell) | The program: window, tray icon, notifications, settings |
| [`OutsideRecorder/ui`](OutsideRecorder/ui) | The window's pages (HTML/CSS/JS, shown in WebView2) |
| [`OutsideRecorder/installer`](OutsideRecorder/installer) | Installer script (Inno Setup) and the checks every build must pass |
| [`OutsideRecorder/layouts`](OutsideRecorder/layouts) | Evidence for each memory address, per game version |
| [`OutsideRecorder/tools`](OutsideRecorder/tools) | Developer tools that find and check those addresses. They aren't in the installer. |
| [`OutsideRecorder/OutsideRecorder.Tests`](OutsideRecorder/OutsideRecorder.Tests) | Tests |

The installer is built with `RecorderPublicRelease=true`, which leaves the developer tools (memory dumps, network
tests) out. [`Verify-PlayerBuild.cs`](OutsideRecorder/installer/Verify-PlayerBuild.cs) fails the build if any of them
slip in.

## Build it yourself

You need Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git for Windows (for Git Bash).

```bash
dotnet test OutsideRecorder/OutsideRecorder.Tests       # some tests use the developer's own recordings and skip without them
node --test OutsideRecorder/tests/stream-controls.test.mjs   # the window's pages (needs Node.js)
OutsideRecorder/installer/build-installer.sh            # installer + checksums in out/or04/
```

Your own build works the same, but its bytes won't match the release exactly, because builds aren't bit-for-bit
reproducible. That's why releases are built by GitHub and come with a build record.

## License

[MIT](LICENSE). Civilization V is a game by Firaxis Games and 2K. This project isn't affiliated with or endorsed by
them. LEKMOD and Snowve belong to their authors.
