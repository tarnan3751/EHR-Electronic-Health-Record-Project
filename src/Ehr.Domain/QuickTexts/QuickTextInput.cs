using System.ComponentModel.DataAnnotations;

namespace Ehr.Domain.QuickTexts;

// A phrase as someone enters it, on the page or from the desktop app, with the rules both check.
public class QuickTextInput
{
    [Required(ErrorMessage = "Shortcut is required.")]
    [StringLength(32, ErrorMessage = "Shortcut must be 32 characters or fewer.")]
    [RegularExpression(@"\.[A-Za-z0-9_-]+", ErrorMessage = "Shortcut must start with a period, then letters, numbers, - or _.")]
    public string? Shortcut { get; set; }

    [Required(ErrorMessage = "Text is required.")]
    [StringLength(4000, ErrorMessage = "Text must be 4,000 characters or fewer.")]
    public string? Body { get; set; }
}
