# Agents — Project Checklist

Model-agnostic desktop-control ("computer use") tool: let any model (Ollama / Claude /
ChatGPT) see the screen and drive the real desktop. **C# / .NET 10. Wayland-first, Ollama-first.**

> Deeper design rationale: approved plan at `~/.claude/plans/enumerated-weaving-blossom.md`.
> Legend: `[x]` done · `[~]` in progress · `[ ]` todo

## Decisions locked
- **Stack:** C# / .NET 10, Central Package Management, layered `src/` + `tests/` (mirrors MyGarageTracker).
- **Architecture:** `IModelProvider` (brain) + `IDesktopDriver` (hands & eyes), decoupled through `Agents.Core`. CLI and any future API are thin front-ends over `AgentLoop`.
- **Target box:** Fedora 44, GNOME on **Wayland**. Input via `ydotool` (uinput). Capture via the XDG Screenshot portal through `gdbus` (no D-Bus NuGet).
- **First model:** Ollama (local). Claude / OpenAI behind the same interface later.
- **API layer:** CLI first; a FastEndpoints `Agents.Api` (localhost-bound + token auth) can bolt on later — not now.
- **Testing:** xUnit + Shouldly + **NSubstitute** (not Moq — SponsorLink history).
- **Zero new NuGet for the MVP:** hand-rolled CLI arg parsing (no System.CommandLine); input/capture via subprocess (ydotool/gdbus); Ollama via built-in `HttpClient`.
- **Dependency rule:** official + popular + clean-licensed; security-check before adding; **never install/fetch anything without explicit approval**.

## Done
- [x] Repo confirmed, plan approved, dependencies security-vetted.
- [x] Scaffolded .NET 10 solution — `Agents.slnx` + 6 projects; CPM + `.gitignore`. Builds green.
- [x] **`Agents.Core` contracts (LOCKED):** AgentAction/Type/MouseButton, ScreenCapture/ScreenInfo, UiElement, AgentContext/AgentStep, IDesktopDriver/IModelProvider/IGroundingProvider, AgentLoop + AgentLoopOptions (safety gate), AgentActionParser.
- [x] **Agents.Providers** — `OllamaProvider` (HttpClient → `/api/chat`, base64 screenshot, robust Fail-on-bad-output); Claude/OpenAI phase-2 stubs. Green.
- [x] **Agents.Core unit tests** — parser + loop + safety/dry-run + element-resolution (xUnit/Shouldly/NSubstitute).
- [x] **docs** — README + `docs/ARCHITECTURE.md` + `docs/ollama-prompt.md`.

## In progress
- [~] **Agents.Desktop** — `LinuxWaylandDriver` (ydotool input + XDG Screenshot portal via `gdbus`), Windows/macOS skeletons, `DriverFactory`. Capture path DECIDED: portal via `gdbus` (Tmds.DBus dropped — CVE-2026-39959).
- [ ] **Integration (Claude):** wire `Agents.Cli` (`driver-probe`, `run "<goal>"`, `--dry-run`), add Desktop + Ollama tests, full `dotnet build` + `dotnet test`, sweep template boilerplate (`Class1.cs`/`UnitTest1.cs`/default `Program.cs`), commit.

## ⛔ CHECKPOINT — STOP & validate direction (Claude will flag this)
Before building anything further, **pause and actually run the tool with the user** to confirm we're on the right track. Reached once `Agents.Cli` + the driver build green.
- [ ] Install driver prereqs — `ydotool` + `ydotoold` + uinput permission (**needs approval**).
- [ ] `agents driver-probe` on the real desktop → confirms it captures a screenshot + moves the mouse + types (driver works, **no model needed**).
- [ ] Upgrade Ollama 0.21.0 → 0.30.x + pull a vision model (**needs approval**).
- [ ] One real goal end-to-end — `agents run "<simple goal>"` (`--dry-run` first, then live) → the screenshot→decide→act loop does something sane.
- [ ] **Review direction with the user** before continuing.

## Review-hardening — from `fablereview/2026-07-02_architecture-security-review.md`
Full architecture + security review (Fable 5). Largely **pre-fan-out / pre-live-test** work — a
safety-critical + Core-contract subset should land BEFORE the live checkpoint above; the rest after.
Changing the locked Core here means re-touching Desktop/Providers/Tests.
- [ ] Parser: `TryParse` with error info (feed parse failures back to the model to self-correct) + trailing-comma/comment tolerance (items 1, 6).
- [ ] `ConfirmAction`: async + Allow/Deny/Abort (not veto=abort) + fire AFTER target resolution (item 2). ← core safety gate.
- [ ] Loop: cap executed actions (not just model turns); stop batch on first failure; FAIL (never blind-click) on unresolved element index (item 3).
- [ ] Add `Drag` (± `MouseDown`/`MouseUp`) to enum + `IDesktopDriver` before drivers harden (item 4).
- [ ] Coordinate/scroll contract doc in `Primitives.cs` — X/Y = screenshot-pixel space, origin top-left; scroll units/sign; single-monitor MVP (item 5).
- [ ] `Directory.Build.props` + analyzers (`AnalysisLevel=latest-recommended`, `TreatWarningsAsErrors`); delete `Class1.cs` ×4 (item 7).
- [ ] Driver safety: ydotool argv-only + text after `--`/via stdin to kill flag-injection (`type --file ~/.ssh/id_ed25519`!); strict key-combo allowlist blocking VT-switch / `ctrl+alt+F*` (S2, S3).
- [ ] `ydotoold` as a USER service, socket `0600` — NOT `chmod 666` (S4).
- [ ] Kill-switch: GNOME custom keyboard shortcut → `agents kill` → cancels the loop's `CancellationToken` (Wayland-legit) (S7).
- [ ] Confirmation ON by default for `Type`/`Key` (prompt-injection defense, S1); explicit `--yolo` to opt out.
- [ ] `driver-probe` grabs 3 frames back-to-back to detect per-capture portal prompts; if they repeat → ScreenCast + PipeWire `restore_token` (Part 3).
- [ ] Cloud providers: opt-in gate + "never put secrets in the goal" (re-sent every turn); screenshots read-and-delete; add `*.png` to `.gitignore` (S5, S6).

## Later (post-checkpoint)
- [ ] **Agents.Grounding** — AT-SPI Set-of-Marks via a Python helper subprocess (no Tmds.DBus). Deferred; `AgentLoop` already handles null grounding.
- [ ] **Claude + OpenAI providers** — real computer-use loops (official Anthropic / OpenAI SDKs; re-vet packages before adding).
- [ ] **Windows / macOS drivers** — flesh out the skeletons.

## Security notes
- Pins CVE-free (checked 2026-06-27 / 07-02). **Avoided:** Moq (SponsorLink), FluentAssertions v8 (now paid), **Tmds.DBus (CVE-2026-39959)**, ImageSharp (split license; not needed), System.CommandLine (not needed — hand-rolled CLI).
- **Net new third-party for the MVP: none.** Input & capture via subprocess; Ollama via built-in `HttpClient`.
- Input injection is gated: kill-switch / max-steps / dry-run in `AgentLoop`.
- If an `Agents.Api` HTTP layer is ever added it MUST be localhost-bound + token-authed — it can drive the real desktop.
