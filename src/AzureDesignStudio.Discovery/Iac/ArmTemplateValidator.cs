using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Discovery.Iac;

public enum ArmDiagnosticSeverity
{
    /// <summary>Deploy would fail; fix before showing/approving the plan.</summary>
    Error,
    Warning,
}

/// <summary>One validation finding; codes are stable so UI and tests can switch on them.</summary>
public sealed record ArmDiagnostic(ArmDiagnosticSeverity Severity, string Code, string Message);

/// <summary>
/// In-process validate-before-show for generated ARM templates (v2 plan 10.3): the checks
/// that need no round-trip - unresolved dependency graphs and cycles, undeclared parameter
/// or variable references, duplicate resource identities, missing api-versions, and the
/// hard rule that a <c>secureString</c>/<c>secureObject</c> parameter must never carry a
/// literal default. ARM's own validation (quotas, regions, provider rules) stays what-if/deploy-time;
/// this is the fast gate before anyone sees a plan.
/// </summary>
public static partial class ArmTemplateValidator
{
    [GeneratedRegex(@"parameters\(\s*'([^']+)'\s*\)")]
    private static partial Regex ParameterRef();

    [GeneratedRegex(@"variables\(\s*'([^']+)'\s*\)")]
    private static partial Regex VariableRef();

