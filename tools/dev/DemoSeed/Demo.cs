using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

/// <summary>
/// The demo company, built step by step through the API. Each part lives in its own file (Demo.*.cs) and is written so
/// that a second run finds what the first one made.
/// </summary>
internal sealed partial class Demo(
    HttpClient http,
    IReadOnlyDictionary<string, string> env,
    Dictionary<string, string> accounts,
    string accountsFile,
    DateOnly runDate)
{
    /// <summary>
    /// The day every demo date is counted from: the first run's date (kept as the treasury opening date + 100 days),
    /// so a later run finds the same dated records instead of making new ones.
    /// </summary>
    private DateOnly today = runDate;

    /// <summary>Every demo record that has a free-text field carries this marker, so the seed can find it again.</summary>
    private const string Marker = "[DEMO]";

    private readonly Dictionary<string, Session> _sessions = new();
    private readonly Dictionary<string, int> _userIds = new();
    private readonly Dictionary<string, int> _employeeIds = new();

    private string DemoPassword => accounts["DEMO_PASSWORD"];

    private Session As(Person person) => _sessions[person.Key];

    private Session Admin => _sessions["bootstrap"];

    private int UserId(Person person) => _userIds[person.Key];

    private int EmployeeId(Person person) => _employeeIds[person.Key];

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");

    /// <summary>A stable idempotency key (a GUID made from the step's name): the same step always sends the same key.</summary>
    private static string Key(string step)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"almotaheda-demo-seed:{step}"));
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    public async Task RunAsync()
    {
        await AccountsAsync();
        await AnchorAsync();
        await EmployeesAsync();
        WriteCredentials();
        await PriceListAsync();
        await SuppliersAsync();
        await ProjectsAsync();
        await ItemsAsync();
        await VehiclesAsync();
        await FinanceAsync();
        await WorkforceAsync();
        await ExpensesAsync();
        await ClaimsAsync();
        await OfficeAsync();
        await HrAsync();
        await SiteAsync();
        await PricesAndProcurementAsync();
    }

    private async Task AnchorAsync()
    {
        var bank = (await As(Cast.Ceo).GetAsync("/api/treasury")).Items("accounts").Single(account => account.Str("account") == "Bank");
        if ((bool)bank["openingSet"]!)
        {
            today = DateOnly.Parse(bank.Str("openingDate")[..10]).AddDays(100);
        }

        Log.Info($"Demo dates are counted from {Iso(today)} (the first run).");
    }

    private static IEnumerable<JsonNode> List(JsonNode node) =>
        node is JsonArray array ? array.Where(item => item is not null).Select(item => item!) : node.Items();

    private void SaveAccounts() =>
        EnvFile.Write(accountsFile, accounts, "Development-only passwords of the demo accounts (git-ignored). Keep this file to keep signing in.");

    private static string NewTemporaryPassword() => $"Temp-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}";

    /// <summary>A tiny valid PNG (a coloured square) with the text of [label] in its metadata, as demo evidence.</summary>
    private static byte[] DemoPng(string label)
    {
        using var stream = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            var length = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(length);
            stream.Write(length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            var crc = BitConverter.GetBytes(Crc32([.. typeBytes, .. data]));
            if (BitConverter.IsLittleEndian) Array.Reverse(crc);
            stream.Write(crc);
        }

        const int size = 64;
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk("IHDR", [0, 0, 0, size, 0, 0, 0, size, 8, 2, 0, 0, 0]);
        Chunk("tEXt", Encoding.Latin1.GetBytes($"Comment\0DEMO evidence for development testing: {label}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(label));
        var raw = new byte[size * (size * 3 + 1)];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = y * (size * 3 + 1) + 1 + x * 3;
                raw[offset] = hash[0];
                raw[offset + 1] = (byte)(hash[1] ^ (x * 4));
                raw[offset + 2] = (byte)(hash[2] ^ (y * 4));
            }
        }

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk("IDAT", compressed.ToArray());
        }

        Chunk("IEND", []);
        return stream.ToArray();
    }

    /// <summary>A one-page PDF saying it is demo data.</summary>
    private static byte[] DemoPdf(string title)
    {
        var text = $"DEMO document for development testing only: {title}".Replace("(", "[").Replace(")", "]");
        var content = $"BT /F1 12 Tf 40 760 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
