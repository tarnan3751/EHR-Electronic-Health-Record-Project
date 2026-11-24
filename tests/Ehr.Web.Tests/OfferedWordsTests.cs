using Ehr.Domain.Notes;
using Ehr.Testing;
using Xunit;

namespace Ehr.Web.Tests;

// The app's reading of an edit of a recorded visit's proposal against the words it offered (Ehr.Domain's OfferedWords,
// the expansion plan's step 16), against the same table of cases as the database's ehr.ambient_words
// (AmbientSecurityTests): both layers find each the same, or not, alike. TC-UC23-04 refuses those found the same once
// the visit's transcript is gone.
public class OfferedWordsTests
{
    [Fact]
    public void An_edit_reads_as_the_words_offered_or_not_as_the_database_reads_it()
    {
        foreach (var (name, offered, edited, same) in AmbientWordsCases.All)
        {
            Assert.True(same == OfferedWords.Same(edited, offered), $"{name}: expected {(same ? "the same" : "different")}.");
        }
    }
}
