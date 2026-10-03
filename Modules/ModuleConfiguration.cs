using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace RigiCompiler.Modules;

public enum ModuleProductKind { Executable, StaticLibrary, DynamicLibrary }
public enum ModuleRunTarget { Vm, Native }
public enum ModuleHookPhase { BeforePublish, AfterPublish, BeforeInstall, AfterInstall }

public sealed class ModuleConfigurationException(string message) : Exception(message);
public sealed record ModuleDependency(string Name, string Version, string? Path)
{
    public string ModuleId => Name + "@" + Version;
}
public sealed record ModuleProfile(string Name, ModuleRunTarget Target, string? Entry, string? Product);
public sealed record ModuleResource(string Source, string Destination);
public sealed record ModuleHook(ModuleHookPhase Phase, string Environment, string Command,
    IReadOnlyList<string> Inputs);

/// <summary>schema 1 的不可变配置；身份与路径语义不依机器工作目录。</summary>
public sealed record ModuleConfiguration(string Name, string Version, ModuleProductKind Type,
    IReadOnlyList<string> Sources, IReadOnlyList<ModuleDependency> Dependencies,
    IReadOnlyList<ModuleResource> Resources, IReadOnlyDictionary<string, ModuleProfile> Profiles,
    string DefaultProfile, string? Entry, IReadOnlyDictionary<string, string> Exports,
    IReadOnlyList<ModuleHook> Hooks)
{
    public string ModuleId => Name + "@" + Version;
    public ModuleProfile SelectProfile(string? name = null)
    {
        var selected = name ?? DefaultProfile;
        return Profiles.TryGetValue(selected, out var profile) ? profile
            : throw new ModuleConfigurationException($"module {ModuleId}: 未知 profile '{selected}'");
    }
}

