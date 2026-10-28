using System.Globalization;
using System.Text;

namespace SecureDocs.Shared;

/// <summary>
/// Generates fake patient/member data. Every value is random — no real PHI is ever used by this demo.
/// </summary>
public static class SyntheticData
{
    static readonly string[] FirstNames =
    [
        "James", "Maria", "Robert", "Linda", "Michael", "Patricia", "David", "Jennifer", "William", "Elizabeth",
        "Carlos", "Aisha", "Wei", "Priya", "Daniel", "Sofia", "Kevin", "Grace", "Thomas", "Nora", "Andre", "Mei",
        "Joseph", "Fatima", "Samuel", "Olivia", "Hector", "Emma", "Raj", "Chloe"
    ];

    public static readonly string[] LastNames =
    [
        "Smith", "Johnson", "Williams", "Garcia", "Martinez", "Nguyen", "Brown", "Davis", "Lopez", "Wilson",
        "Anderson", "Patel", "Kim", "Thompson", "Hernandez", "Moore", "Chen", "Jackson", "Rivera", "Lee",
        "Walker", "Young", "Okafor", "Singh", "Scott", "Torres", "Hill", "Flores", "Adams", "Baker"
    ];

    static readonly string[] Streets =
    [
        "Oak St", "Maple Ave", "Cedar Ln", "Pine Rd", "Elm Dr", "Harbor Blvd", "Sunset Way", "Mission St",
        "Lakeview Ter", "Valley Rd", "Park Pl", "Hillcrest Ave", "Bayshore Dr", "Canyon Rd", "Market St"
    ];

    static readonly (string City, string State, string ZipPrefix)[] Cities =
    [
        ("Oakland", "CA", "946"), ("Sacramento", "CA", "958"), ("San Diego", "CA", "921"), ("Fresno", "CA", "937"),
        ("Portland", "OR", "972"), ("Seattle", "WA", "981"), ("Denver", "CO", "802"), ("Atlanta", "GA", "303"),
        ("Honolulu", "HI", "968"), ("Rockville", "MD", "208"), ("Arlington", "VA", "222"), ("Pasadena", "CA", "911")
    ];

    public static string RandomName(Random? r = null)
    {
        r ??= Random.Shared;
        return $"{FirstNames[r.Next(FirstNames.Length)]} {LastNames[r.Next(LastNames.Length)]}";
    }

    public static string RandomLastName() => LastNames[Random.Shared.Next(LastNames.Length)];

    public static List<DocumentRequest> GenerateDocuments(int count, Random? r = null)
    {
        r ??= Random.Shared;
        var list = new List<DocumentRequest>(count);
        for (var i = 0; i < count; i++)
        {
            var name = RandomName(r);
            var (city, state, zip) = Cities[r.Next(Cities.Length)];
            var type = DocumentTypes.All[r.Next(DocumentTypes.All.Length)];
            // Physical items (checks, cards, ID cards) always print; statements can go digital.
            var channel = type is DocumentTypes.Check or DocumentTypes.CreditCard or DocumentTypes.MemberIdCard
                ? Channels.Print
                : r.Next(10) switch { < 6 => Channels.Print, < 9 => Channels.Email, _ => Channels.Sms };
            var handle = name.ToLowerInvariant().Replace(' ', '.');
            list.Add(new DocumentRequest
            {
                DocumentType = type,
                Channel = channel,
                RecipientName = name,
                AddressLine1 = $"{r.Next(100, 9999)} {Streets[r.Next(Streets.Length)]}",
                City = city,
                State = state,
                PostalCode = zip + r.Next(10, 99).ToString(CultureInfo.InvariantCulture),
                MemberId = $"M{r.Next(10000000, 99999999)}",
                Email = $"{handle}@example.com",
                Phone = $"555-{r.Next(100, 999)}-{r.Next(1000, 9999)}",
                Amount = type is DocumentTypes.Check or DocumentTypes.MedicalStatement or DocumentTypes.ExplanationOfBenefits
                    ? Math.Round((decimal)(r.NextDouble() * 2500), 2)
                    : null
            });
        }
        return list;
    }

    public const string CsvHeader =
        "DocumentType,Channel,RecipientName,AddressLine1,City,State,PostalCode,MemberId,Email,Phone,Amount";

    public static string ToCsv(IEnumerable<DocumentRequest> docs)
    {
        var sb = new StringBuilder().AppendLine(CsvHeader);
        foreach (var d in docs)
        {
            sb.AppendLine(string.Join(',', d.DocumentType, d.Channel, d.RecipientName, d.AddressLine1, d.City,
                d.State, d.PostalCode, d.MemberId, d.Email, d.Phone,
                d.Amount?.ToString(CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }
}
