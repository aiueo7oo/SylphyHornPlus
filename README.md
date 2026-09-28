# SylphyHornPlus

SylphyHornPlus is a virtual desktop enhancement tool for Windows 11 and 10.
It provides global hotkeys, desktop-switch notifications, per-desktop
wallpapers, mouse gestures, and tools for managing virtual desktops.

This app is a fork of SylphyHorn with better support for Windows 11.  
New features are below:

* Add support for Windows 11.
  * SylphyHornPlus is synchronized with Windows 11 virtual desktop (its name and wallpaper file settings).
  * Reordering notification.
* Can control and manage Windows virtual desktops on Settings window (Desktop tab).
  * Creating and removing a desktop, and reordering (Windows 11 only) is available.
* Can export and import settings.
  * Can also use this feature as backup of virtual desktops.
* Add Mouse shortcuts (rocker and wheel gestures) on both Windows 11 and 10.
* Can place app windows on specified desktops automatically (App placement).
  * Can create missing destination desktops and close empty desktops after use.
  * Can also rearrange windows that are already open.
* Can set the wallpaper of newly created desktops by their name or number.
* Add `sylphyhorn-cli`, a command-line tool to operate SylphyHornPlus from scripts and AI agents.


## Installation

### Install with WinGet

For a new WinGet-managed installation, run:

```powershell
winget install --id hwtnb.SylphyHornPlus --exact
```

To update an existing WinGet-managed installation, run:

```powershell
winget upgrade --id hwtnb.SylphyHornPlus --exact
```

To uninstall a WinGet-managed installation, run:

```powershell
winget uninstall --id hwtnb.SylphyHornPlus --exact
```

The `SylphyHornPlus` command alias starts the WinGet-managed application, and
the `sylphyhorn-cli` alias runs its command-line tool. WinGet removes the
managed application files and command aliases when uninstalling, but it does not remove SylphyHornPlus settings stored in your
user profile.

### Install from a ZIP

Download the ZIP that matches your Windows CPU architecture from
[Releases](https://github.com/hwtnb/SylphyHornPlus/releases):

* `SylphyHornPlus-v{version}-x86.zip` for 32-bit Windows
* `SylphyHornPlus-v{version}-x64.zip` for 64-bit Intel/AMD Windows
* `SylphyHornPlus-v{version}-arm64.zip` for ARM64 Windows

All three packages are self-contained. The .NET Desktop Runtime does not need
to be installed separately.

To use the ZIP manually, extract it to a new folder and start
`SylphyHorn/SylphyHorn.exe`. For every manual ZIP update, exit SylphyHornPlus
and replace the entire extracted application folder; do not overwrite the old
folder in place. When migrating from the previous single-ZIP distribution,
also select the package that matches your Windows CPU architecture. A WinGet
installation does not remove a separately extracted copy or its startup
registration. Remove or unregister the old manual copy before switching to the
WinGet-managed package.

### Virtual desktop interface cache

Normal updates do not require manual cache removal.

If virtual desktop initialization fails after migrating from the original
SylphyHorn or another fork, exit all related applications and remove the old
cache if present:

`%LocalAppData%\grabacr.net\SylphyHorn\assemblies`

To force the current SylphyHornPlus installation to regenerate its virtual
desktop interface cache, exit SylphyHornPlus and remove:

`%LocalAppData%\hwtnb.net\SylphyHornPlus\assemblies`

### Startup diagnostics

SylphyHornPlus keeps a small startup-phase log under
`%LocalAppData%\hwtnb.net\SylphyHornPlus\StartupTrace`. These logs help
diagnose startup failures and contain timestamps, startup phases and results,
elapsed time, process and session identifiers, the Windows build number, and
limited error metadata when startup fails. The newest 20 log files are kept.
They remain on the device and are not uploaded automatically.

When reporting a startup problem, include the most recent log from this folder
if possible.


## Requirements

* Windows 10 version 1809 (build 17763) or later


## Features

* Switching notification
  * Support for desktop name
<!-- ![](https://cloud.githubusercontent.com/assets/1779073/19052151/a6be54ac-89f0-11e6-8936-9bcc2aafc1d5.gif) -->

* Move active window to adjacent desktop  
(default key combination: <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Win</kbd> + <kbd><-</kbd> or <kbd>-></kbd>)
<!-- ![](https://cloud.githubusercontent.com/assets/1779073/19051476/22e49daa-89ee-11e6-8fe2-9734f2714871.gif) -->

* Move active window to create new desktop  
(default key combination: <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Win</kbd> + <kbd>D</kbd>)

* Pin window to all desktops ... It is more convenient than using the task view  
(default key combination: <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Win</kbd> + <kbd>P</kbd>)  
![](https://user-images.githubusercontent.com/1779073/40626965-e400321e-62f6-11e8-8947-b2ded3ed8c77.gif)

* Automatic app placement  
Place new windows of registered apps on a desktop specified by its number or name.
  * Create missing desktops, and close empty desktops after use
  * Rearrange windows that are already open

* Wallpaper for new desktops  
Set the wallpaper of newly created desktops by their name or number.

* Settings GUI (call from tasktray)  
![](https://user-images.githubusercontent.com/56633452/140468242-cac44131-b49e-4ed6-bb98-2be88c56b27e.png)
![](https://user-images.githubusercontent.com/56633452/140468237-33203a2f-fe08-4e20-8ffa-9c724e6e0a67.png)

* Control and manage Windows virtual desktops  
Windows 11
![](https://user-images.githubusercontent.com/56633452/140468244-4a714ccd-dcb2-491f-b76c-2cbf186dbda7.png)
![](https://user-images.githubusercontent.com/56633452/140468239-7bcc81a9-58b1-434b-8e80-12fa9332651a.png)
Windows 10
![](https://user-images.githubusercontent.com/56633452/141109503-a15bd99a-ba55-4e0a-a14e-d4a8b2edda73.png)
![](https://user-images.githubusercontent.com/56633452/141109493-4db1496e-e0ac-46b5-b483-d851651d7432.png)


## Command-line tool

`sylphyhorn-cli` lets you switch and manage desktops, move windows, and change
settings from a terminal, a script, or an AI agent. It controls SylphyHornPlus
running in the same Windows user session at the same elevation level, and
prints its results as JSON.

With WinGet, run `sylphyhorn-cli`. With a ZIP, run `sylphyhorn-cli.exe` in the
extracted `SylphyHorn` folder by its full path. Run `sylphyhorn-cli --help` to
list the commands, and `sylphyhorn-cli spec <command>` for the details of a
command.

An agent skill for AI coding assistants is included in the `skills/sylphyhorn`
folder next to `SylphyHorn.exe`.


## Credits

### Original authors & developers

* Manato KAMEYA [@Grabacr07](https://twitter.com/Grabacr07) (Author, Developer)
* Yutaka TSUMORI [@tmyt](https://twitter.com/tmyt) (Developer)


## License

SylphyHornPlus is licensed under [the MIT License](LICENSE.txt).