/// <summary>只使用 YAML 节点 API，所有 schema 字段显式读取，NativeAOT 不依反射。</summary>
public static partial class ModuleConfigurationReader
{
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_-]*(\\.[a-zA-Z][a-zA-Z0-9_-]*)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?(\\+[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CNamePattern();

    public static ModuleConfiguration ReadFile(string path) => Read(File.ReadAllText(path), path);
    public static ModuleConfiguration Read(string yaml, string sourceName = "module.yaml")
    {
        try
        {
            // 配置不需要共享节点；拒绝 anchor/alias，避免循环与别名展开预算歧义。
            var parser = new YamlDotNet.Core.Parser(new StringReader(yaml));
            while (parser.MoveNext())
                if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && !node.Anchor.IsEmpty)
                    throw Error(sourceName, "不允许 YAML anchor/alias");
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1) throw Error(sourceName, "必须恰有一个 YAML document");
            var root = Map(stream.Documents[0].RootNode, sourceName);
            Known(root, sourceName, "schema", "name", "version", "type", "source", "dependencies",
                "resources", "profiles", "default-profile", "entry", "exports", "hooks");
            if (Required(root, "schema", sourceName) != "1") throw Error(sourceName, "不支持的 schema（要求 1）");
            var name = Required(root, "name", sourceName);
            var version = Required(root, "version", sourceName);
            ValidateIdentity(name, version, sourceName);
            var kind = Required(root, "type", sourceName) switch
            {
                "executable" => ModuleProductKind.Executable,
                "static-library" => ModuleProductKind.StaticLibrary,
                "dyn-library" => ModuleProductKind.DynamicLibrary,
                var value => throw Error(sourceName, $"未知 type '{value}'")
            };
            var sources = root.TryGetValue("source", out var sourceNode)
                ? Strings(sourceNode, sourceName + ".source") : ["**/*.rg"];
            foreach (var source in sources) ModulePaths.ValidateRelative(source, true);
            if (sources.Distinct(StringComparer.Ordinal).Count() != sources.Count)
                throw Error(sourceName, "重复 source selector");
            var dependencies = new List<ModuleDependency>();
            if (root.TryGetValue("dependencies", out var dependencyNode))
                foreach (var child in Sequence(dependencyNode, sourceName + ".dependencies"))
                {
                    var item = Map(child, sourceName + ".dependencies[]");
                    Known(item, sourceName, "name", "version", "path");
                    var depName = Required(item, "name", sourceName);
                    var depVersion = Required(item, "version", sourceName);
                    ValidateIdentity(depName, depVersion, sourceName);
                    var path = Optional(item, "path", sourceName);
                    if (path != null) ModulePaths.ValidateRelative(path);
                    if (dependencies.Any(d => d.Name == depName)) throw Error(sourceName, $"重复依赖 '{depName}'");
                    dependencies.Add(new(depName, depVersion, path));
                }
            var resources = new List<ModuleResource>();
            if (root.TryGetValue("resources", out var resourceNode))
                foreach (var child in Sequence(resourceNode, sourceName + ".resources"))
                {
                    var item = Map(child, sourceName + ".resources[]");
                    Known(item, sourceName, "source", "destination");
                    var source = Required(item, "source", sourceName);
                    var destination = Required(item, "destination", sourceName);
                    ModulePaths.ValidateRelative(source);
                    ModulePaths.ValidateRelative(destination);
                    if (resources.Any(r => string.Equals(r.Destination, destination, StringComparison.OrdinalIgnoreCase)))
                        throw Error(sourceName, $"重复 resource destination '{destination}'");
                    resources.Add(new(source, destination));
                }
            var profiles = new Dictionary<string, ModuleProfile>(StringComparer.Ordinal);
            if (root.TryGetValue("profiles", out var profileNode))
                foreach (var (profileName, child) in Map(profileNode, sourceName + ".profiles"))
                {
                    if (!NamePattern().IsMatch(profileName)) throw Error(sourceName, $"非法 profile '{profileName}'");
                    var item = Map(child, sourceName + ".profiles." + profileName);
                    Known(item, sourceName, "target", "entry", "product");
                    var target = Required(item, "target", sourceName) switch
                    {
                        "vm" => ModuleRunTarget.Vm, "native" => ModuleRunTarget.Native,
                        var value => throw Error(sourceName, $"未知 profile target '{value}'")
                    };
                    var product = Optional(item, "product", sourceName);
                    if (product != null) ModulePaths.ValidateRelative(product);
                    profiles.Add(profileName, new(profileName, target, Optional(item, "entry", sourceName), product));
                }
            else profiles.Add("default", new("default", ModuleRunTarget.Native, null, null));
            var defaultProfile = Optional(root, "default-profile", sourceName) ?? "default";
            if (!profiles.ContainsKey(defaultProfile)) throw Error(sourceName, $"default-profile '{defaultProfile}' 不存在");
            var exports = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetValue("exports", out var exportNode))
                foreach (var (cName, child) in Map(exportNode, sourceName + ".exports"))
                {
                    if (!CNamePattern().IsMatch(cName) || cName.StartsWith("rigi_", StringComparison.Ordinal))
                        throw Error(sourceName, $"非法或保留 C export 名 '{cName}'");
                    exports.Add(cName, Scalar(child, sourceName + ".exports." + cName));
                }
            var hooks = new List<ModuleHook>();
            if (root.TryGetValue("hooks", out var hookNode))
                foreach (var child in Sequence(hookNode, sourceName + ".hooks"))
                {
                    var item = Map(child, sourceName + ".hooks[]");
                    Known(item, sourceName, "phase", "environment", "command", "inputs");
                    var phase = Required(item, "phase", sourceName) switch
                    {
                        "before-publish" => ModuleHookPhase.BeforePublish, "after-publish" => ModuleHookPhase.AfterPublish,
                        "before-install" => ModuleHookPhase.BeforeInstall, "after-install" => ModuleHookPhase.AfterInstall,
                        var value => throw Error(sourceName, $"未知 hook phase '{value}'")
                    };
                    var environment = Required(item, "environment", sourceName);
                    if (environment is not ("linux" or "windows" or "all")) throw Error(sourceName, $"未知 hook environment '{environment}'");
                    var command = Required(item, "command", sourceName);
                    if (command.IndexOfAny(['\r', '\n', '\0']) >= 0) throw Error(sourceName, "hook command 必须单行且无 NUL");
                    var inputs = item.TryGetValue("inputs", out var inputsNode) ? Strings(inputsNode, sourceName + ".hooks.inputs") : [];
                    foreach (var input in inputs) ModulePaths.ValidateRelative(input);
                    hooks.Add(new(phase, environment, command, inputs));
                }
            return new(name, version, kind, sources, dependencies, resources, profiles, defaultProfile,
                Optional(root, "entry", sourceName), exports, hooks);
        }
        catch (YamlException ex) { throw Error(sourceName, $"YAML 无效：{ex.Message}"); }
    }

    internal static void ValidateIdentity(string name, string version, string sourceName)
    {
        if (!NamePattern().IsMatch(name)) throw Error(sourceName, $"非法 module name '{name}'");
        if (!VersionPattern().IsMatch(version)) throw Error(sourceName, $"非法 module version '{version}'");
        var prerelease = version.Split('+')[0].Split('-').Skip(1);
        // SemVer 数字 prerelease 标识符不能带前导零。
        foreach (var part in string.Join('-', prerelease).Split('.'))
            if (part.Length > 1 && part.All(char.IsAsciiDigit) && part[0] == '0') throw Error(sourceName, "非法 prerelease 前导零");
    }
    private static ModuleConfigurationException Error(string where, string text) => new($"{where}: {text}");
    private static Dictionary<string, YamlNode> Map(YamlNode node, string where)
    {
        if (node is not YamlMappingNode mapping) throw Error(where, "要求 mapping");
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var pair in mapping.Children)
        {
            var key = Scalar(pair.Key, where + ".key");
            if (!result.TryAdd(key, pair.Value)) throw Error(where, $"重复 key '{key}'");
        }
        return result;
    }
    private static IEnumerable<YamlNode> Sequence(YamlNode node, string where) => node is YamlSequenceNode sequence
        ? sequence.Children : throw Error(where, "要求 sequence");
    private static IReadOnlyList<string> Strings(YamlNode node, string where) =>
        Sequence(node, where).Select(n => Scalar(n, where)).ToArray();
    private static string Scalar(YamlNode node, string where) => node is YamlScalarNode scalar
        && !string.IsNullOrWhiteSpace(scalar.Value) ? scalar.Value : throw Error(where, "要求非空 scalar");
    private static string Required(Dictionary<string, YamlNode> map, string key, string where) =>
        map.TryGetValue(key, out var value) ? Scalar(value, where + "." + key) : throw Error(where, $"缺少 '{key}'");
    private static string? Optional(Dictionary<string, YamlNode> map, string key, string where) =>
        map.TryGetValue(key, out var value) ? Scalar(value, where + "." + key) : null;
    private static void Known(Dictionary<string, YamlNode> map, string where, params string[] keys)
    {
        foreach (var key in map.Keys) if (!keys.Contains(key, StringComparer.Ordinal)) throw Error(where, $"未知字段 '{key}'");
    }
}
