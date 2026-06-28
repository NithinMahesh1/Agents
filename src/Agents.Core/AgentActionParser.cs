using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agents.Core;

/// <summary>
/// Parses a model's JSON action output into <see cref="AgentAction"/>s, and supplies the
/// schema text providers embed in the prompt. Tolerant of fenced ```json blocks and of a
/// response that is either a single object or an array.
/// </summary>
public static class AgentActionParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Description of the action JSON the model must emit (embed this in the prompt).</summary>
    public const string SchemaPrompt = """
        Respond with ONLY a JSON array of one or more actions. Each action is an object:
          {"type":"move|click|doubleClick|type|key|scroll|wait|done|fail",
           "x":int,"y":int,            // pixel coordinates for move/click
           "element":int,              // OR a numbered element index from the list (preferred)
           "button":"left|right|middle",
           "text":"...",               // for type
           "key":"Return|ctrl+c|...",  // for key
           "scrollDx":int,"scrollDy":int,
           "waitMs":int,
           "message":"why / final answer"}   // required on done/fail
        Prefer "element" when a numbered element matches; otherwise use x/y. Emit "done" when the
        goal is achieved, "fail" if it cannot be. No prose, no markdown — the JSON array only.
        """;

    /// <summary>Parse model output into actions. Returns an empty list if nothing valid is found.</summary>
    public static IReadOnlyList<AgentAction> Parse(string modelOutput)
    {
        var json = Extract(modelOutput);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            if (json.TrimStart().StartsWith('['))
                return JsonSerializer.Deserialize<List<AgentAction>>(json, JsonOptions) ?? [];

            var single = JsonSerializer.Deserialize<AgentAction>(json, JsonOptions);
            return single is null ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Strip ```json fences / surrounding prose to isolate the JSON payload.</summary>
    private static string Extract(string s)
    {
        s = s.Trim();

        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = s.IndexOf('\n', fence);
            var end = s.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start >= 0 && end > start)
                s = s[(start + 1)..end].Trim();
        }

        var firstArr = s.IndexOf('[');
        var firstObj = s.IndexOf('{');
        var startIdx = (firstArr, firstObj) switch
        {
            ( >= 0, >= 0) => Math.Min(firstArr, firstObj),
            ( >= 0, _) => firstArr,
            (_, >= 0) => firstObj,
            _ => -1,
        };
        if (startIdx < 0)
            return string.Empty;

        var endIdx = Math.Max(s.LastIndexOf(']'), s.LastIndexOf('}'));
        return endIdx > startIdx ? s[startIdx..(endIdx + 1)] : string.Empty;
    }
}
