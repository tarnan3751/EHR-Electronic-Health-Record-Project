using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Accounts;

// What the portal's invitation page posts (step 6b): the code from the emailed link, the name the new account's
// holder goes by, and the password twice. The address is the one the invitation was sent to, never typed.
public class InvitationForm : NewPassword
{
    public string? Code { get; set; }

    [Required(ErrorMessage = "Enter your name.")]
    [StringLength(100, ErrorMessage = "Name must be 100 characters or fewer.")]
    public string? Name { get; set; }
}
