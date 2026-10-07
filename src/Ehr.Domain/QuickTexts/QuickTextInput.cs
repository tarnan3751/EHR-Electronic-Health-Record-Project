using System.ComponentModel.DataAnnotations;

namespace Ehr.Domain.QuickTexts;

// A phrase as someone enters it, on the page or from the desktop app, with the rules both check.
public class QuickTextInput
{
    [Required(ErrorMessage = "Enter a shortcut.")]
    [StringLength(32, ErrorMessage = "Use 32 characters or fewer.")]
    [RegularExpression(@"\.[A-Za-z0-9_-]+", ErrorMessage = "Start with a period, then letters, numbers, - or _.")]
    public string? Shortcut { get; set; }

    [Required(ErrorMessage = "Enter the text.")]
    [StringLength(4000, ErrorMessage = "Use 4,000 characters or fewer.")]
    public string? Body { get; set; }
}