    public static IReadOnlyList<ArmDiagnostic> Validate(string templateJson)
    {
        var diagnostics = new List<ArmDiagnostic>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(templateJson);
        }
        catch (JsonException e)
        {
            return [new ArmDiagnostic(ArmDiagnosticSeverity.Error, "json", $"Template is not valid JSON: {e.Message}")];
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [new ArmDiagnostic(ArmDiagnosticSeverity.Error, "json", "Template root must be a JSON object")];

            var root = doc.RootElement;
            if (!root.TryGetProperty("contentVersion", out _))
                diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Warning, "content_version", "Missing contentVersion"));

            var parameters = ChildNames(root, "parameters");
            var secureParameters = ChildProperties(root, "parameters")
                .Where(p => p.Value.TryGetProperty("type", out var t) &&
                            t.GetString() is "secureString" or "secureObject" or "securestring" or "secureobject")
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var variables = ChildNames(root, "variables");

            if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() == 0)
            {
                diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "resources", "Template declares no resources"));
                return diagnostics;
            }

            // A resource's identity is its type plus its name: ARM allows a storage account and a
            // virtual network to share a name. Dependencies are keyed by identity, "type|name".
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identitiesByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var declared = new List<(string Identity, string Name, List<string> DependsOn)>();
            foreach (var resource in resources.EnumerateArray())
            {
                var name = Str(resource, "name");
                var type = Str(resource, "type");
                var label = string.IsNullOrEmpty(name) ? type : name;

                if (string.IsNullOrEmpty(name))
                    diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "resource.name", "Resource without a name"));
                if (string.IsNullOrEmpty(type))
                    diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "resource.type", $"Resource '{label}' has no type"));
                else if (!type.Contains('/'))
                    diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "resource.type", $"Resource '{label}' type '{type}' is not a provider/type pair"));
                if (string.IsNullOrEmpty(Str(resource, "apiVersion")))
                    diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "resource.api_version", $"Resource '{label}' has no apiVersion"));

                if (string.IsNullOrEmpty(name))
                    continue;
                var identity = $"{type}|{name}";
                if (!identities.Add(identity))
                    diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "resource.duplicate", $"Duplicate resource '{name}' of type '{type}'"));
                if (!identitiesByName.TryGetValue(name, out var sameName))
                    identitiesByName[name] = sameName = [];
                sameName.Add(identity);

                var dependsOn = resource.TryGetProperty("dependsOn", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : [];
                declared.Add((identity, name, dependsOn));
            }

            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (identity, name, dependsOn) in declared)
            {
                labels.TryAdd(identity, name);
                if (!dependencies.TryGetValue(identity, out var resolved))
                    dependencies[identity] = resolved = [];
                foreach (var dep in dependsOn)
                {
                    if (ResolveDependency(dep, identities, identitiesByName) is { } target)
                        resolved.Add(target);
                    else if (IsCheckable(dep))
                        diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "dependency.unknown", $"'{name}' dependsOn '{dep}', which is not in the template"));
                }
            }

            var cycle = FindCycle(dependencies);
            if (cycle is not null)
                diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "dependency.cycle",
                    $"dependsOn cycle: {string.Join(" -> ", cycle.Select(c => labels.GetValueOrDefault(c, c)))}"));

            var referencedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in EnumerateStrings(root))
            {
                if (text.Length < 2 || text[0] != '[' || text.StartsWith("[[", StringComparison.Ordinal)) continue; // ARM literals escape
                foreach (Match m in ParameterRef().Matches(text))
                    referencedParameters.Add(m.Groups[1].Value);
                foreach (Match m in VariableRef().Matches(text))
                    referencedVariables.Add(m.Groups[1].Value);
            }

            foreach (var reference in referencedParameters.Where(p => !parameters.Contains(p)))
                diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "expression.parameter", $"parameters('{reference}') is not declared"));
            foreach (var reference in referencedVariables.Where(v => !variables.Contains(v)))
                diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "expression.variable", $"variables('{reference}') is not declared"));
            foreach (var unused in parameters.Where(p => !referencedParameters.Contains(p) && !secureParameters.Contains(p)))
                diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Warning, "parameter.unused", $"Parameter '{unused}' is never referenced"));


            foreach (var parameter in ChildProperties(root, "parameters"))
                if (secureParameters.Contains(parameter.Name) && parameter.Value.TryGetProperty("defaultValue", out _))
                    diagnostics.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "parameter.secret_default",
                        $"Secure parameter '{parameter.Name}' carries a literal default - secrets come from Key Vault references, never plaintext"));
        }

        return diagnostics;
    }

    [GeneratedRegex(@"^\[\s*resourceId\((?<args>.*)\)\s*\]$")]
    private static partial Regex ResourceIdCall();

    [GeneratedRegex(@"^\s*'(?<value>(?:[^']|'')*)'\s*$")]
    private static partial Regex QuotedLiteral();

    /// <summary>
    /// A dependsOn entry is a resource name, or a <c>[resourceId('type', 'name', ...)]</c>
    /// expression (the generator always writes the latter; an optional subscription and
    /// resource-group argument may lead). Returns the matched identity, or null.
    /// </summary>
    private static string? ResolveDependency(string dependency, HashSet<string> identities, Dictionary<string, List<string>> identitiesByName)
    {
        if (!dependency.StartsWith('['))
            return identitiesByName.TryGetValue(dependency, out var matches) && matches.Count == 1 ? matches[0] : null;

        if (LiteralResourceIdArguments(dependency) is not { } args)
            return null;
        var typeIndex = args.FindIndex(IsResourceType);
        if (typeIndex < 0 || typeIndex == args.Count - 1)
            return null;
        var identity = $"{args[typeIndex]}|{string.Join('/', args.Skip(typeIndex + 1))}";
        return identities.Contains(identity) ? identity : null;
    }

    /// <summary>"Microsoft.Network/virtualNetworks": a dotted namespace, then a slash.</summary>
    private static bool IsResourceType(string argument)
    {
        var slash = argument.IndexOf('/');
        var dot = argument.IndexOf('.');
        return slash > 0 && dot >= 0 && dot < slash;
    }

    /// <summary>
    /// Whether a miss is a real error: bare names and all-literal resourceId() calls can be
    /// checked here; anything built from parameters or other functions is left to ARM.
    /// </summary>
    private static bool IsCheckable(string dependency) =>
        !dependency.StartsWith('[') || LiteralResourceIdArguments(dependency) is not null;

    private static List<string>? LiteralResourceIdArguments(string expression)
    {
        var call = ResourceIdCall().Match(expression.Trim());
        if (!call.Success)
            return null;
        var args = new List<string>();
        foreach (var part in SplitArguments(call.Groups["args"].Value))
        {
            var literal = QuotedLiteral().Match(part);
            if (!literal.Success)
                return null;
            args.Add(literal.Groups["value"].Value.Replace("''", "'"));
        }
        return args;
    }

    /// <summary>Splits top-level comma-separated arguments, honouring quotes and nested parentheses.</summary>
    private static IEnumerable<string> SplitArguments(string text)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
                quoted = !quoted;
            else if (quoted)
                continue;
            else if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    foreach (var text in EnumerateStrings(property.Value))
                        yield return text;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var text in EnumerateStrings(item))
                        yield return text;
                break;
            case JsonValueKind.String when element.GetString() is { } s:
                yield return s;
                break;
        }
    }

    private static IEnumerable<string> ChildNames(JsonElement root, string property) =>
        ChildProperties(root, property).Select(p => p.Name);

    private static IEnumerable<JsonProperty> ChildProperties(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject()
            : [];

    private static string Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>DFS over dependsOn; returns the cycle path when found.</summary>
    private static List<string>? FindCycle(Dictionary<string, List<string>> dependencies)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unvisited 1=stack 2=done
        var path = new List<string>();
        return Visit();

        List<string>? Visit()
        {
            foreach (var node in dependencies.Keys)
                if (state.GetValueOrDefault(node) == 0 && Walk(node) is { } found)
                    return found;
            return null;
        }

        List<string>? Walk(string node)
        {
            state[node] = 1;
            path.Add(node);
            foreach (var next in dependencies.GetValueOrDefault(node) ?? [])
            {
                if (!dependencies.ContainsKey(next)) continue;
                if (state.GetValueOrDefault(next) == 1)
                {
                    path.Add(next);
                    return path.Skip(path.IndexOf(next)).ToList();
                }
                if (state.GetValueOrDefault(next) == 0 && Walk(next) is { } found)
                    return found;
            }
            path.RemoveAt(path.Count - 1);
            state[node] = 2;
            return null;
        }
    }

    /// <summary>
    /// Pre-deploy gate for hosts (Server's CreateDeployment calls this before any Azure
    /// round-trip): errors block, warnings never do. The message lists findings by stable
    /// code for the client to show; parameter payloads are checked for parseability only and
    /// their content (which may hold secrets) is never echoed.
    /// </summary>
    public static bool TryFindBlockingErrors(string templateJson, string? parametersJson, out string message)
    {
        var errors = Validate(templateJson).Where(d => d.Severity == ArmDiagnosticSeverity.Error).ToList();
        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            try
            {
                JsonDocument.Parse(parametersJson).Dispose();
            }
            catch (JsonException)
            {
                errors.Add(new ArmDiagnostic(ArmDiagnosticSeverity.Error, "parameters.json",
                    "Deployment parameters are not valid JSON."));
            }
        }

        message = string.Join(Environment.NewLine, errors.Select(e => $"{e.Code}: {e.Message}"));
        return errors.Count > 0;
    }
}
