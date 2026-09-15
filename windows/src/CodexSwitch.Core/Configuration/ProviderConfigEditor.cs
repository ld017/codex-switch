using System.Text;
using System.Text.RegularExpressions;

namespace CodexSwitch.Core.Configuration;

public static partial class ProviderConfigEditor
{
    public static Provider Read(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scan = ScanTopLevel(source);
        if (scan.ProviderIndexes.Count > 1)
        {
            throw new ProviderConfigException(
                ProviderConfigError.DuplicateProvider,
                "配置中存在多个顶层 model_provider。"
            );
        }

        if (scan.ProviderIndexes.Count == 0)
        {
            if (scan.ModelIndex is not null)
            {
                return Provider.OpenAI;
            }

            throw new ProviderConfigException(
                ProviderConfigError.MissingProvider,
                "配置中没有找到顶层 model_provider。"
            );
        }

        var line = MatchableContent(scan.Lines[scan.ProviderIndexes[0]].Content);
        var match = ProviderLineRegex().Match(line);
        return match.Groups[1].Value switch
        {
            "openai" => Provider.OpenAI,
            "sub2api" => Provider.Sub2Api,
            var value => throw new ProviderConfigException(
                ProviderConfigError.UnknownProvider,
                $"无法识别当前连接提供方：{value}"
            ),
        };
    }

    public static string Replace(string source, Provider target)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scan = ScanTopLevel(source);
        if (scan.ProviderIndexes.Count > 1)
        {
            throw new ProviderConfigException(
                ProviderConfigError.DuplicateProvider,
                "配置中存在多个顶层 model_provider。"
            );
        }

        if (scan.ProviderIndexes.Count == 1)
        {
            var index = scan.ProviderIndexes[0];
            var original = scan.Lines[index];
            var hasBom = original.Content.StartsWith('\uFEFF');
            var matchable = MatchableContent(original.Content);
            var replaced = ProviderReplaceRegex().Replace(
                matchable,
                match => match.Groups[1].Value + target.ToConfigValue() + match.Groups[2].Value
            );
            scan.Lines[index] = original with
            {
                Content = (hasBom ? "\uFEFF" : string.Empty) + replaced,
            };
            return Join(scan.Lines);
        }

        if (scan.ModelIndex is not int modelIndex)
        {
            throw new ProviderConfigException(
                ProviderConfigError.MissingModelAnchor,
                "配置中没有找到顶层 model，无法安全插入 model_provider。"
            );
        }

        var ending = scan.Lines[modelIndex].Ending;
        if (ending.Length == 0)
        {
            ending = PreferredLineEnding(source);
            scan.Lines[modelIndex] = scan.Lines[modelIndex] with { Ending = ending };
        }

        scan.Lines.Insert(
            modelIndex + 1,
            new LinePart($"model_provider = \"{target.ToConfigValue()}\"", ending)
        );
        return Join(scan.Lines);
    }

    public static bool HasSub2ApiConfiguration(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hasProvider = false;
        var hasAuth = false;
        foreach (var part in SplitLines(source))
        {
            var line = MatchableContent(part.Content).Trim();
            hasProvider |= line == "[model_providers.sub2api]";
            hasAuth |= line == "[model_providers.sub2api.auth]";
        }

        return hasProvider && hasAuth;
    }

    private static ScanResult ScanTopLevel(string source)
    {
        var lines = SplitLines(source);
        var providerIndexes = new List<int>();
        int? modelIndex = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var content = MatchableContent(lines[index].Content);
            if (content.TrimStart(' ', '\t').StartsWith('['))
            {
                break;
            }

            if (ProviderLineRegex().IsMatch(content))
            {
                providerIndexes.Add(index);
            }
            else if (ModelLineRegex().IsMatch(content))
            {
                modelIndex = index;
            }
        }

        return new ScanResult(lines, providerIndexes, modelIndex);
    }

    private static List<LinePart> SplitLines(string source)
    {
        var lines = new List<LinePart>();
        var start = 0;
        while (start < source.Length)
        {
            var cursor = start;
            while (cursor < source.Length && source[cursor] is not '\r' and not '\n')
            {
                cursor++;
            }

            var content = source[start..cursor];
            var ending = string.Empty;
            if (cursor < source.Length)
            {
                if (source[cursor] == '\r' && cursor + 1 < source.Length && source[cursor + 1] == '\n')
                {
                    ending = "\r\n";
                    cursor += 2;
                }
                else
                {
                    ending = source[cursor].ToString();
                    cursor++;
                }
            }

            lines.Add(new LinePart(content, ending));
            start = cursor;
        }

        return lines;
    }

    private static string Join(IEnumerable<LinePart> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line.Content);
            builder.Append(line.Ending);
        }

        return builder.ToString();
    }

    private static string MatchableContent(string content) => content.TrimStart('\uFEFF');

    private static string PreferredLineEnding(string source) => source.Contains("\r\n", StringComparison.Ordinal)
        ? "\r\n"
        : source.Contains('\r') ? "\r" : "\n";

    [GeneratedRegex("^[ \\t]*model_provider[ \\t]*=[ \\t]*\\\"([^\\\"]+)\\\"[ \\t]*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderLineRegex();

    [GeneratedRegex("^([ \\t]*model_provider[ \\t]*=[ \\t]*\\\")[^\\\"]+(\\\".*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderReplaceRegex();

    [GeneratedRegex("^[ \\t]*model[ \\t]*=", RegexOptions.CultureInvariant)]
    private static partial Regex ModelLineRegex();

    private sealed record ScanResult(List<LinePart> Lines, List<int> ProviderIndexes, int? ModelIndex);

    private sealed record LinePart(string Content, string Ending);
}
