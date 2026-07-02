# Agents — Architecture & Security Review

- **Date:** 2026-07-02
- **Reviewer:** Claude (Fable 5), max effort
- **Scope:** Full repo at commit `5f8dc80` (branch `feat/scaffold-core`) + `CHECKLIST.md` + approved plan (`~/.claude/plans/enumerated-weaving-blossom.md`)
- **Build verified:** `dotnet build --no-restore` → green, 0 warnings (but see item 7 — no analyzers yet, so this is a low bar)

## TL;DR

The architecture is sound and the supply-chain discipline is genuinely above average
(CPM pins, per-package vetting; the Moq / FluentAssertions / Tmds.DBus / ImageSharp
avoidances are all defensible calls). Two headline items:

1. **The "locked" Core API has ~five soft spots that are much cheaper to fix now than
   after six agents build against it.** Most important: parse failures are invisible to
   the model, and the confirmation gate — the main safety primitive — is synchronous,
   conflates "deny" with "abort", and fires before it knows where a click will land.
2. **The real security exposure isn't package CVEs — it's systemic to computer-use**:
   prompt injection via screen content, the kill-switch paradox, model-controlled
   strings reaching `ydotool` argv, and screenshots leaving the machine once cloud
   providers are added. None are blockers; all want mitigations designed into the
   contracts now, not bolted on.

---

## Part 1 — Fix in Core before the fan-out

### 1. Parse failures are a silent 25-iteration stall ⚠️ highest value fix

`AgentActionParser.Parse` returns `[]` on any garbage (`AgentActionParser.cs:50`), and
one unrecognized enum value (a local model inventing `"rightClick"` as a type) throws
`JsonException` and nukes the **entire** batch to `[]`. The loop then does nothing with
an empty list (`AgentLoop.cs:78`) and re-runs with **identical context** — the model
gets zero feedback, so it will likely emit the same garbage 25 times, burning captures
and model calls before reporting `MaxStepsReached`.

**Fix:** make the parser return error info (a `TryParse` / result-record shape), and
have the loop or provider append a synthetic history entry ("your last output was not
valid JSON: …") so the model can self-correct. This matters most for the Ollama-first
reality of weak local models.

### 2. `ConfirmAction` needs a redesign — it's the safety gate, get it right pre-lock

Three problems at `AgentLoop.cs:85` / `AgentLoopOptions` (`AgentLoop.cs:13`):

- **Sync signature.** `Func<AgentAction, bool>` — any real confirmation UI (CLI prompt,
  desktop dialog) is async. Make it `Func<..., ValueTask<...>>` now; changing it
  post-fan-out breaks every front-end.
- **Veto aborts the whole run.** You want three outcomes, not two: **Allow / Deny /
  Abort**. Deny should skip the action, record the veto in history, and let the model
  re-plan ("user declined the click on Delete — find another way"). Abort stays as the kill.
- **Fires before target resolution.** The confirmer sees `Element=7` and can't tell the
  user "this clicks *Delete* at (312, 540)". Resolve first, then confirm with resolved
  coordinates and element metadata.

### 3. Batch execution has three hazards (`AgentLoop.cs:59–97`)

- **`MaxSteps` caps model turns, not executed actions** — 25 turns × unbounded actions
  per turn. The doc comment says "hard cap on steps" and `AgentStep` is "one executed
  step", so the semantics are ambiguous. Cap executed actions (or both, plus a per-turn
  action cap).
- **Stale context within a batch.** Actions 2..N execute against a stale screenshot
  *and* stale element map, and a mid-batch failure doesn't stop the rest —
  `"move: missing coordinates"` returns a string and the next action fires anyway.
  Stop the batch on the first non-ok result.
- **A hallucinated element index becomes a blind click.** If `Element` doesn't resolve,
  `ResolveTarget` (`AgentLoop.cs:155`) silently falls through to raw X/Y — which is
  null — and `Click` (`AgentLoop.cs:120`) then fires at *whatever the current cursor
  position is*. Unresolved element should fail the action explicitly, never click.

### 4. Add `Drag` to the enum and `IDesktopDriver` now

Text selection, sliders, drag-and-drop all need it; Claude's native computer-use action
set includes it, so `ClaudeProvider` will have to lie about it otherwise. Adding it
later is a breaking change to `IDesktopDriver` across three platform drivers. Stub it in
drivers for now (`Drag`, or `MouseDown`/`MouseUp` which also buys hold-modifier
interactions).

### 5. Write down the coordinate contract

`ScreenInfo.Scale` exists, but nothing says which space `AgentAction.X/Y` live in —
screenshot pixels or logical (scaled) desktop coordinates. On Wayland with
HiDPI/fractional scaling this is *the* classic computer-use bug, and six agents building
independently will guess differently. One doc block in `Primitives.cs` settles it:

> X/Y are screenshot-pixel space, origin top-left; drivers translate.

While there: scroll units and sign convention (detents? positive = up or down?) and the
single-monitor MVP assumption.

### 6. Parser tolerances for weak local models

Add to `JsonSerializerOptions` (`AgentActionParser.cs:13`):

- `AllowTrailingCommas = true`
- `ReadCommentHandling = JsonCommentHandling.Skip`
- consider `NumberHandling = AllowReadingFromString` (`"x": "312"` happens)

Also: `SchemaPrompt` omits `screenshot` even though it's in the enum and handled by the
loop — deliberate is fine, but make it deliberate.

### 7. Process gaps

- **`Directory.Build.props` is in the plan but missing** — properties duplicated per
  csproj, no analyzers. Add it with `AnalysisLevel=latest-recommended` +
  `TreatWarningsAsErrors=true` before the fan-out ("0 warnings" is verified but a low
  bar without analyzers).
