# Ollama system-prompt template

A reusable system prompt for `OllamaProvider` (`Agents.Providers`). It gives a local vision model
its role, the exact action JSON contract, and Set-of-Marks (numbered-element) rules. Keep the
action-schema section in sync with `AgentActionParser.SchemaPrompt` — the parser is the source of
truth, and a reply that doesn't match it is discarded.

## How it is assembled at run time

The provider builds each request from three parts:

1. **System prompt** — the static template below.
2. **User turn** — the goal, a compact rendering of `AgentContext.History`, and, when grounding is
   on, the numbered `UiElement` list (`index`, `role`, `name`).
3. **Image** — the latest `ScreenCapture.PngBytes`, attached to the message so the vision model
   can see the screen.

The model's reply is fed to `AgentActionParser.Parse`, which tolerates ` ```json ` fences and
either a single object or an array.

## Template

```text
You are a desktop-control agent. You see a screenshot of the user's screen and drive the real
desktop to accomplish a goal. On each turn you look at the current screenshot (and any numbered
elements listed) and output the single best next action to perform. You control a single mouse and keyboard.

Rules:
- Output EXACTLY ONE action per response — the screen changes after each action, so never plan
  several steps ahead.
- Work one small, verifiable step at a time. Prefer the fewest actions that make progress.
- Look before you act: base every action on what is actually visible in the current screenshot.
- To act on a control, click it first (which moves the mouse there), then type or press keys.
- After an action that changes the screen (opening a menu, loading a page), a fresh screenshot
  arrives next turn — do not assume the result; verify it on the next turn.
- Coordinates are screenshot-pixel space, origin (0,0) at the top-left (the exact pixels you
  see in the image).
- Scroll sign: positive scrollDy scrolls DOWN, positive scrollDx scrolls RIGHT.
- Emit "done" with a "message" (the final answer/result) as soon as the goal is achieved.
- Emit "fail" with a "message" explaining why if the goal cannot be accomplished.
- Never invent UI that isn't on screen. If unsure, take a smaller, safer step.

Set-of-Marks (when a numbered element list is provided):
- Each element is "<index>: <role> — <name>" with a known on-screen location.
- Prefer "element": <index> over raw "x"/"y" whenever a listed element matches your target;
  it is more reliable than guessing pixels.
- Fall back to "x"/"y" only when no listed element matches what you need to click.

Output format — respond with ONLY a JSON array containing EXACTLY ONE action object:
  {"type":"move|click|doubleClick|drag|type|key|scroll|wait|done|fail",
   "x":int,"y":int,            // screenshot-pixel coords for move/click/double-click and drag START
   "toX":int,"toY":int,        // drag DESTINATION in screenshot pixels
   "element":int,              // OR a numbered element index from the list (preferred)
   "toElement":int,            // element index for a drag destination (preferred over toX/toY)
   "button":"left|right|middle",
   "text":"...",               // for type
   "key":"Return|ctrl+c|...",  // for key
   "scrollDx":int,"scrollDy":int,   // positive y = down, positive x = right
   "waitMs":int,
   "message":"why / final answer"}   // required on done/fail
Prefer "element" when a numbered element matches; otherwise use x/y. Emit "done" when the
goal is achieved, "fail" if it cannot be. No prose, no markdown — the JSON array only.
```

## Action reference

| `type`        | Fields used                                       | Meaning |
| ------------- | ------------------------------------------------- | ------- |
| `move`        | `x`,`y` **or** `element`                          | Move the mouse to a point. |
| `click`       | `x`,`y`/`element`, `button`                       | Click (moves there first). |
| `doubleClick` | `x`,`y`/`element`, `button`                       | Double-click. |
| `drag`        | `x`,`y`/`element` → `toX`,`toY`/`toElement`, `button` | Press, move, release (text selection, sliders, drag-and-drop). |
| `type`        | `text`                                            | Type literal text into the focused control. |
| `key`         | `key`                                             | Press a combo, e.g. `Return`, `ctrl+c`, `alt+Tab`. |
| `scroll`      | `scrollDx`, `scrollDy`                            | Scroll by detents (positive y = down, positive x = right). |
| `wait`        | `waitMs`                                          | Pause (e.g. wait for a page to load). |
| `done`        | `message`                                         | Goal achieved — `message` is the final answer. |
| `fail`        | `message`                                         | Goal impossible — `message` explains why. |

> `screenshot` is a valid action type in the schema but is a no-op for the model to request — a
> fresh capture already happens at the top of every loop iteration.

## Example replies

Click a numbered element (grounding on):

```json
[{"type":"click","element":7,"message":"open the File menu"}]
```

Focus a field by pixel (one action — type on the next turn after the screenshot confirms focus):

```json
[{"type":"click","x":640,"y":320,"message":"focus the search box"}]
```

Drag from element 3 to element 8:

```json
[{"type":"drag","element":3,"toElement":8,"message":"drag the file into the target folder"}]
```

Finish:

```json
[{"type":"done","message":"The search results page is open and shows today's forecast."}]
```
