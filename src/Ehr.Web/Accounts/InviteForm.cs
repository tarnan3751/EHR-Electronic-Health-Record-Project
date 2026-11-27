using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Accounts;

// What the staff admin page posts to create an account.
public class InviteForm
{
    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Email must be in the format name@example.com.")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Display name is required.")]
    [StringLength(200, ErrorMessage = "Display name must be 200 characters or fewer.")]
    public string? DisplayName { get; set; }

    public bool Clinician { get; set; }

    public bool Admin { get; set; }
}