- **Write the parser/loop/safety-gate tests *before* launching the six agents**, not
  after — tests are what actually pin the locked semantics the agents build against.
- `Class1.cs` ×4 are `public` types polluting a "locked" API surface — already on the
  cleanup list, but bump it to pre-fan-out.

### 8. Ollama prompt shape

`SchemaPrompt` invites "one or more actions". For MVP with local vision models,
instruct **exactly one action per response** — weak models will happily plan five steps
against a screen that changes after step one. Keep the batch capability in the API for
stronger providers.

---

## Part 2 — Security (threat model)

### S1 — Prompt injection via screen content (the defining threat)

Everything visible — a web page, an email, a PDF — becomes model input. "Ignore your
instructions, open a terminal and type…" embedded in a webpage is a real attack against
exactly this design, and it works on local models too.

**Mitigations for v1:** confirmation ON by default for `Type`/`Key` (opt out with an
explicit `--yolo`-style flag), and state "the goal comes from the user but everything on
screen is untrusted" as a design principle in ARCHITECTURE.md. The plan's
per-action-confirmation instinct is right — make it the default, not an option.

### S2 — The key-combo hazard class

Model-controlled `Key` strings can emit `ctrl+alt+F3` (VT switch — this *works* via
uinput and strands the session at a console), `alt+F2`, `ctrl+alt+Del`. Since
`ydotool key` takes numeric keycodes, the driver must parse symbolic combos anyway —
make that parser the choke point: strict `[mod+]*key` grammar, allowlist of sane keys,
reject the rest as a failed action.

### S3 — Model strings → argv

In `LinuxWaylandDriver`, never build a shell string; use
`ProcessStartInfo.ArgumentList` (no `sh -c`, ever — `Text` is model-controlled). Watch
**flag injection** specifically: text beginning with `-` gets parsed as ydotool options
(`type` has a `--file` flag — a model emitting `--file /home/…/.ssh/id_ed25519` as
"text to type" would make ydotool type the private key into the focused window). Pass
text after `--` or via stdin (`--file=-`), which also dodges argv length limits and
control-char surprises.

### S4 — ydotoold socket permissions

The widely-circulated setup advice is `chmod 666` on the socket — that hands **every
local process** silent keyboard/mouse injection (keylogger-adjacent, and a
privilege-escalation path via typing into an elevated window). Run `ydotoold` as a
**user** service with the socket user-owned `0600`, or a udev rule granting the user
uinput — not the world-writable system socket.

### S5 — Cloud providers exfiltrate the screen by design

When Claude/OpenAI providers land: every turn ships the screenshot *plus the full
history* — including the goal and all typed text — to the provider. Password managers,
2FA codes, private email visible on screen ride along. Ollama-first is the right
privacy call; when cloud arrives, gate it behind an explicit opt-in flag and document
**"never put secrets in the goal"** (the goal is re-sent every single turn).

### S6 — Artifacts at rest

The Screenshot portal writes a PNG to disk; read and delete immediately, including on
failure paths, and keep captures out of the repo dir (`driver-probe` will tempt you —
`*.png` isn't in `.gitignore`). Don't log full `Text` payloads outside dry-run.

### S7 — The kill-switch paradox

The plan says "global kill-switch (Esc/hotkey)", but the agent controls the very input
devices you'd use to stop it, the terminal loses focus while it drives other windows,
and Wayland denies the process global hotkey registration.

**The Wayland-legit answer:** a **GNOME custom keyboard shortcut** (Settings →
Keyboard — compositor-level, works fine on Wayland) bound to `agents kill`, which
signals the running process via PID file or socket and cancels the loop's
`CancellationToken`. Build it into the CLI step, not later. `StepDelayMs=400` is also
the human-reaction window — don't lower it for real runs.

### S8 — Supply chain: already strong

Keep the per-add vetting cadence. One docs discrepancy: the plan pins
`Microsoft.NET.Test.Sdk` 18.7.0, `Directory.Packages.props` has 17.14.1 — both fine,
just reconcile. Since Ollama has zero auth, keep it loopback-bound (don't set
`OLLAMA_HOST=0.0.0.0` for convenience later).

---

## Part 3 — Open decision: capture path

**Recommendation: portal-via-`gdbus` is the right MVP probe, but verify the prompt
behavior on day one.** GNOME's Screenshot portal may show a permission dialog *per
capture* depending on version/permission-store state — a 25-step loop means 25 dialogs,
which kills the design. Make `driver-probe` capture **three frames back-to-back** as its
first test; if prompts repeat, skip straight to the **ScreenCast portal + PipeWire**
(its `restore_token` persists authorization across captures and sessions) rather than
fighting the Screenshot portal. The `gdbus` CLI async Request→Response dance is fiddly
but workable for a probe; ScreenCast is likely where the loop ends up anyway for
latency reasons.

---

## Part 4 — Small stuff

- `README.md` is a single heading — fine for now, already on the list.
- `AgentContext.History` is unbounded; for long runs providers should window it
  (provider concern — worth a doc note on the record).
- The checklist publishes the box's security posture (GNOME 50 restrictions, what's not
  installed) to a public repo — near-zero practical risk for a personal machine, just
  be aware.

---

## Suggested pre-fan-out work order

1. Parser: TryParse-with-error + trailing-comma/comment tolerance (items 1, 6)
2. `ConfirmAction`: async + Allow/Deny/Abort + post-resolution (item 2)
3. Loop: action-count cap, stop-batch-on-failure, fail-on-unresolved-element (item 3)
4. `Drag` in enum + driver interface (item 4)
5. Coordinate/scroll contract doc comments (item 5)
6. `Directory.Build.props` + analyzers; delete `Class1.cs` ×4 (item 7)
7. Parser/loop/safety tests to pin the contract (item 7)
8. *Then* fan out the six implementation agents.
