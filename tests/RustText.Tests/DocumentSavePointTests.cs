using AvaloniaEdit.Document;
using RustText.Demo;
using RustText.Demo.Editor;
using Xunit;

namespace RustText.Tests;

public sealed class DocumentSavePointTests
{
    private static readonly EditorFileFormat Format = new(TextEncodingKind.Utf8, LineEndingKind.Lf);

    [Fact]
    public void DistantEditsStayDirtyUntilBothMatchTheSavedVersion()
    {
        var document = new TextDocument("α" + new string('x', 8192) + "\nω");
        var history = DocumentFormatHistory.For(document, Format);
        history.MarkSaved(Format);
        using (document.RunUpdate())
        {
            document.Replace(0, 1, "A");
            document.Replace(document.TextLength - 1, 1, "Z");
        }
        Assert.False(history.IsSaved);

        document.Replace(0, 1, "α");
        Assert.False(history.IsSaved);
        document.Replace(document.TextLength - 1, 1, "ω");
        Assert.True(history.IsSaved);
    }

    [Fact]
    public void CompletedSaveDoesNotClearEditsMadeDuringTheWrite()
    {
        var document = new TextDocument("saved");
        var history = DocumentFormatHistory.For(document, Format);
        var snapshot = document.CreateSnapshot(0, document.TextLength);
        document.Replace(4, 1, "r");

        history.MarkSaved(snapshot, Format, textIsCurrent: false);
        Assert.False(history.IsSaved);
        document.Replace(4, 1, "d");
        Assert.True(history.IsSaved);
    }

    [Fact]
    public void RetypingOneCharacterDoesNotCopyALargeDocument()
    {
        var document = new TextDocument(string.Concat(Enumerable.Repeat(new string('x', 127) + "\n", 8192)));
        var history = DocumentFormatHistory.For(document, Format);
        history.MarkSaved(Format);
        var offset = document.TextLength - 2;
        Retype();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 8; index++) Retype();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(history.IsSaved);
        Assert.True(allocated < document.TextLength * sizeof(char), $"Allocated {allocated} bytes for eight edits.");

        void Retype()
        {
            document.Remove(offset, 1);
            document.Insert(offset, "x");
        }
    }

    [Fact]
    public void SavedStateMatchesExactComparisonAcrossBatchedEditsAndUndo()
    {
        var random = new Random(319);
        var document = new TextDocument(new string('x', 2048));
        var history = DocumentFormatHistory.For(document, Format);
        var saved = document.Text;
        history.MarkSaved(Format);
        for (var index = 0; index < 300; index++)
        {
            if (index % 17 == 0)
            {
                document.UndoStack.Undo();
            }
            else if (index % 19 == 0)
            {
                document.UndoStack.Redo();
            }
            else
            {
                using (document.RunUpdate())
                {
                    if (index % 31 == 0)
                        document.Replace(0, document.TextLength, saved);
                    else
                        for (var edit = 0; edit < 3; edit++)
                        {
                            var offset = random.Next(document.TextLength + 1);
                            var removed = random.Next(Math.Min(10, document.TextLength - offset) + 1);
                            document.Replace(offset, removed, new string((char)('x' + random.Next(3)), random.Next(10)));
                        }
                }
            }

            Assert.Equal(string.Equals(saved, document.Text, StringComparison.Ordinal), history.IsSaved);
            if (index % 53 != 0) continue;
            saved = document.Text;
            history.MarkSaved(Format);
            Assert.True(history.IsSaved);
        }
    }
}
