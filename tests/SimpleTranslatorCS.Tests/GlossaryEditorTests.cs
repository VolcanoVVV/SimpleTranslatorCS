using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TranslationFoundation;
namespace SimpleTranslatorCS.Tests;
[TestClass]
public class GlossaryEditorTests
{
    [TestMethod] public void EditableRoundtripKeepsSettingsCommentsAndDisabledRows()
    {
        var doc = GlossaryDocument.Parse("\uFEFF# note\r\n#@maxcharacters=700\r\nGroundZero\t中心区\r\n#~\traid\t战局\r\n");
        Assert.AreEqual(700, doc.MaxCharacters); Assert.AreEqual(24, doc.MaxEntries);
        Assert.AreEqual(2, doc.Rows.Count); Assert.IsFalse(doc.Rows[1].Enabled);
        Assert.AreEqual("# note", doc.Comments.Single());
        Assert.AreEqual(doc.Serialize(), GlossaryDocument.Parse(doc.Serialize()).Serialize());
        StringAssert.Contains(doc.Preview("GroundZero raid", 8000), "中心区");
        Assert.IsFalse(doc.Preview("GroundZero raid", 8000).Contains("战局"));
        doc.Rows[1].Enabled = true; StringAssert.Contains(doc.Preview("raid", 8000), "战局");
    }
    [TestMethod] public void DraftValidationPreventsHiddenControlsDuplicateSourcesAndOversizedData()
    {
        foreach (var raw in new[] {"#~\tbad", "#~\t\t空", "raid\t战局\nRAID\t重复", "#@maxentries=65", "#@maxcharacters=-1", "#@unknown=0"})
            Assert.ThrowsException<InvalidDataException>(() => GlossaryDocument.Parse(raw));
        var doc = GlossaryDocument.Parse("raid\t战局");
        doc.Rows[0].Source = "#turn-into-comment"; Assert.ThrowsException<InvalidDataException>(() => doc.Serialize());
        doc.Rows[0].Source = "raid\tcontrol"; Assert.ThrowsException<InvalidDataException>(() => doc.Serialize());
        Assert.ThrowsException<InvalidDataException>(() => GlossaryDocument.Parse("#" + new string('中', 100000)));
        doc.Rows.Clear(); for(var i=0;i<2001;i++) doc.Rows.Add(new GlossaryRow { Source="r"+i, Target="战局", Enabled=false });
        Assert.ThrowsException<InvalidDataException>(() => doc.Serialize());
    }
    [TestMethod] public void EmptyAndDisabledGlossariesStayEmptyAndPreviewHonorsJobBudget()
    {
        var doc = GlossaryDocument.Parse("Ground Zero\t中心区\nraid\t战局");
        Assert.AreEqual("", doc.Preview("GroundZero",8000)); // compact alias requires its explicit row
        StringAssert.Contains(doc.Preview("GROUND_ZERO",8000), "中心区");
        Assert.AreEqual("", doc.Preview("Ground Zero",100));
        Assert.AreEqual("", doc.Preview("raider",8000));
        doc.Enabled=false; Assert.AreEqual("",doc.Preview("raid",8000));
        doc.Enabled=true; doc.MaxEntries=0; Assert.AreEqual("",doc.Preview("raid",8000));
        doc.Rows.Clear(); Assert.AreEqual(0,GlossaryDocument.Parse(doc.Serialize()).Rows.Count);
    }
}
