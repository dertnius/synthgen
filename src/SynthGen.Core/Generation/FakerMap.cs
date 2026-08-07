using Bogus;

namespace SynthGen.Core.Generation;

/// <summary>Curated map from "category.method" names in the rules YAML to Bogus generators.</summary>
public static class FakerMap
{
    private static readonly Dictionary<string, Func<Faker, object>> Map =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["name.firstName"] = f => f.Name.FirstName(),
        ["name.lastName"] = f => f.Name.LastName(),
        ["name.fullName"] = f => f.Name.FullName(),
        ["name.jobTitle"] = f => f.Name.JobTitle(),
        ["internet.email"] = f => f.Internet.Email(),
        ["internet.userName"] = f => f.Internet.UserName(),
        ["internet.url"] = f => f.Internet.Url(),
        ["internet.ip"] = f => f.Internet.Ip(),
        ["internet.domainName"] = f => f.Internet.DomainName(),
        ["phone.phoneNumber"] = f => f.Phone.PhoneNumber(),
        ["address.city"] = f => f.Address.City(),
        ["address.country"] = f => f.Address.Country(),
        ["address.countryCode"] = f => f.Address.CountryCode(),
        ["address.state"] = f => f.Address.State(),
        ["address.zipCode"] = f => f.Address.ZipCode(),
        ["address.streetAddress"] = f => f.Address.StreetAddress(),
        ["address.fullAddress"] = f => f.Address.FullAddress(),
        ["address.latitude"] = f => f.Address.Latitude(),
        ["address.longitude"] = f => f.Address.Longitude(),
        ["company.companyName"] = f => f.Company.CompanyName(),
        ["company.catchPhrase"] = f => f.Company.CatchPhrase(),
        ["commerce.productName"] = f => f.Commerce.ProductName(),
        ["commerce.department"] = f => f.Commerce.Department(),
        ["commerce.color"] = f => f.Commerce.Color(),
        ["commerce.ean13"] = f => f.Commerce.Ean13(),
        ["lorem.word"] = f => f.Lorem.Word(),
        ["lorem.sentence"] = f => f.Lorem.Sentence(),
        ["lorem.paragraph"] = f => f.Lorem.Paragraph(),
        ["finance.iban"] = f => f.Finance.Iban(),
        ["finance.currencyCode"] = f => f.Finance.Currency().Code,
        ["finance.accountName"] = f => f.Finance.AccountName(),
        ["vehicle.manufacturer"] = f => f.Vehicle.Manufacturer(),
        ["vehicle.model"] = f => f.Vehicle.Model(),
        ["vehicle.vin"] = f => f.Vehicle.Vin(),
    };

    public static Func<Faker, object> Resolve(string method)
    {
        if (Map.TryGetValue(method, out var fn)) return fn;
        var known = string.Join(", ", Map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        throw new GenerationException($"Unknown faker method '{method}'. Available: {known}");
    }

    public static IEnumerable<string> KnownMethods => Map.Keys;
}
