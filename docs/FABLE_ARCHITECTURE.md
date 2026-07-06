# Fable Architecture & Handoff Guide — `Agents`

> **You are Opus, resuming this project.** This doc is your entry point. Read it top-to-bottom,
> then skim `CHECKLIST.md`, `docs/ARCHITECTURE.md`, and `fablereview/2026-07-02_architecture-security-review.md`.
> The **"Remaining work"** section below is your ordered task list with exact per-item instructions.
> Authored by Fable 5 on 2026-07-06, handing off from the branch `feat/scaffold-core`.

---

## 0. The one rule that overrides everything
**NEVER install or fetch software (packages, binaries, models, `curl|sh`) without the user's
explicit, per-command approval.** Say plainly: *"STOP — about to install `<thing>` via `<cmd>`. Approve?"*
and wait. This is the user's standing top-priority rule (a PreToolUse hook also enforces it). Read-only
inspection (`--version`, `dotnet list package`, `git status`) is fine.

Other standing constraints:
- Prefer **official + popular + clean-licensed** dependencies; security-check every package before adding.
- Ask before destructive ops (`rm`, `git reset --hard`, force-push).
- Commit/push only when the user asks. We're on a feature branch (`feat/scaffold-core`), not `main`.

---

## 1. What this is
A **model-agnostic desktop-control ("computer use") tool**: any model (Ollama / Claude / ChatGPT)
sees the screen and drives the *real* desktop — move, click, type, drag — agentically, across any app.
The model-side "brain" is a commodity we *consume*; the value is the **local harness** + a
**provider-agnostic** interface. **C# / .NET 10. Wayland-first (Fedora 44 / GNOME 50). Ollama-first.**

Target box specifics that shaped decisions: GNOME on Wayland (no X11 session); input via `ydotool`
(uinput); capture via the **XDG Screenshot portal through `gdbus`** (we deliberately avoid the
`Tmds.DBus` NuGet — CVE-2026-39959). `gdbus`, AT-SPI, .NET 10, and Ollama are already installed;
`ydotool` is **not**.

---

## 2. Architecture (the shape you must preserve)
Two abstractions, decoupled by `Agents.Core`. Everything else is a thin adapter.

- **`IModelProvider`** — the *brain*. `DecideAsync(AgentContext) → IReadOnlyList<AgentAction>`.
- **`IDesktopDriver`** — the *hands & eyes*. `Capture / MoveMouse / Click / DoubleClick / Drag / Type / Key / Scroll`.
- **`IGroundingProvider`** (optional) — Set-of-Marks element list (AT-SPI). Loop tolerates `null`.
- **`AgentLoop`** — orchestrates one run: `capture → (ground) → decide → resolve targets → confirm → execute`,
  behind an async safety gate, until the model emits `Done`/`Fail` or a limit trips.

The CLI (and any future API) are just front-ends that construct a driver + provider + `AgentLoop`.

**`AgentLoop` lifecycle (already implemented — do not regress):**
1. Capture screenshot + optional element list → build `AgentContext` (includes a one-off `Notice`).
2. `provider.DecideAsync(context)`. **Empty result → set `Notice` ("your output wasn't parseable"),
   retry; after `MaxConsecutiveEmpty` → `Failed`.** (This is the parse-error feedback loop.)
3. For each action: `Done`/`Fail` are terminal. Otherwise **resolve target coordinates FIRST**
   (element index → centre, else raw X/Y). **Unresolved target → record failure, stop the batch
   (NEVER blind-click).**
4. **Async confirm gate** on the *resolved* action: `Allow` / `Deny` (skip + re-plan) / `Abort` (kill).
5. Execute via the driver; **stop the batch on the first failure**; enforce `MaxActions` + `MaxSteps`.

**Coordinate contract:** `X/Y`/`ToX/ToY` are **screenshot-pixel space, origin top-left**. Drivers
translate. Scroll: positive `dy` = down, positive `dx` = right. Single-monitor MVP.

---

