---
name: build-fix-from-log
description: Triage build failures from logs and apply the smallest correct fix at the first meaningful error.
---

# Skill: build-fix-from-log

Use when:
- `dotnet build` fails
- compile errors are pasted
- startup errors clearly originate from a build/runtime compile issue

## Goal

Find the first meaningful failure and fix only that scope.

## Workflow

1. Read only the first real error, not all downstream noise.
2. Identify:
   - project
   - file
   - symbol/member/type involved
3. Inspect only nearby code first.
4. Propose or apply the smallest correct fix.
5. Verify with the smallest affected build command.

## Preferred commands

- `dotnet build <project.csproj> -c Debug -v minimal`
- if solution-level failure is required, keep output narrow and focus on first real error

## Do not

- fix warnings unless required by the task
- refactor unrelated files
- chase secondary errors before the primary one is resolved
- paste giant logs back to the user

## Output contract

1. first real error
2. root cause
3. files to change
4. minimal fix
5. verification result

## Token discipline

- summarize logs, do not echo them
- inspect the touched project first
- keep the answer under 10 bullets unless asked for detail
