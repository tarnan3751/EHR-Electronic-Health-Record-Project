using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Areas.Clinical.Pages.QuickTexts;

// What the add and edit forms post.
public class QuickTextForm
{
    // Edit only: the phrase, and the version the edit started from.
    public Guid Id { get; set; }

    public int Version { get; set; }

    [Required(ErrorMessage = "Enter a shortcut.")]
    [StringLength(32, ErrorMessage = "Use 32 characters or fewer.")]
    [RegularExpression(@"\.[A-Za-z0-9_-]+", ErrorMessage = "Start with a period, then letters, numbers, - or _.")]
    public string? Shortcut { get; set; }

    [Required(ErrorMessage = "Enter the text.")]
    [StringLength(4000, ErrorMessage = "Use 4,000 characters or fewer.")]
    public string? Body { get; set; }
}
