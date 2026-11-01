using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Accounts;

// What a sign-in form posts, for staff and patients alike.
public class Credentials
{
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Enter your password.")]
    public string? Password { get; set; }
}
