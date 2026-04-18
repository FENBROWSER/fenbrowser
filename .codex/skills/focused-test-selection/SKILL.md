---
name: focused-test-selection
description: Select the smallest meaningful build and test slice to verify a change with minimal cost.
---

# Skill: focused-test-selection

Use when:
- a code change was made
- the user asks what to run
- verification must stay cheap and fast

## Goal

Choose the smallest meaningful build/test slice that can falsify the change.

## Workflow

1. Identify the touched subsystem:
   - Core
   - FenEngine
   - Host
   - DevTools
   - WebDriver
   - Tests/tooling
2. Choose verification in this order:
   - focused project build
   - focused test class / filter
   - subsystem slice
   - solution build/test only if needed
3. Add runtime artifact checks when rendering/host behavior is involved.

## Typical examples

Build only:
- `dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -c Debug -v minimal`

Focused tests:
- `dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~NameOfRelevantTests" --logger "console;verbosity=minimal"`

Rendering checks:
- inspect `debug_screenshot.png`
- inspect `dom_dump.txt`
- inspect `logs/fenbrowser_*.log`

## Do not

- default to full solution test runs
- run Test262/WPT unless the task actually touches those surfaces
- claim confidence without a falsifying check

## Output contract

1. recommended build command
2. recommended focused test command
3. runtime artifacts to inspect if relevant
4. why this is the smallest sufficient check

## Token discipline

- give only the commands that matter
- do not list every possible suite
