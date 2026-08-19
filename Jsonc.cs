using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleTranslatorCS;

/// <summary>
/// JSON/JSONC 解析工具：剥离注释、去尾随逗号、读文件（含编码检测）、
/// 解析为键值字典（含同文件重复键检测）。
/// 不依赖任何 SPT 类型，日志通过回调外抛，便于单元测试。
/// </summary>
internal static class Jsonc
{
    // 预编译正则：匹配对象/数组收尾前的尾随逗号
    private static readonly Regex TrailingCommaRegex = new(@",\s*(\}|\])", RegexOptions.Compiled);

    /// <summary>
    /// 安全地剥离 JSON/JSONC 中的注释（// 行注释与 /* */ 块注释）。
    /// 通过逐字符状态机实现，避免正则把字符串字面量里的 // 或 /* 误判为注释；
    /// 换行符会保留以保持行号。
    /// </summary>
    public static string StripComments(string input)
    {
        var result = new StringBuilder();
        var inString = false;
        var inBlockComment = false;
        var inLineComment = false;
        var escapeNext = false;

        for (int i = 0; i < input.Length; i++)
        {
            char current = input[i];
            char next = i + 1 < input.Length ? input[i + 1] : '\0';

            if (escapeNext)
            {
                result.Append(current);
                escapeNext = false;
                continue;
            }

            if (current == '\\' && inString)
            {
                result.Append(current);
                escapeNext = true;
                continue;
            }

            if (!inString && !inBlockComment && !inLineComment)
            {
                if (current == '/' && next == '/')
                {
                    inLineComment = true;
                    i++; // 跳过下一个字符 '/'
                    continue;
                }
                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++; // 跳过下一个字符 '*'
                    continue;
                }
                if (current == '"')
                {
                    inString = true;
                    result.Append(current);
                    continue;
                }
            }
            else if (inString)
            {
                if (current == '"')
                {
                    inString = false;
                }
                result.Append(current);
                continue;
            }
            else if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++; // 跳过下一个字符 '/'
                    continue;
                }
                // 注释内容不添加到结果中
                continue;
            }
            else if (inLineComment)
            {
                if (current == '\n' || current == '\r')
                {
                    inLineComment = false;
                    result.Append(current); // 保留换行符以保持行号
                }
                // 注释内容不添加到结果中
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    /// <summary>
    /// 规范化 JSONC 文本：先剥离注释，再移除尾随逗号，输出严格 JSON。
    /// </summary>
    public static string Normalize(string rawContent)
    {
        var noComments = StripComments(rawContent);
        return TrailingCommaRegex.Replace(noComments, "$1");
    }

    /// <summary>
    /// 读取文件文本（自动识别 BOM，默认按 UTF-8 解码）。
    /// 若解码后出现替换字符 U+FFFD，说明文件很可能不是 UTF-8（常见为记事本存出的 GBK/ANSI），
    /// 通过 warn 回调给出明确提示——没有 CodePages 包无法自动转码，只能提示用户另存为 UTF-8。
    /// </summary>
    public static string ReadFileText(string path, Action<string>? warn)
    {
        var text = File.ReadAllText(path);
        if (text.Contains('�'))
        {
            warn?.Invoke($"文件包含无法按 UTF-8 解码的字符（疑似 GBK/ANSI 编码），请用编辑器另存为 UTF-8: {path}");
        }
        return text;
    }

    /// <summary>
    /// 解析 .json/.jsonc 文件为键值字典。
    /// - 同文件内重复键：保留最后一条（与 System.Text.Json 行为一致），并逐键 warn 提示；
    /// - 非字符串值：跳过并 warn 提示；
    /// - 解析失败：warn 提示并返回 null。
    /// </summary>
    public static Dictionary<string, string>? ParseFile(string path, Action<string>? warn)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var raw = ReadFileText(path, warn);
            var json = ext == ".jsonc" ? Normalize(raw) : raw;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                warn?.Invoke($"翻译文件顶层必须是对象 {{ }}: {path}");
                return null;
            }

            var result = new Dictionary<string, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    warn?.Invoke($"跳过非字符串值的键 \"{prop.Name}\"（翻译值必须是字符串）: {path}");
                    continue;
                }
                if (result.ContainsKey(prop.Name))
                {
                    warn?.Invoke($"同文件内重复键 \"{prop.Name}\"，已保留最后一条: {path}");
                }
                result[prop.Name] = prop.Value.GetString()!;
            }
            return result;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"解析文件失败: {path}, 错误: {ex.Message}");
            return null;
        }
    }
}
