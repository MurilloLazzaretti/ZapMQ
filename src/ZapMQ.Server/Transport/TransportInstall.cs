using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZapMQ.Server.Transport;

/// <summary>
/// The text of an application as it travels to be installed somewhere else: its configuration
/// with the secrets left blank, and the lines of the reverse proxy that are about it.
/// </summary>
public static partial class InstallText
{
    /// <summary>
    /// A name that says it holds a secret: a password, a key, a token. A connection string is not one:
    /// only the password inside it is left blank, the server and the database are worth keeping.
    /// </summary>
    [GeneratedRegex(@"(pass(word)?|pwd|senha|secret|segredo|token|api[-_]?key|private[-_]?key|access[-_]?key|client[-_]?secret|^key$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    /// <summary>
    /// A password inside a connection string, whatever holds it.
    /// </summary>
    [GeneratedRegex(@"(?<name>\b(password|pwd)\s*=\s*)(?<value>[^;""'<]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionPassword();

    /// <summary>
    /// A line <c>name = value</c> or <c>name: value</c> (ini, env, yml, properties).
    /// </summary>
    [GeneratedRegex(@"^(?<lead>\s*(?<name>[\w.\-]+)\s*[=:]\s*)(?<value>.*)$", RegexOptions.Multiline)]
    private static partial Regex Assignment();

    /// <summary>
    /// An attribute of a tag whose name says it holds a secret, or an <c>add key="…" value="…"</c>.
    /// </summary>
    [GeneratedRegex(@"(?<lead>\b(?<name>[\w\-]+)\s*=\s*"")(?<value>[^""]*)("")")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"(?<lead><add\b[^>]*\bkey\s*=\s*""(?<name>[^""]*)""[^>]*\bvalue\s*=\s*"")(?<value>[^""]*)("")", RegexOptions.IgnoreCase)]
    private static partial Regex AddKeyValue();

    /// <summary>
    /// The file with every secret it seems to hold left blank, and the names of what was blanked.
    /// </summary>
    public static (string Content, List<string> Blanked) Blank(string path, string content)
    {
        var blanked = new List<string>();
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var root = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (root is not null)
                {
                    BlankJson(root, "", blanked);
                    return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), blanked);
                }
            }
            catch (JsonException)
            {
                // Not JSON after all: it is treated as text, line by line.
            }
        }

