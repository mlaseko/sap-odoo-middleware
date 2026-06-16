using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SapOdooMiddleware.Pages.Admin;

public class IndexModel : PageModel
{
    public void OnGet() => ViewData["Title"] = "Admin";
}
