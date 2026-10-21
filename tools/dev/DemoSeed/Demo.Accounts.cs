using System.Text;

namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    /// <summary>
    /// The bootstrap Executive (made by the API from Access:Bootstrap on an empty database) creates every other account
    /// through <c>POST /api/users</c>. Each account is then signed in once and changes its password through the normal
    /// forced change, so every demo user can sign in with the demo password, like any real user.
    /// </summary>
    private async Task AccountsAsync()
    {
        Log.Section("Accounts");
        var bootstrapEmail = env["Access__Bootstrap__Email"];
        var bootstrap = await SignInAsync("bootstrap", bootstrapEmail, env["Access__Bootstrap__Password"]);
        _userIds["bootstrap"] = bootstrap.UserId;

        foreach (var person in Cast.Everyone)
        {
            var found = (await Admin.GetAsync($"/api/users?search={Uri.EscapeDataString(person.Email)}&pageSize=5"))
                .Items().FirstOrDefault(user => string.Equals(user.Str("email"), person.Email, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                var temporary = NewTemporaryPassword();
                accounts[$"TEMP_{person.Key}"] = temporary;
                SaveAccounts();
                await Admin.PostAsync("/api/users", new
                {
                    email = person.Email,
                    phone = person.Phone,
                    nameAr = person.NameAr,
                    nameEn = person.NameEn,
                    jobTitleAr = person.JobTitleAr,
                    jobTitleEn = person.JobTitleEn,
                    role = person.Role,
                    initialPassword = temporary,
                });
                Log.New($"account {person.Email} ({person.Role})");
            }
            else if (found.Str("role") != person.Role)
            {
                throw new InvalidOperationException($"{person.Email} exists with role {found.Str("role")}, not {person.Role}. It was changed by hand; the seed leaves it alone and stops.");
            }
            else
            {
                Log.Kept($"account {person.Email} ({person.Role})");
            }

            var session = await SignInAsync(person.Key, person.Email, accounts.GetValueOrDefault($"TEMP_{person.Key}"));
            _userIds[person.Key] = session.UserId;
        }
    }

    /// <summary>
    /// Signs in with the demo password, or with the first password (the bootstrap one or the temporary one) and then
    /// makes the forced change to the demo password.
    /// </summary>
    private async Task<Session> SignInAsync(string key, string email, string? firstPassword)
    {
        var session = new Session(http, email, DemoPassword);
        if (!await session.TryLoginAsync())
        {
            if (firstPassword is null)
            {
                throw new InvalidOperationException($"Cannot sign in as {email} with the demo password, and no first password is known. Reset it from the Users screen.");
            }

            session = new Session(http, email, firstPassword);
            await session.LoginAsync();
        }

        if (session.MustChangePassword)
        {
            await session.ChangePasswordAsync(DemoPassword);
            Log.New($"first sign-in and password change: {email}");
        }

        accounts.Remove($"TEMP_{key}");
        SaveAccounts();
        _sessions[key] = session;
        return session;
    }

    /// <summary>The handoff sheet: every demo account with what to try with it (git-ignored, next to the passwords).</summary>
    private void WriteCredentials()
    {
        var path = Path.Combine(Path.GetDirectoryName(accountsFile)!, "demo-credentials.md");
        var text = new StringBuilder();
        text.AppendLine("# Demo accounts (development only)");
        text.AppendLine();
        text.AppendLine($"API: {http.BaseAddress} · every account below uses the password: `{DemoPassword}`");
        text.AppendLine();
        text.AppendLine("| Name | Role | Sign-in e-mail | What to try |");
        text.AppendLine("| --- | --- | --- | --- |");
        text.AppendLine($"| Bootstrap Executive | executive | {env["Access__Bootstrap__Email"]} | The first account the API made; set-up only |");
        foreach (var person in Cast.Everyone)
        {
            text.AppendLine($"| {person.NameEn} ({person.NameAr}) | {person.Role} | {person.Email} | {Purpose(person)} |");
        }

        File.WriteAllText(path, text.ToString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string Purpose(Person person) => person.Key switch
    {
        "ceo" => "Executive and financial manager: treasury, approvals, price decisions, every project",
        "deputy" => "Second Executive: approves what the CEO asked for (nobody approves their own request)",
        "pm" => "Manages the Nile Tower project: reviews daily reports, labor sheets, requisitions",
        "engineer" => "Site engineer, Nile Tower: custody, expenses, labor sheets, daily reports, requisitions",
        "engineer2" => "Site engineer, Shorouk Schools",
        "accountant" => "Accountant custody, transfers, reviews of expenses, settlements, vehicle claims",
        "office" => "Office custodian: office cash, expenses with receipts, funding, cash counts",
        "hr" => "Employees, salary structures, HR requests and referrals, salary sheet",
        "purchasing" => "Quotations, purchase orders, suppliers",
        "driver" => "Driver of the pickup: fuel and vehicle expense claims, HR requests",
        "driver2" => "Driver of the truck",
        _ => "",
    };
}
