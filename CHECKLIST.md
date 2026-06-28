# Agents — Project Checklist

Model-agnostic desktop-control ("computer use") tool: let any model (Ollama / Claude /
ChatGPT) see the screen and drive the real desktop. **C# / .NET 10. Wayland-first, Ollama-first.**

> Deeper design rationale: approved plan at `~/.claude/plans/enumerated-weaving-blossom.md`.
> Legend: `[x]` done · `[~]` in progress · `[ ]` todo

## Decisions locked
- **Language/stack:** C# / .NET 10, Central Package Management, layered `src/` + `tests/` (mirrors MyGarageTracker).
- **Architecture:** `IModelProvider` (brain) + `IDesktopDriver` (hands & eyes), decoupled through `Agents.Core`. CLI and any future API are thin front-ends over `AgentLoop`.
- **Target box:** Fedora 44, GNOME on **Wayland**. Input via `ydotool` (uinput). Capture via the XDG desktop portal.
- **First model:** Ollama (local). Claude / OpenAI behind the same interface later.
- **API layer:** CLI first; a FastEndpoints `Agents.Api` (localhost-bound + token auth) can bolt on later — not now.
- **Testing:** xUnit + Shouldly + **NSubstitute** (not Moq — SponsorLink history).
- **Dependency rule:** prefer official + popular + clean-licensed; minimize third-party; security-check every package before it's added (build step 0); **never install/fetch anything without explicit approval**.

## Done
- [x] Repo confirmed, plan approved, dependency set security-vetted (2026-06-27).
- [x] Scaffolded .NET 10 solution — `Agents.slnx` + 6 projects: Core, Desktop, Providers, Grounding, Cli, Tests.
- [x] Central Package Management (`Directory.Packages.props`) + `.gitignore`.
- [x] Test stack on CPM: xUnit 2.9.3, runner 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, NSubstitute 5.3.0, Shouldly 4.3.0.
- [x] **`Agents.Core` contracts (LOCKED API):** `AgentAction` / `AgentActionType` / `MouseButton`; `ScreenCapture` / `ScreenInfo`; `UiElement`; `AgentContext` / `AgentStep`; `IDesktopDriver`, `IModelProvider`, `IGroundingProvider`; `AgentLoop` + `AgentLoopOptions` (safety gate: max-steps, dry-run, confirm/kill-switch); `AgentActionParser` (JSON → actions + schema prompt). **Builds green, 0 warnings.**

## Open decision — screen capture (Tmds.DBus dropped)
Dropped **Tmds.DBus** over **CVE-2026-39959** (CVSS 7.1, signal spoofing / fd exhaustion / DoS; fixed in 0.92.0). Probe on this box:
- `gdbus` present ✓
- `org.gnome.Shell.Screenshot` → **AccessDenied** (restricted on GNOME 50) ✗
- XDG **Screenshot portal present** (`org.freedesktop.portal.Screenshot`) ✓ — but async (Request → Response signal)
- `gnome-screenshot` / `grim` / `spectacle` → not installed
- [ ] **Decide capture path** — leaning: drive the Screenshot portal via the `gdbus` CLI (no NuGet dep, fiddly async). Alternatives: install a small screenshot helper, or PipeWire/ScreenCast.

## Next — implementation fan-out (6 agents, one project each, against Core)
- [ ] **Agents.Desktop** — `LinuxWaylandDriver` (ydotool input + chosen capture path), Windows/macOS skeletons, `DriverFactory`.
- [ ] **Agents.Providers** — `OllamaProvider` (`HttpClient` → `/api/chat`); Claude / OpenAI stubs.
- [ ] **Agents.Grounding** — deferred for MVP (raw-coordinate mode); later a Python AT-SPI helper subprocess (no Tmds.DBus).
- [ ] **Agents.Cli** — `driver-probe`, `run "<goal>"`, `--dry-run`.
- [ ] **Agents.Tests** — parser / loop / safety / driver-arg tests (NSubstitute).
- [ ] **docs** — README, ARCHITECTURE, Ollama prompt template.
- [ ] **security-agent** review + integration/verify pass (after the six).

## Prereqs (explicit approval required before each)
- [ ] Add `System.CommandLine` 2.0.9 to CPM (only new MVP NuGet now Tmds.DBus is gone).
- [ ] Install `ydotool` + enable `ydotoold` + uinput permission.
- [ ] Upgrade Ollama **0.21.0 → 0.30.x** (not vulnerable to known CVEs, but stale on vision-model support).
- [ ] Pull a vision model (`qwen2.5-vl` or `llama3.2-vision`).

## Cleanup
- [ ] Remove template boilerplate (`Class1.cs` ×4, `UnitTest1.cs`, default `Program.cs`).

## Security notes
- Pins are CVE-free as of 2026-06-27. **Avoided:** Moq (SponsorLink phone-home), FluentAssertions v8 (now paid), Tmds.DBus (CVE-2026-39959), ImageSharp (split license; not needed — no imaging lib in MVP).
- Net new third-party for the MVP: **System.CommandLine** (Microsoft official) only. Input & capture via subprocess; Ollama via built-in `HttpClient`.
- If an `Agents.Api` HTTP layer is ever added it MUST be localhost-bound + token-authed — it can drive the real desktop.
