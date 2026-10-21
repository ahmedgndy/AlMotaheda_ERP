namespace AlMotaheda.DemoSeed;

/// <summary>
/// One demo person: a sign-in account (Access) and, for everyone who works for the company, an HR employee linked to
/// it. The names are invented; the e-mail addresses use the reserved <c>.test</c> domain (RFC 2606), so nothing can
/// ever be delivered to a real person.
/// </summary>
internal sealed record Person(
    string Key,
    string Role,
    string Email,
    string NameAr,
    string NameEn,
    string JobTitleAr,
    string JobTitleEn,
    string EmployeeKind,
    string Department,
    string NationalId,
    string Phone,
    decimal BasicSalary,
    decimal Allowance);

internal static class Cast
{
    public const string Domain = "almotaheda.test";

    // Role codes as the Access module seeds them (AccessRoles).
    public static readonly Person Ceo = new("ceo", "executive", $"ceo@{Domain}",
        "كريم منصور", "Karim Mansour", "المدير التنفيذي والمدير المالي", "Chief Executive and Financial Manager",
        "OfficeEmployee", "management", "29900000000001", "01000000001", 60000, 10000);

    public static readonly Person Deputy = new("deputy", "executive", $"deputy.ceo@{Domain}",
        "سامية رشدي", "Samia Roshdy", "نائب المدير التنفيذي", "Deputy Chief Executive",
        "OfficeEmployee", "management", "29900000000002", "01000000002", 45000, 8000);

    public static readonly Person Manager = new("pm", "manager", $"pm@{Domain}",
        "محمود فتحي", "Mahmoud Fathy", "مدير مشروعات", "Project Manager",
        "Engineer", "projects", "29900000000003", "01000000003", 35000, 5000);

    public static readonly Person Engineer = new("engineer", "engineer", $"engineer@{Domain}",
        "عمر حسن", "Omar Hassan", "مهندس موقع", "Site Engineer",
        "Engineer", "projects", "29900000000004", "01000000004", 18000, 3000);

    public static readonly Person Engineer2 = new("engineer2", "engineer", $"engineer2@{Domain}",
        "يوسف عادل", "Youssef Adel", "مهندس موقع", "Site Engineer",
        "Engineer", "projects", "29900000000005", "01000000005", 17000, 3000);

    public static readonly Person Accountant = new("accountant", "accountant", $"accountant@{Domain}",
        "منى سمير", "Mona Samir", "محاسبة", "Accountant",
        "OfficeEmployee", "finance", "29900000000006", "01000000006", 16000, 2000);

    public static readonly Person OfficeManager = new("office", "office_admin", $"office@{Domain}",
        "هبة علي", "Heba Ali", "مديرة المكتب", "Office Manager",
        "OfficeEmployee", "admin", "29900000000007", "01000000007", 12000, 1500);

    public static readonly Person Hr = new("hr", "hr", $"hr@{Domain}",
        "نور إبراهيم", "Nour Ibrahim", "أخصائية موارد بشرية", "HR Specialist",
        "OfficeEmployee", "hr", "29900000000008", "01000000008", 14000, 1500);

    public static readonly Person Purchasing = new("purchasing", "purchasing", $"purchasing@{Domain}",
        "طارق سعيد", "Tarek Said", "مسؤول مشتريات", "Purchasing Officer",
        "OfficeEmployee", "procurement", "29900000000009", "01000000009", 13000, 1500);

    // Drivers have the Staff role: fuel View + Create, fleet View, custody View + Create (PermissionCatalog).
    public static readonly Person Driver = new("driver", "staff", $"driver@{Domain}",
        "سيد عبد الرحمن", "Sayed Abdelrahman", "سائق", "Driver",
        "Staff", "fleet", "29900000000010", "01000000010", 8000, 1000);

    public static readonly Person Driver2 = new("driver2", "staff", $"driver2@{Domain}",
        "أشرف كمال", "Ashraf Kamal", "سائق", "Driver",
        "Staff", "fleet", "29900000000011", "01000000011", 7500, 1000);

    public static readonly IReadOnlyList<Person> Everyone =
        [Ceo, Deputy, Manager, Engineer, Engineer2, Accountant, OfficeManager, Hr, Purchasing, Driver, Driver2];

    /// <summary>The HR departments the employees belong to (key, Arabic, English).</summary>
    public static readonly IReadOnlyList<(string Key, string Ar, string En)> Departments =
    [
        ("management", "الإدارة العليا", "Management"),
        ("projects", "المشروعات", "Projects"),
        ("finance", "الحسابات", "Finance"),
        ("admin", "الشؤون الإدارية", "Administration"),
        ("hr", "الموارد البشرية", "Human Resources"),
        ("procurement", "المشتريات", "Procurement"),
        ("fleet", "الحركة والنقل", "Fleet and Transport"),
    ];
}