        var text = AddKeyValue().Replace(content, match => SecretName().IsMatch(match.Groups["name"].Value) && match.Groups["value"].Length > 0
            ? Note(blanked, match.Groups["name"].Value, match.Groups["lead"].Value + "\"") : BlankConnection(match.Value, match.Groups["name"].Value, blanked));
        text = Attribute().Replace(text, match => SecretName().IsMatch(match.Groups["name"].Value) && !match.Groups["name"].Value.Equals("key", StringComparison.OrdinalIgnoreCase) && match.Groups["value"].Length > 0
            ? Note(blanked, match.Groups["name"].Value, match.Groups["lead"].Value + "\"") : BlankConnection(match.Value, match.Groups["name"].Value, blanked));
        if (!path.EndsWith(".config", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            text = Assignment().Replace(text, match => SecretName().IsMatch(match.Groups["name"].Value) && match.Groups["value"].Value.Trim().Length > 0
                ? Note(blanked, match.Groups["name"].Value, match.Groups["lead"].Value) : BlankConnection(match.Value, match.Groups["name"].Value, blanked));
        return (text, blanked);
    }

    private static string Note(List<string> blanked, string name, string replacement)
    {
        if (!blanked.Contains(name))
            blanked.Add(name);
        return replacement;
    }

    private static string BlankConnection(string text, string name, List<string> blanked) =>
        ConnectionPassword().Replace(text, match => match.Groups["value"].Length == 0 ? match.Value : Note(blanked, name + " (senha)", match.Groups["name"].Value));

    private static void BlankJson(JsonNode node, string path, List<string> blanked)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    var here = path.Length == 0 ? name : path + ":" + name;
                    if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text))
                    {
                        if (SecretName().IsMatch(name) && text.Length > 0)
                        {
                            obj[name] = "";
                            if (!blanked.Contains(here))
                                blanked.Add(here);
                        }
                        else if (ConnectionPassword().IsMatch(text))
                            obj[name] = BlankConnection(text, here, blanked);
                    }
                    else if (value is not null)
                        BlankJson(value, here, blanked);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    if (array[index] is { } item)
                        BlankJson(item, $"{path}:{index}", blanked);
                break;
        }
    }

    /// <summary>
    /// The blocks of an NGINX configuration that send requests to the given ports: the
    /// <c>upstream</c> that lists them and every <c>location</c> that uses it or them. Null when
    /// nothing there is about them.
    /// </summary>
    public static string? ProxyLines(IEnumerable<(string File, string Content)> files, IReadOnlyCollection<int> ports)
    {
        if (ports.Count == 0)
            return null;
        var all = files.Select(file => (file.File, Text: StripComments(file.Content))).ToList();
        bool Mentions(string text) => ports.Any(port => Regex.IsMatch(text, $@"(:|\s){port}\b"));

        var upstreams = new List<(string File, string Name, string Block)>();
        foreach (var (file, text) in all)
            foreach (var (head, block) in Blocks(text, "upstream"))
                if (Mentions(block) && head.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var name, ..])
                    upstreams.Add((file, name, block));

        var names = upstreams.Select(upstream => upstream.Name).ToHashSet(StringComparer.Ordinal);
        var locations = new List<(string File, string Block)>();
        foreach (var (file, text) in all)
            foreach (var (_, block) in Blocks(text, "location"))
                if (Regex.Match(block, @"proxy_pass\s+https?://(?<to>[^/;\s]+)") is { Success: true } pass && (names.Contains(pass.Groups["to"].Value) || Mentions(pass.Value)))
                    locations.Add((file, block));

        if (upstreams.Count == 0 && locations.Count == 0)
            return null;
        var parts = upstreams.Select(upstream => $"# {upstream.File}\n{upstream.Block}")
            .Concat(locations.Select(location => $"# {location.File}\n{location.Block}"));
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// The lines of the proxy with the ports of the other environment replaced, one for one, by
    /// the ones chosen here.
    /// </summary>
    public static string Adapt(string proxy, IReadOnlyList<int> from, IReadOnlyList<int> to)
    {
        var text = proxy;
        for (var index = 0; index < from.Count && index < to.Count; index++)
            text = Regex.Replace(text, $@"(?<=[:\s]){from[index]}\b", $"\u0001{index}\u0001");
        for (var index = 0; index < from.Count && index < to.Count; index++)
            text = text.Replace($"\u0001{index}\u0001", to[index].ToString(System.Globalization.CultureInfo.InvariantCulture));
        return text;
    }

    private static string StripComments(string text) => Regex.Replace(text, @"#[^\n]*", "");

    /// <summary>
    /// Each block that starts with the word, with what is between its braces, kept whole.
    /// </summary>
    private static IEnumerable<(string Head, string Block)> Blocks(string text, string word)
    {
        foreach (Match start in Regex.Matches(text, $@"(?<=^|[;{{}}\s]){word}\b[^{{;]*\{{"))
        {
            var depth = 0;
            for (var index = start.Index; index < text.Length; index++)
            {
                if (text[index] == '{')
                    depth++;
                else if (text[index] == '}' && --depth == 0)
                {
                    var block = text[start.Index..(index + 1)];
                    yield return (start.Value.TrimEnd('{').Trim(), Indent(block));
                    break;
                }
            }
        }
    }

    private static string Indent(string block) =>
        string.Join('\n', block.Replace("\r", "").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Trim().Length > 0));
}

public sealed partial class TransportService
{
    /// <summary>
    /// The kinds an environment that does not have the target can install from a package.
    /// </summary>
    public static readonly string[] Installable = ["worker", "service", "api"];

    /// <summary>
    /// A folder written by the Worker Control, as the path of a file inside it.
    /// </summary>
    private static string? Inside(string? file, string? folder)
    {
        if (file is null || folder is null)
            return null;
        var root = folder.TrimEnd('\\', '/');
        return file.Length > root.Length + 1 && file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && file[root.Length] is '\\' or '/'
            ? file[(root.Length + 1)..].Replace('\\', '/') : null;
    }

