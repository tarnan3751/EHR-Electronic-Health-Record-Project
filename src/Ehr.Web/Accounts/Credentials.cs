using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Accounts;

// What a sign-in form posts, for staff and patients alike.
public class Credentials
{
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter an email address like name@example.com.")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Enter your password.")]
    public string? Password { get; set; }
}
