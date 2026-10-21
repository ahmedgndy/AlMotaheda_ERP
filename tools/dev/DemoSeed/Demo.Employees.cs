namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    /// <summary>HR departments, an employee linked to every account, and a salary structure each (the HR user's work).</summary>
    private async Task EmployeesAsync()
    {
        Log.Section("HR employees");
        var hr = As(Cast.Hr);

        var departments = List(await hr.GetAsync("/api/hr/departments")).ToList();
        var departmentIds = new Dictionary<string, int>();
        foreach (var (key, ar, en) in Cast.Departments)
        {
            var found = departments.FirstOrDefault(department => department.Str("nameEn") == en);
            if (found is null)
            {
                found = await hr.PostAsync("/api/hr/departments", new { nameAr = ar, nameEn = en, isActive = true });
                Log.New($"department {en}");
            }
            else
            {
                Log.Kept($"department {en}");
            }

            departmentIds[key] = found.Id();
        }

        var hireDate = today.AddYears(-2);
        foreach (var person in Cast.Everyone)
        {
            var employee = (await hr.GetAsync($"/api/hr/employees?search={person.NationalId}&status=Active"))
                .Items().FirstOrDefault(item => item.Str("nationalId") == person.NationalId);
            if (employee is null)
            {
                employee = await hr.PostAsync("/api/hr/employees", new
                {
                    nameAr = person.NameAr,
                    nameEn = person.NameEn,
                    nationalId = person.NationalId,
                    phone = person.Phone,
                    email = person.Email,
                    jobTitle = person.JobTitleEn,
                    departmentId = departmentIds[person.Department],
                    kind = person.EmployeeKind,
                    hireDate = Iso(hireDate),
                    userId = UserId(person),
                    notes = $"{Marker} demo employee",
                });
                Log.New($"employee {employee.Str("employeeNo")} {person.NameEn} -> user {UserId(person)}");
            }
            else
            {
                Log.Kept($"employee {employee.Str("employeeNo")} {person.NameEn}");
            }

            _employeeIds[person.Key] = employee.Id();

            var salaries = List(await hr.GetAsync($"/api/hr/employees/{employee.Id()}/salaries")).ToList();
            if (salaries.Count == 0)
            {
                await hr.PostAsync($"/api/hr/employees/{employee.Id()}/salaries", new
                {
                    effectiveFrom = Iso(hireDate),
                    basicSalary = person.BasicSalary,
                    fixedAllowance = person.Allowance,
                    note = $"{Marker} starting salary",
                });
                Log.New($"salary structure {person.NameEn}: {person.BasicSalary:N0} + {person.Allowance:N0}");
            }
            else
            {
                Log.Kept($"salary structure {person.NameEn}");
            }
        }
    }
}