    /// <summary>
    /// What the target of an item is here, to go with the package: the program, how many
    /// processes or instances, the configuration without its secrets, and the lines of the proxy.
    /// Whatever cannot be told is left out; nothing here stops the package from being closed.
    /// </summary>
    private async Task<InstallInfo?> DescribeForInstall(PackageItem item, string by, CancellationToken cancellation)
    {
        if (!Installable.Contains(item.Kind))
            return null;
        try
        {
            var target = (await Ask("TransportTarget", by, request => { request["Kind"] = item.Kind; request["Name"] = item.Name; }, cancellation))["Target"]!;
            var defaults = await Ask("ApplicationDefaults", by, _ => { }, cancellation);
            var folder = (target["Paths"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
            var info = new InstallInfo();
            switch (item.Kind)
            {
                case "worker":
                {
                    var group = (target["Groups"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
                    var found = (defaults["Groups"] as JsonArray ?? []).FirstOrDefault(entry => entry?["Name"]?.GetValue<string>() == group);
                    info.Executable = Inside(found?["Path"]?.GetValue<string>(), folder);
                    info.Instances = found?["Workers"]?.GetValue<int>() ?? 1;
                    break;
                }
                case "service":
                {
                    var name = target["Service"]?.GetValue<string>() ?? item.Name;
                    var found = (defaults["Services"] as JsonArray ?? []).FirstOrDefault(entry => string.Equals(entry?["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
                    // Run by nssm: the program and the script given to it, which is the file of the application.
                    info.Program = found?["Program"]?.GetValue<string>();
                    info.Wrapper = info.Program is null ? null : found?["Path"]?.GetValue<string>()?.Trim('"');
                    info.Executable = info.Program is null ? Inside(found?["Path"]?.GetValue<string>()?.Trim('"'), folder) : Inside(found?["Script"]?.GetValue<string>(), folder);
                    info.DisplayName = found?["DisplayName"]?.GetValue<string>();
                    info.StartType = found?["StartType"]?.GetValue<string>();
                    break;
                }
                case "api":
                {
                    var sites = (target["Sites"] as JsonArray ?? []).Select(site => site?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    info.Ports = [.. (defaults["Sites"] as JsonArray ?? []).Where(site => sites.Contains(site?["Name"]?.GetValue<string>() ?? "")).Select(site => site!["Port"]!.GetValue<int>()).Order()];
                    info.Instances = Math.Max(1, sites.Count);
                    info.Proxy = await ProxyLinesFor(info.Ports, by, cancellation);
                    break;
                }
            }

            var files = await Ask("TransportFiles", by, request => { request["Kind"] = item.Kind; request["Name"] = item.Name; }, cancellation);
            foreach (var file in (files["Files"] as JsonArray ?? []).Where(file => file?["Instance"]?.GetValue<int>() == 0).Take(20))
            {
                var path = file!["Path"]!.GetValue<string>();
                var read = await Ask("TransportFile", by, request => { request["Kind"] = item.Kind; request["Name"] = item.Name; request["Instance"] = 0; request["Path"] = path; }, cancellation);
                var (content, blanked) = InstallText.Blank(path, read["Content"]?.GetValue<string>() ?? "");
                info.Settings.Add(new SettingTemplate { Path = path, Content = content, Blanked = blanked });
            }
            return info;
        }
        catch (TransportRefused refused)
        {
            logger.LogInformation("Transport: {Kind} {Name} goes without what it takes to install it elsewhere: {Problem}", item.Kind, item.Name, refused.Message);
            return null;
        }
    }

    private async Task<string?> ProxyLinesFor(IReadOnlyCollection<int> ports, string by, CancellationToken cancellation)
    {
        if (ports.Count == 0)
            return null;
        try
        {
            var proxy = await Ask("Proxy", by, _ => { }, cancellation);
            if (proxy["Configured"]?.GetValue<bool>() != true)
                return null;
            var files = new List<(string, string)>();
            foreach (var file in (proxy["Files"] as JsonArray ?? []).Take(50))
            {
                var path = file!["Path"]!.GetValue<string>();
                var read = await Ask("ProxyFile", by, request => request["Path"] = path, cancellation);
                files.Add((path, read["Content"]?.GetValue<string>() ?? ""));
            }
            return InstallText.ProxyLines(files, ports);
        }
        catch (TransportRefused)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks what whoever approves said about installing each item this environment does not
    /// have, before anything is decided.
    /// </summary>
    private static Dictionary<int, InstallChoice> CheckInstalls(PackageRecord record, IReadOnlyList<ItemCheck> checks, IReadOnlyDictionary<int, InstallChoice>? given)
    {
        var chosen = new Dictionary<int, InstallChoice>();
        foreach (var check in checks.Where(check => check.State == "install"))
        {
            var item = record.Manifest.Items.First(candidate => candidate.Number == check.Number);
            if (given?.GetValueOrDefault(item.Number) is not { } choice)
                throw new TransportRefused($"O item {item.Number} ({item.Kind} {item.Name}) não existe neste ambiente: informe como instalá-lo", StatusCodes.Status409Conflict);
            if (string.IsNullOrWhiteSpace(choice.Folder))
                throw new TransportRefused($"Informe a pasta de {item.Name}");
            if (item.Kind is "worker" or "service" && string.IsNullOrWhiteSpace(choice.Executable))
                throw new TransportRefused($"Informe o executável de {item.Name}");
            if (item.Kind == "api" && choice.Port is < 1 or > 65535)
                throw new TransportRefused($"Informe a primeira porta de {item.Name}");
            chosen[item.Number] = choice;
        }
        return chosen;
    }

    /// <summary>
    /// Installs an item this environment did not have: the Worker Control creates it, as a new
    /// application made from the panel is, with the files of the item and the configuration filled
    /// in when it was approved.
    /// </summary>
    private async Task Install(string id, PackageItem item, InstallChoice choice, ItemResult result, string by, CancellationToken cancellation)
    {
        var files = store.Content(id, item.Number) ?? throw new TransportRefused("O conteúdo do item não está mais no arquivo do pacote");
        var token = Guid.NewGuid().ToString("N");
        var work = store.WorkFile($"new-{token}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(work)!);
        await File.WriteAllBytesAsync(work, files, cancellation);
        try
        {
            var answer = await worker.AskAsync("ApplicationCreate", by, ReplacePatience, request =>
            {
                request["Kind"] = item.Kind;
                request["Name"] = item.Name;
                request["Folder"] = choice.Folder.Trim();
                request["File"] = work;
                request["Executable"] = choice.Executable?.Trim();
                if (item.Kind == "service" && !string.IsNullOrWhiteSpace(choice.Program))
                {
                    request["Program"] = choice.Program.Trim();
                    if (!string.IsNullOrWhiteSpace(choice.Wrapper))
                        request["Wrapper"] = choice.Wrapper.Trim();
                }
                request["Instances"] = choice.Instances;
                request["Port"] = choice.Port;
                request["SiteName"] = string.IsNullOrWhiteSpace(choice.SiteName) ? null : choice.SiteName.Trim();
                request["SharedFolder"] = choice.SharedFolder;
                request["DisplayName"] = string.IsNullOrWhiteSpace(choice.DisplayName) ? null : choice.DisplayName.Trim();
                request["StartType"] = choice.StartType;
                request["Configs"] = new JsonArray([.. choice.Settings.Select(setting => new JsonObject { ["Path"] = setting.Path, ["Content"] = setting.Content })]);
            }, cancellation);
            if (!answer.Reached)
            {
                result.Status = "unknown";
                result.Problem = (answer.Problem ?? "O Worker Control não respondeu") + ". Confira na máquina o que chegou a ser criado.";
            }
            else if (!answer.Ok)
            {
                result.Status = "failed";
                result.Problem = answer.ErrorMessage;
            }
            else
            {
                var body = answer.Body!;
                var created = body["Created"]?.GetValue<bool>() == true;
                result.Status = created ? "applied" : "failed";
                result.Installed = created;
                result.Did = created ? $"instalado em {choice.Folder.Trim()}" : null;
                result.Problem = body["Problem"]?.GetValue<string>();
                result.Messages = [.. (body["Did"] as JsonArray ?? []).Select(step => step?.GetValue<string>() ?? "")];
            }
        }
        finally
        {
            File.Delete(work);
        }
    }
}
