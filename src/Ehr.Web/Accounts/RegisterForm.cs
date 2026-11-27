using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Accounts;

// What the portal's registration page posts: the address, the password twice, and the enrollment code from the clinic.
public class RegisterForm : NewPassword
{
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter an email address like name@example.com.")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Enter your enrollment code.")]
    public string? Code { get; set; }
}
