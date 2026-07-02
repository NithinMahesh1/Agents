# Architecture

`Agents` is a model-agnostic computer-use tool. Everything hangs off two small interfaces plus
one orchestrator, all defined in **`Agents.Core`** (which has **no third-party dependencies**).
Providers and drivers implement the interfaces; the CLI wires them together.

## The core abstractions

### `IModelProvider` — the brain

```csharp
public interface IModelProvider
{
    string Name { get; }  // e.g. "ollama:qwen2.5-vl", "claude", "openai"
    Task<IReadOnlyList<AgentAction>> DecideAsync(AgentContext context, CancellationToken ct = default);
}
```

Given an `AgentContext` (goal + latest screenshot + known UI elements + step history), the
provider returns **one or more** `AgentAction`s to perform next. This is the only thing a model
integration has to implement.

### `IDesktopDriver` — the hands & eyes

```csharp
public interface IDesktopDriver
{
    string Platform { get; }                 // "linux-wayland", "windows", "macos"
    ScreenInfo GetScreenInfo();
    Task<ScreenCapture> CaptureAsync(CancellationToken ct = default);
    Task MoveMouseAsync(int x, int y, CancellationToken ct = default);
    Task ClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task DoubleClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task TypeTextAsync(string text, CancellationToken ct = default);
    Task KeyPressAsync(string keyCombo, CancellationToken ct = default);  // "Return", "ctrl+c", "alt+Tab"
    Task ScrollAsync(int dx, int dy, CancellationToken ct = default);
}
```

Captures the screen and injects real input. Coordinates are **absolute pixels**. One concrete
driver exists per platform; the loop never knows which one it holds.

### `IGroundingProvider` — optional Set-of-Marks source

```csharp
public interface IGroundingProvider
{
    Task<IReadOnlyList<UiElement>> GetElementsAsync(CancellationToken ct = default);
}
```

Supplies numbered on-screen elements (from an accessibility tree) so the model can say "click
element 7" instead of guessing pixel coordinates. **Optional** — the loop runs fine without it
(raw-coordinate mode) and passes an empty element list.

## The data model

| Type            | Purpose |
| --------------- | ------- |
| `AgentActionType` | `Move`, `Click`, `DoubleClick`, `Type`, `Key`, `Scroll`, `Wait`, `Screenshot`, `Done`, `Fail`. |
| `MouseButton`     | `Left`, `Right`, `Middle`. |
| `AgentAction`     | One normalized action (record). Fields not relevant to a type stay `null`. |
| `ScreenInfo`      | `Width`, `Height`, `Scale` (HiDPI factor; 1.0 = 100%). |
| `ScreenCapture`   | `PngBytes` + the `ScreenInfo` they were taken at. |
| `UiElement`       | `Index`, `Role`, `Name`, `X`, `Y`, `Width`, `Height`; `.Center` gives the click point. |
| `AgentStep`       | One executed `AgentAction` + its string `Result` (loop history entry). |
| `AgentContext`    | `Goal` + `ScreenCapture` + `Elements` + `History` — everything the model sees. |

### `AgentAction` fields

- `Type` (**required**) — the `AgentActionType`.
- `X`, `Y` — absolute pixel target for `Move`/`Click`. **Ignored when `Element` is set.**
- `Button` — mouse button (default `Left`).
- `Text` — text to type (for `Type`).
- `Key` — key combo like `"Return"`, `"ctrl+c"`, `"alt+Tab"` (for `Key`).
- `ScrollDx`, `ScrollDy` — scroll deltas (for `Scroll`).
- `Element` — Set-of-Marks element index; the loop resolves it to coordinates, overriding `X`/`Y`.
- `WaitMs` — pause duration (for `Wait`).
- `Message` — rationale for the step, or the final answer/reason on `Done`/`Fail`.

### The action JSON schema

Models emit actions as JSON. `AgentActionParser.SchemaPrompt` is the canonical description that
providers embed in their prompt; `AgentActionParser.Parse` turns the model's reply back into
`AgentAction`s. The schema, in brief:

> Respond with **only** a JSON array of one or more action objects:
> ```json
> {"type":"move|click|doubleClick|type|key|scroll|wait|done|fail",
>  "x":int,"y":int,            // pixel coordinates for move/click
>  "element":int,              // OR a numbered element index (preferred)
>  "button":"left|right|middle",
>  "text":"...",               // for type
>  "key":"Return|ctrl+c|...",  // for key
>  "scrollDx":int,"scrollDy":int,
>  "waitMs":int,
>  "message":"why / final answer"}   // required on done/fail
> ```
> Prefer `element` when a numbered element matches; otherwise use `x`/`y`. Emit `done` when the
> goal is achieved, `fail` if it cannot be. No prose, no markdown — the JSON array only.

