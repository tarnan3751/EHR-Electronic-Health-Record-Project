using System.ComponentModel.DataAnnotations;

namespace Ehr.Web.Accounts;

// What the staff admin page posts to create an account.
public class InviteForm
{
    [Required(ErrorMessage = "Enter the person's email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Enter the name to show.")]
    [StringLength(200, ErrorMessage = "Use 200 characters or fewer.")]
    public string? DisplayName { get; set; }

    public bool Clinician { get; set; }

    public bool Admin { get; set; }
}
