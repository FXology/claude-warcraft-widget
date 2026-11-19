# Claude Warcraft Widget

A small Windows desktop widget for [Claude Code](https://code.claude.com): your plan's usage limits (5-hour and weekly) with a forecast of when you'll run out, and your background Claude Code chats with their status and context use, one click away. It comes in a Warcraft III look, with orc voices and game sounds, or as a plain dark card.

| Warcraft III look | Plain look |
| --- | --- |
| ![Warcraft III look](docs/warcraft.png) | ![Plain look](docs/simple.png) |

Unofficial fan project. Not affiliated with or endorsed by Anthropic or Blizzard Entertainment.

## Install

1. Download `ClaudeUsageWidget-Setup.exe` from [Releases](../../releases) and run it. It isn't code-signed, so Windows may say "Windows protected your PC": click **More info → Run anyway**.
2. It asks, then installs into `%LOCALAPPDATA%\Programs\ClaudeUsageWidget`, points Claude Code's status line at the widget (your `~/.claude/settings.json` is backed up first), starts it with Windows and launches it.
3. Your numbers appear after Claude Code's next reply.

Needs Windows 10 or 11 and Claude Code on a Pro or Max plan (only those plans report usage limits). Nothing else: the setup carries its own .NET runtime, and the status line runs inside the widget's exe, so no Node.js either. Running a newer setup over an installed copy updates it in place.

## What it does

- **Usage limits**: 5-hour and weekly, with reset times and a countdown. When your pace this window would hit the limit before it resets, the line turns orange: `full ~16:40 · resets 18:00`. Notifications at 80% and 95%, and when a limit resets (with confetti).
- **Chats panel**: your background Claude Code chats (`claude --bg`, agent view), marked **needs input**, **working** or **done**, each with a ring showing its context use. Hover for what it's doing now; click to open it in PowerShell; middle-click for its folder; right-click to stop or remove it. `+` starts a new background chat.
- **Two looks** (menu → Display → Warcraft III look): the Warcraft one is drawn from [warcraftcn/ui](https://github.com/TheOrcDev/warcraftcn-ui) art, with loading-bar limits, the Cinzel font, the orc gauntlet cursor and game sounds. Click the card too many times and, like a Warcraft III critter, it bursts. The plain one is a dark card with rounded corners.
- **Small things**: drag to move, drag the corner grip to resize the whole card (text included), drag its right edge to make it wider for long chat names (menu → Display → Reset size undoes both), scroll to change opacity, Esc or `–` hides it to the tray (its icon is a column filling with your 5-hour usage), middle-click the bars for compact mode, always on top, click-through, lock position, start with Windows.

## Sounds

The Warcraft look plays game sounds on chat and widget events. All of Blizzard's audio is © Blizzard Entertainment, so **none of it is in this repo or in the setup**:

- **Orc lines** (Warcraft III peon and grunt): the widget downloads these once, on first start, from the [peon-ping](https://github.com/PeonPing/peon-ping) project's pack, and keeps only files matching the SHA-256 hashes pinned in `Program.cs`. That is the widget's only network access.
- **WoW interface sounds** are optional: until you add them, an orc line plays in their place (and the plain clicks stay quiet). To add them, save each as a WAV with the name below in the `sounds` folder next to the exe. The links are to Wowhead.

| File | Plays when | Sound |
| --- | --- | --- |
| `QuestComplete.wav` | the weekly limit resets | [QUESTCOMPLETED](https://www.wowhead.com/sound=619) |
| `LevelUp.wav` | the 5-hour limit resets | [Level Up](https://www.wowhead.com/sound=1440) |
| `TellMessage.wav` | a chat needs your input | [TellMessage](https://www.wowhead.com/sound=3081) |
| `QuestLogOpen.wav` | you open a chat | [igQuestLogOpen](https://www.wowhead.com/sound=844) |
| `QuestFailed.wav` | a chat fails | [igQuestFailed](https://www.wowhead.com/sound=847) |
| `RaidWarning.wav` | a limit passes 95% | [RaidWarning](https://www.wowhead.com/sound=8959) |
| `BackpackOpen.wav` | you open a chat's folder | [igBackPackOpen](https://www.wowhead.com/sound=862) |
| `MenuOpen.wav` / `MenuClose.wav` | the menu opens / closes | [igMainMenuOpen](https://www.wowhead.com/sound=850) / [igMainMenuClose](https://www.wowhead.com/sound=851) |
| `CardShow.wav` / `CardHide.wav` | the card comes back / hides to the tray | [igCharacterInfoOpen](https://www.wowhead.com/sound=839) / [igCharacterInfoClose](https://www.wowhead.com/sound=840) |
| `OptionTick.wav` | you tick an option | [igMainMenuOptionCheckBoxOn](https://www.wowhead.com/sound=856) |

Turn all of it off under menu → Alerts → Game sounds. The burst's splat (`sounds/explode.wav`) and the confetti pop (`confetti.mp3`) are this project's own.

## How it works

Claude Code hands `rate_limits` and each chat's context use to its [status line command](https://code.claude.com/docs/en/statusline) after every reply. The installed widget is that command (`ClaudeUsageWidget.exe --statusline`): it saves the numbers to `%LOCALAPPDATA%\ClaudeUsageWidget\` and prints a short line such as `Opus 5.5 · ctx 8% · 5h 24% · 7d 41%`. The widget reads those files. The chat list comes from `claude agents --json --all`. No tokens or credentials are involved, and nothing about you is sent anywhere.

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet publish -c Release -o dist      # the widget, with art\ and sounds\ beside it
dist/ClaudeUsageWidget.exe --selftest  # its checks
```

To feed it from Claude Code without the installer, point the status line at it in `~/.claude/settings.json` (or at `statusline.js`, the same job in Node):

```json
"statusLine": { "type": "command", "command": "\"C:/path/to/dist/ClaudeUsageWidget.exe\" --statusline" }
```

The one-file setup (about 50 MB, with the .NET runtime inside):

```sh
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o setup
```

Rename `setup/ClaudeUsageWidget.exe` to `ClaudeUsageWidget-Setup.exe`: run with no `art` folder beside it, the exe acts as the installer. Publishing to `dist` works while the widget runs from there; it restarts itself into the new build.

## Credits

- Frames, button and cursor: [warcraftcn/ui](https://github.com/TheOrcDev/warcraftcn-ui) (MIT, `art/warcraftcn-LICENSE.txt`)
- Font: [Cinzel](https://github.com/NDISCOVER/Cinzel) (SIL Open Font License, `art/Cinzel-OFL.txt`)
- Orc lines: Warcraft III © Blizzard Entertainment, via [peon-ping](https://github.com/PeonPing/peon-ping); WoW sounds © Blizzard Entertainment

## License

[MIT](LICENSE) for this project's code and its own sounds; the third-party parts above keep their licenses.