The parser is deliberately **tolerant**: it strips ` ```json ` fences and surrounding prose, and
accepts either a single object or an array. Anything it can't parse yields an **empty list**
(no exception), so a malformed model reply simply produces no action rather than crashing the run.

## `AgentLoop` lifecycle

`AgentLoop.RunAsync(goal, ct)` drives the cycle:

1. **Capture** — `driver.CaptureAsync()` grabs a fresh screenshot.
2. **Ground** — if an `IGroundingProvider` is present, fetch `UiElement`s; otherwise use `[]`.
3. **Decide** — build an `AgentContext` (goal + screen + elements + history) and call
   `model.DecideAsync()`.
4. **Execute** — for each returned action, in order:
   - `Done` / `Fail` → return immediately with outcome + message.
   - Run the **safety gate** (`ConfirmAction`); a veto aborts the run.
   - Resolve the target: if `Element` is set, map it to that element's `.Center`; else use `X`/`Y`.
   - Dispatch to the driver (`Move`/`Click`/`DoubleClick`/`Type`/`Key`/`Scroll`/`Wait`/`Screenshot`),
     record an `AgentStep`, then pause `StepDelayMs`.
5. **Repeat** until `Done`/`Fail`, cancellation, or `MaxSteps`.

`RunAsync` returns an **`AgentRunResult`** — an `AgentRunOutcome`
(`Completed` / `Failed` / `MaxStepsReached` / `Aborted`), a `Message`, and the full step `History`.

### `AgentLoopOptions` — the safety gate

| Option          | Default | Effect |
| --------------- | ------- | ------ |
| `MaxSteps`      | `25`    | Hard cap on iterations before the loop aborts with `MaxStepsReached`. |
| `DryRun`        | `false` | Actions are **logged but never executed** — the driver's input methods are not called. |
| `ConfirmAction` | `null`  | `Func<AgentAction,bool>` called before each action; return `false` to **veto** it (kill-switch / interactive confirm) → run ends `Aborted`. |
| `StepDelayMs`   | `400`   | Pause after each action so the UI can settle before the next screenshot. |
| `Log`           | `null`  | `Action<string>` sink for progress/log lines (keeps Core dependency-free). |

## Driver matrix

| Platform            | Status   | Input                                  | Capture |
| ------------------- | -------- | -------------------------------------- | ------- |
| **Linux / Wayland** | **Real** | `ydotool` via subprocess (uinput)      | XDG **Screenshot portal**, driven with the **`gdbus` CLI** |
| Windows             | Skeleton | —                                      | — |
| macOS               | Skeleton | —                                      | — |

**Why `gdbus`, not a D-Bus library:** the natural choice, **Tmds.DBus**, was **dropped over
CVE-2026-39959** (CVSS 7.1 — signal spoofing / fd exhaustion / DoS; fixed in 0.92.0). Rather than
take a fresh dependency, the driver shells out to the already-present `gdbus` CLI to drive the
portal (`org.freedesktop.portal.Screenshot`), which is async (Request → Response signal).
`org.gnome.Shell.Screenshot` is **not** an option — it returns `AccessDenied` on GNOME 50.

## Provider matrix

| Provider   | Status              | Transport |
| ---------- | ------------------- | --------- |
| **Ollama** | **Real**            | Built-in `HttpClient` → `POST /api/chat` (vision model, image + prompt). |
| Claude     | Stub (phase 2)      | Same `IModelProvider` interface. |
| OpenAI     | Stub (phase 2)      | Same `IModelProvider` interface. |

## Grounding

Grounding is **deferred for the MVP** — the loop ships in raw-coordinate mode (the model reads
the screenshot and returns pixel `X`/`Y`). The planned upgrade is a **Set-of-Marks** overlay
sourced from the **AT-SPI accessibility tree**, exposed through `IGroundingProvider` as numbered
`UiElement`s. To keep clear of the dropped Tmds.DBus dependency, that tree will be read by a small
**Python AT-SPI helper subprocess** rather than an in-process D-Bus binding.

## Dependency & security posture

- Core has **zero third-party dependencies**; the only net-new NuGet for the MVP is
  **`System.CommandLine`** (Microsoft, for the CLI). Input and capture are done via **subprocess**
  (`ydotool`, `gdbus`); Ollama is called with the built-in `HttpClient`.
- **Avoided** packages: Moq (SponsorLink phone-home), FluentAssertions v8 (now paid),
  Tmds.DBus (CVE-2026-39959), ImageSharp (split license, and no imaging lib is needed).
- If an `Agents.Api` HTTP layer is ever added it **must** be **localhost-bound + token-authed** —
  it can drive the real desktop.
