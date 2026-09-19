using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleTranslatorCS;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class JsoncTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "st-jsonc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDir, true);
    }

    private string WriteTempFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void StripComments_RemovesLineAndBlockComments()
    {
        var input = """
        {
            // 行注释
            "a": "1", /* 块注释 */
            /* 多行
               块注释 */
            "b": "2"
        }
        """;
        var result = Jsonc.StripComments(input);
        StringAssert.DoesNotMatch(result, new System.Text.RegularExpressions.Regex("行注释|块注释|多行"));
        StringAssert.Contains(result, "\"a\": \"1\"");
    }

    [TestMethod]
    public void StripComments_KeepsCommentLikeTextInsideStrings()
    {
        var input = """{ "a": "这不是 // 注释", "b": "这也不是 /* 注释 */" }""";
        var result = Jsonc.StripComments(input);
        StringAssert.Contains(result, "这不是 // 注释");
        StringAssert.Contains(result, "这也不是 /* 注释 */");
    }

    [TestMethod]
    public void StripComments_HandlesEscapedQuotesInStrings()
    {
        // JSON 里的 \" 转义序列不应终止字符串状态，其后的 // 仍在字符串内
        var input = """{ "a": "引号\"转义 // 保留" } // 真注释""";
        var result = Jsonc.StripComments(input);
        StringAssert.Contains(result, """引号\"转义 // 保留""");
        StringAssert.DoesNotMatch(result, new System.Text.RegularExpressions.Regex("真注释"));
    }

    [TestMethod]
    public void Normalize_RemovesTrailingCommas()
    {
        // Normalize 现在基于原生解析选项重写为严格 JSON（压缩格式），
        // 字符串内的 ,} / ,] 不会被误删
        var result = Jsonc.Normalize("""{ "a": "1", "b": "2", }""");
        Assert.AreEqual("""{"a":"1","b":"2"}""", result);
    }

    [TestMethod]
    public void Normalize_RemovesTrailingCommasInArrays()
    {
        var result = Jsonc.Normalize("""{ "a": "1", "arr": [1, 2,], }""");
        Assert.AreEqual("""{"a":"1","arr":[1,2]}""", result);
    }

    [TestMethod]
    public void Normalize_KeepsCommaBraceInsideStringValue()
    {
        var result = Jsonc.Normalize("""{ "text": "hello,}", }""");
        Assert.AreEqual("""{"text":"hello,}"}""", result);
    }

    [TestMethod]
    public void ParseFile_CommaBraceInsideStringValue_NotCorrupted()
    {
        // 核心回归：字符串值内的 ,} 不是尾随逗号，绝不能被改写（旧正则实现的缺陷）
        var path = WriteTempFile("comma-brace.jsonc", """{ "text": "hello,}" }""");
        var result = Jsonc.ParseFile(path, null);
        Assert.IsNotNull(result);
        Assert.AreEqual("hello,}", result["text"]);
    }

    [TestMethod]
    public void ParseFile_CommaBracketInsideStringValue_NotCorrupted()
    {
        // 核心回归：字符串值内的 ,] 同样不能被改写
        var path = WriteTempFile("comma-bracket.jsonc", """{ "text": "hello,]" }""");
        var result = Jsonc.ParseFile(path, null);
        Assert.IsNotNull(result);
        Assert.AreEqual("hello,]", result["text"]);
    }

    [TestMethod]
    public void ParseFile_JsonExtension_ToleratesCommentsAndTrailingComma()
    {
        // .json 与 .jsonc 走同一套原生选项，行为一致
        var path = WriteTempFile("a.json", """
        {
            // 注释
            "k1": "v1",
        }
        """);
        var result = Jsonc.ParseFile(path, null);
        Assert.IsNotNull(result);
        Assert.AreEqual("v1", result["k1"]);
    }

    [TestMethod]
    public void ParseFile_CommentLikeTextInsideStringValue_Preserved()
    {
        var path = WriteTempFile("commentlike.jsonc",
            """{ "a": "这不是 // 注释", "b": "这也不是 /* 注释 */" }""");
        var result = Jsonc.ParseFile(path, null);
        Assert.IsNotNull(result);
        Assert.AreEqual("这不是 // 注释", result["a"]);
        Assert.AreEqual("这也不是 /* 注释 */", result["b"]);
    }

    [TestMethod]
    public void ParseFile_InvalidJson_WarningContainsLineNumber()
    {
        // 第 2 行出错，warn 信息应包含 1 起始的行号便于定位
        var path = WriteTempFile("broken-line.jsonc", "{ \"a\": \"1\",\n  \"b\": }");
        var warnings = new List<string>();
        var result = Jsonc.ParseFile(path, warnings.Add);
        Assert.IsNull(result);
        Assert.IsTrue(warnings.Exists(w => w.Contains("解析文件失败") && w.Contains("行号") && w.Contains('2')),
            $"warn 信息应包含行号: {string.Join(" | ", warnings)}");
    }

    [TestMethod]
    public void ParseFile_ParsesJsoncWithCommentsAndTrailingComma()
    {
        var path = WriteTempFile("a.jsonc", """
        {
            /* 头注释 */
            "k1": "v1", // 行注释
            "k2": "v2",
        }
        """);
        var result = Jsonc.ParseFile(path, null);
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("v1", result["k1"]);
        Assert.AreEqual("v2", result["k2"]);
    }

    [TestMethod]
    public void ParseFile_DuplicateKey_KeepsLastAndWarns()
    {
        var path = WriteTempFile("dup.jsonc", """{ "k": "first", "k": "second" }""");
        var warnings = new List<string>();
        var result = Jsonc.ParseFile(path, warnings.Add);
        Assert.IsNotNull(result);
        Assert.AreEqual("second", result["k"]);
        Assert.IsTrue(warnings.Exists(w => w.Contains("重复键") && w.Contains("k")));
    }

    [TestMethod]
    public void ParseFile_NonStringValue_SkippedWithWarning()
    {
        var path = WriteTempFile("nonstr.jsonc", """{ "good": "v", "bad": 123 }""");
        var warnings = new List<string>();
        var result = Jsonc.ParseFile(path, warnings.Add);
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("v", result["good"]);
        Assert.IsTrue(warnings.Exists(w => w.Contains("非字符串") && w.Contains("bad")));
    }

    [TestMethod]
    public void ParseFile_InvalidJson_ReturnsNullWithWarning()
    {
        var path = WriteTempFile("broken.jsonc", """{ "a": "1" """); // 未闭合
        var warnings = new List<string>();
        var result = Jsonc.ParseFile(path, warnings.Add);
        Assert.IsNull(result);
        Assert.IsTrue(warnings.Exists(w => w.Contains("解析文件失败")));
    }

    [TestMethod]
    public void ParseFile_TopLevelNotObject_ReturnsNullWithWarning()
    {
        var path = WriteTempFile("array.jsonc", """[ "a", "b" ]""");
        var warnings = new List<string>();
        var result = Jsonc.ParseFile(path, warnings.Add);
        Assert.IsNull(result);
        Assert.IsTrue(warnings.Exists(w => w.Contains("顶层必须是对象")));
    }

    [TestMethod]
    public void ParseFile_GbkEncodedFile_WarnsAboutEncoding()
    {
        // {"a":"中"} 其中 "中" 用 GBK 字节 0xD6 0xD0 写入（无 BOM），UTF-8 解码会产生替换字符
        var path = Path.Combine(_tempDir, "gbk.jsonc");
        var bytes = new List<byte>();
        bytes.AddRange("{\"a\":\""u8.ToArray());
        bytes.Add(0xD6);
        bytes.Add(0xD0);
        bytes.AddRange("\"}"u8.ToArray());
        File.WriteAllBytes(path, bytes.ToArray());

        var warnings = new List<string>();
        Jsonc.ParseFile(path, warnings.Add);
        Assert.IsTrue(warnings.Exists(w => w.Contains("UTF-8")));
    }
}
