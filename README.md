# Unicord Dev

A fork of [Unicord](https://github.com/UnicordDev/Unicord), the free, open source Discord client for Windows 10 and Windows 10 Mobile, providing a fast, efficient, native feeling Discord experience. Built on [DSharpPlus](https://github.com/DSharpPlus/DSharpPlus/)!

![promo](Assets/promo1.png)

> [!IMPORTANT]
> **This fork is AI-driven.**
>
> Changes here are researched, written and documented by AI agents working against the codebase, with a human reviewing and deciding what ships. The goal is to stay as close to Unicord's expected behaviour as possible — this is not a redesign or a rebrand, and it is not trying to become a different client.
>
> Treat it as **a codebase first and an app second**. Changes land here so they can be read, analysed and — where they hold up — reimplemented or pulled into upstream Unicord. Every change is a normal commit with a real explanation of *why*, so it can be judged on its merits rather than taken on trust.
>
> It also runs. On most desktop Windows setups you can install it and use it as an ordinary Unicord client.

> [!NOTE]
> **Desktop is the focus. Phones are not abandoned.**
>
> Development and testing happen on Windows 10 and 11 desktop, so that is where things are known to work. Windows 10 Mobile is still built and still shipped — the 1703 API floor is respected, and each release carries an ARM package — but **nobody here owns a Windows phone**, so nothing on that platform is verified by hand.
>
> If you run this on a phone, please [open an issue](https://github.com/Darker935/Unicord/issues). Bugs, papercuts, or "this used to work" reports are all welcome, and are the only way phone problems get found at all.

> [!WARNING]
> **Builds are self-signed.** These packages are not from the Microsoft Store, so Windows will refuse to install them until you trust the certificate that comes with each release. `INSTALL.md` inside the download explains it in one command. If you would rather not do that, install [Unicord from the Store](https://github.com/UnicordDev/Unicord) instead.

## Downloads

Grab the [latest release](https://github.com/Darker935/Unicord/releases). Every release ships **x64**, **x86** and **ARM** packages, the certificate to trust, and install instructions.

| Your device | Package |
|---|---|
| Windows 10/11 PC (Intel/AMD) | `x64` |
| Older 32-bit PC | `x86` |
| Windows 11 on Arm | `x64`, which Windows emulates |
| Windows 10 on Arm | `x86`, which Windows emulates |
| Windows 10 Mobile phone | `ARM` |

Windows on Arm PCs no longer run 32-bit Arm applications, so the `ARM` package is for phones only.

For upstream's Store release, see [UnicordDev/Unicord](https://github.com/UnicordDev/Unicord).

## Getting Started
So you wanna build Unicord, well you're gonna need to have a few things handy.

### Prerequisites
 - Windows 11 (Build 22000+)
 - Windows 11 SDK Build 26100
 - Visual Studio 2022 or later
   - Universal Windows Platform tools
     - For Visual Studio < `17.10`, Select `Universal Windows Platform Workload`
     - For Visual Studio >= `17.10`, Select `WinUI Application Development Workload` and `Installation details`->`WinUI application development`->`Optional`->`Universal Windows Platform tools`

### Building and Installing
By default, cloning a repository through Visual Studio should handle submodules, but for the sake of completeness and as with all GitHub projects, you'll also need to pull submodules.
To do this, use:

```sh
$ git submodule update --init --recursive
```

From here, building should be as simple as double clicking `Unicord.sln`, ensuring your targets are appropriate to your testing platform (i.e. Debug x64), and hitting F5. 
Once built and deployed, it should show in your start menu as "Unicord Dev", data and settings are kept separate from the Store version, so they can be installed side by side.

Local builds cannot be signed with the certificate named in the committed project file — it belongs to upstream's author and exists on no other machine. `tools/New-SigningCertificate.ps1` creates one of your own, trusts it locally, and prints the thumbprint to pass to MSBuild. See `Unicord-memory-vault/10 - Workflows/Building.md`.

![Canary](Assets/canarylauncher.png)

## Testing
Unicord currently lacks any kind of unit testing. This will likely change as I adopt a more sane workflow, but for now, I suggest going around the app and making sure everything you'd use regularly works, and ensuring all configurations build. A handy way of doing this, is Visual Studio's Batch Build feature, accessible like so:

![batch build](Assets/batchbuildmenu.png)

On one specific note, while the project technically targets a minimum of Windows 10 version 1709 (Fall Creators Update), all code should compile and run on version 170**3** (Creators Update) to maintain Windows Phone support. Please pay special attention to the minimum required Windows version when consuming UWP APIs, and be careful when consuming .NET Standard 2.0 APIs, which may require a newer Windows version.

## Contributing

Contributions are welcome here, and so are plain bug reports — especially from phones, which nobody here can test.

Because this fork exists to feed changes back upstream, the bar is that a change should be **explainable**: what broke, why, and what evidence says the fix is right. Root causes over workarounds. That applies equally to AI-written and human-written changes.

Want a feature that doesn't already exist? Dig in. If you don't have the know-how yourself, file an issue — someone might pick up on it.

For upstream Unicord itself, contribute at [UnicordDev/Unicord](https://github.com/UnicordDev/Unicord).

## Get in Touch
We have a Discord server specifically for Unicord development and testing, join here:

[![Unicord](https://discordapp.com/api/guilds/648519011130408980/widget.png?style=banner2)](https://discord.gg/64g7M5Y)

## License
Unicord is licensed under the [MIT License](LICENSE).

## Acknowledgements
 - [WamWooWam](https://github.com/WamWooWam) and the [Unicord](https://github.com/UnicordDev/Unicord) contributors, who wrote the client this fork is built on
 - [DSharpPlus](https://github.com/DSharpPlus/DSharpPlus) Contributors, for providing a wonderful base on which much of this is built
 - Any member of my personal Discord server who's given me any tips, feedback or guidance! <3
