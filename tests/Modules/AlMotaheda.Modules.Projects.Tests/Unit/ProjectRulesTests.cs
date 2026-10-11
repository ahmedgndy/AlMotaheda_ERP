using System.Security.Claims;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Members;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Tests.Unit;

/// <summary>Who sees which projects, who may edit them, and who may hold each responsibility.</summary>
public sealed class ProjectRulesTests
{
    [Theory]
    [InlineData(AccessRoles.Executive, true)]
    [InlineData(AccessRoles.Accountant, true)]
    [InlineData(AccessRoles.Manager, false)]
    [InlineData(AccessRoles.Engineer, false)]
    [InlineData(AccessRoles.Staff, false)]
    [InlineData(AccessRoles.OfficeAdmin, false)]
    public void Only_executive_and_accountant_see_every_project(string role, bool companyWide)
    {
        Assert.Equal(companyWide, ProjectScopes.IsCompanyWide(role));
    }

    [Theory]
    [InlineData(nameof(MemberResponsibility.ProjectManager), AccessRoles.Executive, true)]
    [InlineData(nameof(MemberResponsibility.ProjectManager), AccessRoles.Manager, true)]
    [InlineData(nameof(MemberResponsibility.ProjectManager), AccessRoles.Engineer, false)]
    [InlineData(nameof(MemberResponsibility.ProjectManager), AccessRoles.Accountant, false)]
    [InlineData(nameof(MemberResponsibility.SiteEngineer), AccessRoles.Engineer, true)]
    [InlineData(nameof(MemberResponsibility.SiteEngineer), AccessRoles.Manager, false)]
    [InlineData(nameof(MemberResponsibility.TeamMember), AccessRoles.Staff, true)]
    [InlineData(nameof(MemberResponsibility.TeamMember), AccessRoles.Manager, true)]
    [InlineData(nameof(MemberResponsibility.TeamMember), AccessRoles.Engineer, true)]
    [InlineData(nameof(MemberResponsibility.TeamMember), AccessRoles.Executive, false)]
    [InlineData(nameof(MemberResponsibility.TeamMember), AccessRoles.OfficeAdmin, false)]
    [InlineData(nameof(MemberResponsibility.TeamMember), AccessRoles.Accountant, false)]
    public void Each_responsibility_fits_the_prototypes_roles(string responsibility, string role, bool fits)
    {
        Assert.Equal(fits, ResponsibilityRoles.Fits(Enum.Parse<MemberResponsibility>(responsibility), role));
    }

    [Fact]
    public void An_executive_may_edit_any_project_even_a_completed_one()
    {
        var executive = Caller(AccessRoles.Executive, "VCEAF");

        Assert.True(ProjectRules.CanEdit(executive, null, ProjectStatus.Completed));
        Assert.True(ProjectRules.CanChangeCode(executive));
        Assert.True(ProjectRules.CanManageMembers(executive));
    }

    [Fact]
    public void A_manager_edits_only_as_the_projects_manager_and_never_a_completed_project()
    {
        var manager = Caller(AccessRoles.Manager, "VCEF");

        Assert.True(ProjectRules.CanEdit(manager, MemberResponsibility.ProjectManager, ProjectStatus.Active));
        Assert.False(ProjectRules.CanEdit(manager, MemberResponsibility.TeamMember, ProjectStatus.Active));
        Assert.False(ProjectRules.CanEdit(manager, null, ProjectStatus.Active));
        Assert.False(ProjectRules.CanEdit(manager, MemberResponsibility.ProjectManager, ProjectStatus.Completed));
        Assert.Equal(EditDecision.ProjectCompleted, ProjectRules.Edit(manager, MemberResponsibility.ProjectManager, ProjectStatus.Completed));
        Assert.Equal(EditDecision.NotAllowed, ProjectRules.Edit(manager, MemberResponsibility.TeamMember, ProjectStatus.Completed));
        Assert.False(ProjectRules.CanChangeCode(manager));
        Assert.False(ProjectRules.CanManageMembers(manager));
    }

    [Fact]
    public void Being_the_manager_does_not_replace_the_edit_permission()
    {
        // An Executive can take Edit away from one manager; the responsibility doesn't bring it back.
        var managerWithoutEdit = Caller(AccessRoles.Manager, "V");

        Assert.False(ProjectRules.CanEdit(managerWithoutEdit, MemberResponsibility.ProjectManager, ProjectStatus.Active));
    }

    [Fact]
    public void A_token_without_a_user_or_role_is_not_a_caller()
    {
        Assert.Null(ProjectCaller.From(new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "manager")], "test"))));
        Assert.Null(ProjectCaller.From(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "5")], "test"))));
    }

    [Fact]
    public void Assignment_problems_say_why()
    {
        var people = new Dictionary<int, UserSummary>
        {
            [1] = new(1, "أ", "Active Engineer", null, null, "e@x.test", null, AccessRoles.Engineer, true),
            [2] = new(2, "ب", "Disabled Manager", null, null, "m@x.test", null, AccessRoles.Manager, false),
        };

        Assert.Null(MemberAssignment.Problem(1, MemberResponsibility.SiteEngineer, people));
        Assert.Contains("must be an Executive or a Manager", MemberAssignment.Problem(1, MemberResponsibility.ProjectManager, people));
        Assert.Contains("disabled", MemberAssignment.Problem(2, MemberResponsibility.ProjectManager, people));
        Assert.Contains("not found", MemberAssignment.Problem(3, MemberResponsibility.TeamMember, people));
    }

    [Fact]
    public void Today_is_the_date_in_the_companys_time_zone()
    {
        // 22:30 UTC on 11 October is already 12 October in Cairo (UTC+3 in October 2026).
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 11, 22, 30, 0, TimeSpan.Zero));
        var calendar = new BusinessCalendar(time, Options.Create(new ProjectsOptions { TimeZone = "Africa/Cairo" }));

        Assert.Equal(new DateOnly(2026, 10, 12), calendar.Today);
        Assert.Equal(new DateTime(2026, 10, 11, 22, 30, 0, DateTimeKind.Utc), calendar.UtcNow);
    }

    [Fact]
    public void An_unknown_time_zone_stops_the_application_at_startup()
    {
        var result = new ProjectsOptionsValidator().Validate(null, new ProjectsOptions { TimeZone = "Mars/Olympus" });

        Assert.True(result.Failed);
    }

    private static ProjectCaller Caller(string role, string letters) =>
        ProjectCaller.From(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "7"),
            new Claim("role", role),
            new Claim("perm", $"projects:{letters}"),
        ], "test")))!;
}
