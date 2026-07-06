# Agents — Project Checklist

Model-agnostic desktop-control ("computer use") tool: let any model (Ollama / Claude /
ChatGPT) see the screen and drive the real desktop. **C# / .NET 10. Wayland-first, Ollama-first.**

> **Resuming? Read `docs/FABLE_ARCHITECTURE.md`** — the full architecture + detailed "pick up from
> here" instructions for the remaining work. This file is the quick status.
> Design rationale: `~/.claude/plans/enumerated-weaving-blossom.md`. Legend: `[x]` done · `[~]` in progress · `[ ]` todo

## Decisions locked
- **Stack:** C# / .NET 10, Central Package Management, layered `src/` + `tests/`.
- **Architecture:** `IModelProvider` (brain) + `IDesktopDriver` (hands & eyes) decoupled by `Agents.Core`; `AgentLoop` orchestrates. CLI is a thin front-end.
- **Target box:** Fedora 44, GNOME Wayland. Input via `ydotool`; capture via the XDG Screenshot portal through `gdbus` (no D-Bus NuGet — Tmds.DBus CVE-2026-39959).
- **First model:** Ollama (local); Claude / OpenAI behind the same interface later (cloud-gated).
- **Testing:** xUnit + Shouldly + NSubstitute. **Zero net-new runtime NuGet in the MVP.**
- **Rule:** official + clean-licensed deps, security-checked; **never install/fetch without explicit approval.**

## Done
- [x] Scaffold, CPM, `Directory.Build.props` (analyzers + warnings-as-errors), `.gitignore`.
- [x] **`Agents.Core`** — hardened: AgentAction (+`Drag`, `ToX/ToY/ToElement`), AgentActionParser (tolerances + SchemaPrompt), **AgentLoop** (async Allow/Deny/Abort gate fired post-resolution · fail-on-unresolved-element · MaxActions/MaxSteps caps · parse-error → `Notice` → retry), coordinate contract.
- [x] **`Agents.Desktop`** — LinuxWaylandDriver (ydotool input + gdbus portal capture), **KeyCombo allowlist** (blocks VT-switch / reboot / X-zap), KeyMap, DragAsync, `IProcessRunner` seam, Win/macOS skeletons, DriverFactory.
- [x] **`Agents.Providers`** — OllamaProvider (real; unparseable → empty → retry); Claude/OpenAI phase-2 stubs.
- [x] **`Agents.Cli`** — `driver-probe` (3-frame portal check), `run`, `agents kill` (Unix-socket kill-switch), confirm-by-default gate + `--yolo`.
- [x] **fablereview security review addressed (code):** S1 confirm-by-default · S2 key allowlist · S3 argv/`--` flag-injection defense · S7 kill-switch · async gate · fail-on-unresolved · action caps · coordinate contract · analyzers.
- [x] **Docs** — README, `docs/ARCHITECTURE.md` (+ Security & threat model), `docs/ollama-prompt.md`, **`docs/FABLE_ARCHITECTURE.md`** (Opus handoff guide).
- [x] **Tests — 69 passing.** Core (parser/loop/safety, 54) + `DesktopMouseTests` (15). Shared seam: `TestDoubles.cs` (RecordingProcessRunner, StubHttpMessageHandler) + `InternalsVisibleTo` + tests-scoped `Directory.Build.props`.

## In progress
- [~] **Remaining test files (4 agents, Opus 4.8):** DesktopDrag · DesktopKey (security) · DesktopType · OllamaRequest · OllamaResponse · CliArgParse · CliSafety.

## ⛔ CHECKPOINT — STOP & validate direction (needs the user; NOT yet done)
The code is unit-tested but `ydotool` / `gdbus` have **never actually run**. Before building more:
- [ ] Install `ydotool` + `ydotoold` (user service, socket **0600** not 666) + uinput perms (**approval**).
- [ ] `agents driver-probe` on the real desktop → capture + mouse + type work; check portal-prompt behavior.
- [ ] Upgrade Ollama 0.21 → 0.30.x + pull a vision model (**approval**).
- [ ] `agents run "<goal>"` (dry-run then live); bind `agents kill` to a GNOME shortcut.
- [ ] **Review direction with the user.**

## Remaining (post-checkpoint) — see `docs/FABLE_ARCHITECTURE.md` §6 for detailed instructions
- [ ] S6 screenshot read-and-delete in the driver (`*.png` now gitignored).
- [ ] Scroll-sign verify vs real ydotool; F-key allowlist decision (strict vs loosen).
- [ ] `Agents.Grounding` — AT-SPI Set-of-Marks (Python helper + provider).
- [ ] Claude + OpenAI real providers (cloud-gated, S5).
- [ ] Windows / macOS drivers.

## Security notes
- Pins CVE-free. **Avoided:** Moq (SponsorLink), FluentAssertions v8 (paid), **Tmds.DBus (CVE-2026-39959)**, ImageSharp, System.CommandLine.
- Input gated: confirm-by-default · key allowlist · kill-switch · action/step caps · dry-run. **Goal trusted, everything on screen UNTRUSTED.**
- Any future `Agents.Api` MUST be localhost-bound + token-authed — it can drive the real desktop.
