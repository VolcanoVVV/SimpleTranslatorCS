using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleTranslatorCS;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class TranslationFilesTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "st-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDir, true);
    }

    private string WriteTempFile(string relativePath, string content = "{}")
    {
        var path = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // 统一为 '/' 分隔的相对路径，便于跨平台断言
    private static List<string> RelPaths(IEnumerable<string> files, string baseDir) =>
        files.Select(f => Path.GetRelativePath(baseDir, f).Replace('\\', '/')).ToList();

    [TestMethod]
    public void EnumerateLocaleFiles_ReturnsStableSortedRelativeOrder()
    {
        // 故意乱序创建，且大写扩展名也应被识别为翻译文件
        WriteTempFile("b.jsonc");
        WriteTempFile("sub/c.jsonc");
        WriteTempFile("a.json");
        WriteTempFile("d.JSONC");

        var files = TranslationFiles.EnumerateLocaleFiles(_tempDir);

        // 按 '/' 分隔相对路径的 Ordinal 排序：'a' < 'b' < 'd' < 's'
        CollectionAssert.AreEqual(
            new List<string> { "a.json", "b.jsonc", "d.JSONC", "sub/c.jsonc" },
            RelPaths(files, _tempDir));
    }

    [TestMethod]
    public void EnumerateLocaleFiles_ExcludesDialogueSubdirectory()
    {
        WriteTempFile("a.json");
        WriteTempFile("dialogue/d.jsonc");
        WriteTempFile("dialogue/sub/e.json");

        var files = TranslationFiles.EnumerateLocaleFiles(_tempDir);

        CollectionAssert.AreEqual(new List<string> { "a.json" }, RelPaths(files, _tempDir));
    }

    [TestMethod]
    public void EnumerateDialogueFiles_IncludesOnlyDialogueContents_Sorted()
    {
        WriteTempFile("a.json");
        var dialogueDir = WriteTempFile("dialogue/z.jsonc");
        WriteTempFile("dialogue/sub/e.json");
        WriteTempFile("dialogue/readme.txt");
        dialogueDir = Path.GetDirectoryName(dialogueDir)!;

        var files = TranslationFiles.EnumerateDialogueFiles(dialogueDir);

        CollectionAssert.AreEqual(
            new List<string> { "sub/e.json", "z.jsonc" },
            RelPaths(files, dialogueDir));
    }

    [TestMethod]
    public void EnumerateLocaleFiles_ExcludesNonJsonFiles()
    {
        WriteTempFile("a.json");
        WriteTempFile("readme.txt");
        WriteTempFile("notes.md");
        WriteTempFile("backup.json.bak");
        WriteTempFile("sub/temp.jsonc.tmp");

        var files = TranslationFiles.EnumerateLocaleFiles(_tempDir);

        CollectionAssert.AreEqual(new List<string> { "a.json" }, RelPaths(files, _tempDir));
    }

    [TestMethod]
    public void EnumerateLocaleFiles_RepeatedEnumeration_IsIdentical()
    {
        WriteTempFile("b.jsonc");
        WriteTempFile("sub/c.jsonc");
        WriteTempFile("a.json");

        var first = TranslationFiles.EnumerateLocaleFiles(_tempDir);
        var second = TranslationFiles.EnumerateLocaleFiles(_tempDir);

        CollectionAssert.AreEqual(first.ToList(), second.ToList());
    }

    [TestMethod]
    public void EnumerateLocaleFiles_MissingDirectory_ReturnsEmpty()
    {
        var files = TranslationFiles.EnumerateLocaleFiles(Path.Combine(_tempDir, "not-exist"));
        Assert.AreEqual(0, files.Count);
    }

    [TestMethod]
    public void KeyComparer_TestBuild_IsOrdinalIgnoreCase()
    {
        // 测试项目按 4.1（net10，无 SPT40 定义）编译，KeyComparer 应与目标容器
        // GlobalLocaleDictionary 的实测比较规则 OrdinalIgnoreCase 一致。
        // 4.0 路径（StringComparer.Ordinal）是编译期分支（#if SPT40），单测覆盖不到。
        Assert.AreSame(StringComparer.OrdinalIgnoreCase, TranslationFiles.KeyComparer);
        Assert.IsTrue(TranslationFiles.KeyComparer.Equals("Key", "key"));
        Assert.AreEqual(
            StringComparer.OrdinalIgnoreCase.GetHashCode("Key"),
            TranslationFiles.KeyComparer.GetHashCode("key"));
    }
}
