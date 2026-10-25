using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Domain.Audit;
using Ehr.Domain.Authorization;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ehr.Domain.Accounts;

public static class AccountServiceCollectionExtensions
{
    // Accounts, sign-in and authorization: Identity for the two kinds of account, each with its own store and its own
    // cookie; the policies attached to the areas; and the services the pages call. The host adds the AuditSource and,
    // on the on-prem site, AdminBootstrap.
    public static IServiceCollection AddEhrAccounts(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BootstrapOptions>(configuration.GetSection(BootstrapOptions.Section));
        services.Configure<SiteAddresses>(configuration.GetSection(SiteAddresses.Section));

        // One IdentityOptions for both kinds of account.
        services.Configure<IdentityOptions>(options =>
        {
            // Length rather than composition rules; Identity's default of six characters is too short here.
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.AllowedForNewUsers = true;
            options.User.RequireUniqueEmail = true;
        });

        // The explicit store types matter: the default registration would give both kinds of account the same claim,
        // login and token entities, which can only map to one table.
        services.AddIdentityCore<StaffUser>()
            .AddUserStore<UserOnlyStore<StaffUser, EhrDbContext, Guid, StaffUserClaim, StaffUserLogin, StaffUserToken>>()
            .AddClaimsPrincipalFactory<StaffClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();
        services.AddIdentityCore<PatientUser>()
            .AddUserStore<UserOnlyStore<PatientUser, EhrDbContext, Guid, PatientUserClaim, PatientUserLogin, PatientUserToken>>()
            .AddClaimsPrincipalFactory<PatientClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        services.AddAuthentication(AccountSchemes.Default)
            .AddPolicyScheme(AccountSchemes.Default, "Staff or patient cookie, by path",
                options => options.ForwardDefaultSelector = AccountSchemes.Select)
            .AddCookie(AccountSchemes.Staff, options =>
            {
                Cookie(options, "__Host-Ehr.Staff", TimeSpan.FromMinutes(30));
                options.LoginPath = "/sign-in";
                options.LogoutPath = "/sign-out";
                options.AccessDeniedPath = "/denied";
                options.Events.OnValidatePrincipal = CookieSecurityStamp.ValidateAsync<StaffUser>;
            })
            .AddCookie(AccountSchemes.Patient, options =>
            {
                Cookie(options, "__Host-Ehr.Patient", TimeSpan.FromMinutes(20));
                options.LoginPath = "/portal/sign-in";
                options.LogoutPath = "/portal/sign-out";
                options.AccessDeniedPath = "/portal/denied";
                options.Events.OnValidatePrincipal = CookieSecurityStamp.ValidateAsync<PatientUser>;
            })
            .AddCookie(AccountSchemes.StaffPending, options =>
            {
                // Between the password and the second factor. Ten minutes, not extended.
                Cookie(options, "__Host-Ehr.StaffPending", TimeSpan.FromMinutes(10));
                options.SlidingExpiration = false;
                options.LoginPath = "/sign-in";
            });

        services.AddAuthorization(options => options.AddEhrPolicies());

        services.AddScoped<AuditRecorder>();
        services.AddScoped<PhiReader>();
        services.AddScoped<StaffSignIn>();
        services.AddScoped<StaffAccounts>();
        services.AddScoped<PatientSignIn>();
        services.AddScoped<PatientRegistration>();
        services.AddScoped<PatientEnrollments>();
        return services;
    }

    // Every cookie: host-bound (the __Host- prefix needs Secure, Path=/ and no Domain), never readable by scripts,
    // never sent cross-site, and idle-limited, with the lifetime extended by use.
    static void Cookie(CookieAuthenticationOptions options, string name, TimeSpan lifetime)
    {
        options.Cookie.Name = name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = lifetime;
        options.SlidingExpiration = true;
    }
}
