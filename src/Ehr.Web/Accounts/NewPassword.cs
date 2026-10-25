using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Accounts;

// A password being chosen, typed twice. Identity applies its own rules as well (AddEhrAccounts); these give the
// immediate answers.
public class NewPassword
{
    [Required(ErrorMessage = "Enter a new password.")]
    [MinLength(12, ErrorMessage = "Use at least 12 characters.")]
    public string? Password { get; set; }

    [Required(ErrorMessage = "Enter the password again.")]
    [Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
    public string? Confirm { get; set; }
}