## 3. Project layout & key files
```
Agents.slnx · Directory.Build.props (net10, analyzers, warnings-as-errors) · Directory.Packages.props (CPM)
src/
  Agents.Core/        Primitives.cs (AgentAction/Type, MouseButton, ScreenCapture/Info, UiElement,
                        AgentStep, AgentContext) · Abstractions.cs (the 3 interfaces) ·
                        AgentLoop.cs (AgentLoop + AgentLoopOptions + ConfirmDecision + ActionConfirmation) ·
                        AgentActionParser.cs (JSON→actions, tolerances, SchemaPrompt)
  Agents.Desktop/     LinuxWaylandDriver.cs (ydotool input + gdbus portal capture) · KeyCombo.cs
                        (security allowlist) · KeyMap.cs · IProcessRunner.cs (test seam +
                        DefaultProcessRunner) · WindowsDriver.cs / MacDriver.cs (skeletons) · DriverFactory.cs
  Agents.Providers/   OllamaProvider.cs (real) · ClaudeProvider.cs / OpenAiProvider.cs (phase-2 stubs)
  Agents.Grounding/   (empty — AT-SPI deferred; see task F)
  Agents.Cli/         Program.cs → CliApp.cs · RunCommand / DriverProbeCommand / KillCommand ·
                        ConfirmGates.cs · KillSwitch.cs (Unix-domain control socket) · RuntimePaths.cs · Help.cs
tests/
  Directory.Build.props (relaxes warnings-as-errors for tests only)
  Agents.Tests/       TestDoubles.cs (RecordingProcessRunner, StubHttpMessageHandler — SHARED) ·
                        AgentActionParserTests · AgentLoopTests · DesktopMouseTests
docs/ (README, ARCHITECTURE.md, ollama-prompt.md, this file) · fablereview/ (the security review)
```

## 4. Conventions
.NET 10, Central Package Management, layered `src/`+`tests/` (mirrors the user's MyGarageTracker).
Tests: **xUnit + Shouldly + NSubstitute** (NOT Moq — SponsorLink). Production is warnings-as-errors
(`CA1859` suppressed by choice); tests relaxed via `tests/Directory.Build.props`. **Zero net-new
runtime NuGet in the MVP** — input/capture shell out via `IProcessRunner`; Ollama via built-in `HttpClient`;
CLI arg-parsing hand-rolled.

---

## 5. Current state (green, as of this handoff)
- Builds clean (analyzers + warnings-as-errors on `src/`). **`dotnet test` → 69 passing.**
- **Done & committed:** hardened Core; LinuxWayland driver (input + capture) with the security
  allowlist; Ollama provider; full CLI (probe/run/kill, confirm-by-default, `--yolo`); Core tests;
  docs incl. a Security & threat-model section. The entire fablereview security review's *code* items
  are addressed (async Allow/Deny/Abort gate, fail-on-unresolved, action caps, parse-retry, Drag,
  coordinate contract, key-combo allowlist blocking VT-switch/reboot/X-zap, argv/`--` flag-injection
  defense, `agents kill` kill-switch).
- **Test scaffolding started:** `TestDoubles.cs` + `InternalsVisibleTo` (Cli, Desktop) + `DesktopMouseTests` (15 tests).
- **NOT yet run for real:** `ydotool` input and the `gdbus` portal capture have never executed
  (no ydotool installed, no display in the build). **This is the crux of the next step.**

---

## 6. Remaining work — your ordered checklist
Each item: what · why · exact files · what to change · how to verify. Do them roughly in order.

### ⛔ A. The live checkpoint — prove it actually works (do FIRST, WITH the user)
Everything is unit-tested but has never touched a real desktop. This gate validates direction before
building more. **All installs here need the user's explicit approval.**
- [ ] **Install `ydotool` + `ydotoold`** (Fedora repo: `sudo dnf install ydotool`). Configure `ydotoold`
      as a **user service** with the socket at `$XDG_RUNTIME_DIR/.ydotool_socket`, perms **0600** —
      NOT the widely-advised `chmod 666` (that hands every local process keyboard/mouse injection; review S4).
      Ensure the user is in a group with `/dev/uinput` access (udev rule or `input` group).
- [ ] **`agents driver-probe`** — the model-independent proof. It captures 3 frames back-to-back
      (watch its timing warning: GNOME's Screenshot portal may prompt **per capture** — if so the loop
      is unusable and you must switch capture to **ScreenCast + PipeWire** using a persisted
      `restore_token`; review Part 3), then moves the mouse to screen-centre and types "agents-probe".
      Confirm capture + mouse + type all actually happen.
- [ ] **Upgrade Ollama 0.21.0 → 0.30.x** (currently stale; not vulnerable but weak on vision-model
      support) and **pull a vision model** (`ollama pull qwen2.5-vl` or `llama3.2-vision`). Keep Ollama
      loopback-bound (never `OLLAMA_HOST=0.0.0.0`).
- [ ] **`agents run "<simple goal>" --dry-run`** then live (e.g. "open the Activities overview").
      Confirm the screenshot→decide→act loop does something sane; the confirm-by-default gate prompts you.
- [ ] **Bind `agents kill` to a GNOME custom keyboard shortcut** (Settings → Keyboard) — the
      Wayland-legit panic key. Verify it cancels a running loop.
- [ ] **Review direction with the user** before proceeding to grounding / cloud providers / other platforms.

### B. Finish the test fleet (7 files; the seam is ready)
Reuse the SHARED fakes in `tests/Agents.Tests/TestDoubles.cs` (`RecordingProcessRunner` with `.Calls` /
`ProcessCall.CommandLine`; `StubHttpMessageHandler` with `.LastRequestBody`). Internals of Cli/Desktop
are visible to tests. `DesktopMouseTests.cs` is the worked example. Each file is disjoint — safe to
fan out. Build only the src project you target while writing; run full `dotnet test` at the end.
- [ ] `DesktopDragTests.cs` — `DragAsync`: exactly 4 ordered ydotool calls (mousemove→click press-code→
      mousemove→click release-code); button codes left 0x40/0x80, right 0x41/0x81, middle 0x42/0x82.
- [ ] `DesktopKeyTests.cs` — **(highest value)** allowed combos (`ctrl+c`, `Return`, `alt+Tab`) →
      correct `ydotool key <codes>` argv; **security rejections** (`ctrl+alt+F1..F12`, `ctrl+alt+Delete`,
      `ctrl+alt+BackSpace`, off-allowlist) → throws AND `RecordingProcessRunner.Calls` stays empty.
- [ ] `DesktopTypeTests.cs` — argv is exactly `["type","--",text]`; flag-injection payload
      (`--file ~/.ssh/id_ed25519`) is the literal text element after `--`; spaces/quotes/unicode verbatim.
- [ ] `OllamaRequestTests.cs` — via `StubHttpMessageHandler`: POST `{base}/api/chat`; body has model,
      `stream:false`, `format:"json"`, system message (schema), user message (goal + element list +
      history) with `images:[<base64>]`; `AgentContext.Notice` surfaced.
- [ ] `OllamaResponseTests.cs` — `{message:{content}}` parsed; empty/whitespace/malformed → empty list
      (feeds the retry loop); handler throwing `HttpRequestException` → propagates.
- [ ] `CliArgParseTests.cs` — command dispatch (run/driver-probe/kill/help) + flags
      (`--dry-run`/`--yolo`/`--model`/`--max-steps`); note any I/O-coupling that blocks testing.
- [ ] `CliSafetyTests.cs` — confirm-gate decision mapping (y→Allow, a→Abort, blank/EOF→Deny/Abort;
      auto-Allow Move/Scroll/Wait/Screenshot; `--yolo`→Allow all) + `KillSwitch` handle-file round-trip
      in a temp `XDG_RUNTIME_DIR` (arm → kill connects → cancellation; stale cleanup; single-instance guard).

### C. Screenshot hygiene (review S6) — small, do soon
- [ ] `LinuxWaylandDriver.CaptureAsync` reads the portal PNG (`File.ReadAllBytesAsync(path)` ~line 160)
      but **never deletes it**. Wrap: read then `File.Delete(path)` in a `finally`, incl. failure paths,
      so screenshots don't accumulate on disk. (`*.png` is already in `.gitignore`.) Don't log full `Text` payloads outside dry-run.

### D. Scroll-sign verification (needs real ydotool)
- [ ] The driver negates `dy` for evdev `REL_WHEEL` (there's a TODO in `LinuxWaylandDriver`). Verify the
      direction against the installed ydotool during checkpoint A; if that build already maps wheel to
      screen-scroll direction, remove the negation. Also confirm `mousemove --wheel` exists on the
      installed ydotool (older builds need `REL_WHEEL`/`REL_HWHEEL` via uinput — documented TODO).

### E. F-key allowlist decision (pending user)
- [ ] `KeyCombo.cs` is currently **strict** — plain F-keys (F5/F11) and `alt+F4` are not emittable at
      all; only `ctrl+alt+F*` is the real hazard. If the user wants F-keys usable, add F1–F12 to the
      allowlist + KeyMap while keeping the `ctrl+alt+F*` VT-switch rejection (a one-line, independent guard).

### F. `Agents.Grounding` — AT-SPI Set-of-Marks (deferred; enables reliable clicking on weak models)
Local vision models are poor at pixel coordinates; Set-of-Marks (numbered elements) fixes that.
- [ ] Write a small **Python helper** using AT-SPI (`gi` / `Atspi`, or `pyatspi`) that walks the
      accessibility tree of the focused/top-level windows and prints JSON: `[{index,role,name,x,y,w,h}]`.
      Needs `at-spi2-core` (installed) + Python AT-SPI bindings (`python3-pyatspi` or gobject-introspection —
      **install with approval**). Ship it as a repo script (e.g. `scripts/atspi_elements.py`).
- [ ] Implement `AtSpiGroundingProvider : IGroundingProvider` in `Agents.Grounding` that invokes the
      helper via a process-runner seam (mirror `IProcessRunner`) and maps JSON → `UiElement[]`. Return an
      empty list on any failure (the loop tolerates it — raw-coordinate fallback). Unit-test the JSON→UiElement mapping.
- [ ] Wire it into the CLI `run` command (construct + pass to `AgentLoop`). `OllamaProvider` already
      renders the element list into the prompt.

### G. Claude + OpenAI providers (cloud — gate carefully, review S5)
- [ ] `ClaudeProvider`: official **`Anthropic` C# SDK** computer-use tool-loop. Confirm the exact
      computer-use tool `type` + beta header from the LIVE docs
      (`https://platform.claude.com/docs/en/agents-and-tools/tool-use/computer-use.md`) — do NOT guess a
      dated string. Map Claude's actions ↔ our `AgentAction` (it has drag; we added it). Re-vet + get
      approval for the `Anthropic` package (~12.8.0, official).
- [ ] `OpenAiProvider`: official OpenAI SDK `computer-use-preview`.
- [ ] **Security gate:** cloud providers ship the screenshot **+ full history (incl. the goal, re-sent
      every turn)** off-machine. Require an explicit opt-in flag; print a warning; document "never put
      secrets in the goal." Ollama-first stays the privacy default.

### H. Windows / macOS drivers (skeletons exist)
- [ ] `WindowsDriver`: `SendInput` (user32 P/Invoke) for input; `Graphics.CopyFromScreen`/BitBlt capture.
- [ ] `MacDriver`: CoreGraphics `CGEventPost`; `CGWindowListCreateImage`/ScreenCaptureKit; needs
      Accessibility + Screen-Recording permissions. Keep them behind the `DriverFactory` OS switch.

### I. Optional / nice-to-have
- [ ] `AgentActionParser` TryParse-with-error shape (review item 1's parser-level piece; the loop's
      `Notice`-retry already covers the user-visible symptom, so this is polish).
- [ ] A FastEndpoints **`Agents.Api`** localhost daemon (POST a goal, SSE-stream actions) IF the user
      wants a web/mobile UI — **must** be localhost-bound + token-authed (it can drive the real desktop).

---

## 7. Build / test / run
```bash
cd /home/nmahesh/Documents/MyGit/Agents
dotnet build Agents.slnx            # 0 warnings on src (warnings-as-errors), tests relaxed
dotnet test  Agents.slnx            # currently 69 passing
dotnet run --project src/Agents.Cli -- driver-probe        # after ydotool is installed
dotnet run --project src/Agents.Cli -- run "<goal>" --dry-run
```

## 8. References
- `CHECKLIST.md` — living status.
- `docs/ARCHITECTURE.md` — architecture + full Security & threat-model section (S1–S7).
- `docs/ollama-prompt.md` — the Ollama system-prompt template.
- `fablereview/2026-07-02_architecture-security-review.md` — the review this hardening came from (source of the S-numbers).
- Design plan: `~/.claude/plans/enumerated-weaving-blossom.md`.
