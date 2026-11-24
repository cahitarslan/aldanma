using Aldanma.Core.Identifiers;

namespace Aldanma.Tests;

public class IdentifierTests
{
    private static readonly InputParser Parser = InputParser.Default();
    private static readonly IdentifierRules Rules = IdentifierRules.Embedded();

    [Theory]
    [InlineData("0532 123 45 67", "phone")]
    [InlineData("TR85 0001 0000 0000 0012 3456 78", "iban")]
    [InlineData("https://example.xyz/login", "domain")]
    [InlineData("Ara: 0532 123 45 67", null)]
    [InlineData("0532 123 45 67 ve 0533 765 43 21", null)]
    [InlineData("", null)]
    public void ParseSingle_accepts_exactly_one_identifier(string input, string? kind)
    {
        Assert.Equal(kind, Parser.ParseSingle(input)?.Kind);
        Assert.Null(Parser.ParseSingle("example.xyz/" + new string('a', InputParser.MaxSingleInputLength)));
    }

    [Theory]
    [InlineData("0532 123 45 67", "+905321234567", PhoneType.Mobile)]
    [InlineData("+90 (532) 123-45-67", "+905321234567", PhoneType.Mobile)]
    [InlineData("905321234567", null, null)]
    [InlineData("5321234567", "+905321234567", PhoneType.Mobile)]
    [InlineData("0312 555 12 34", "+903125551234", PhoneType.Landline)]
    [InlineData("0850 222 00 00", "+908502220000", PhoneType.NonGeographic)]
    [InlineData("0900 111 22 33", "+909001112233", PhoneType.Premium)]
    [InlineData("444 0 123", "+904440123", PhoneType.BusinessShort)]
    [InlineData("+44 7911 123456", "+447911123456", PhoneType.Foreign)]
    [InlineData("0044 7911 123456", "+447911123456", PhoneType.Foreign)]
    [InlineData("3340", "sc:3340", PhoneType.ShortCode)]
    [InlineData("0132 12", null, null)]
    public void Phone_numbers_are_normalised_to_E164(string input, string? e164, PhoneType? type)
    {
        var p = PhoneNumber.Parse(input);
        Assert.Equal(e164, p?.E164);
        Assert.Equal(type, p?.Type);
    }

    [Theory]
    [InlineData("TR85 0001 0000 0000 0012 3456 78", true)]
    [InlineData("tr850001000000000012345678", true)]
    [InlineData("TR86 0001 0000 0000 0012 3456 78", false)]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    [InlineData("TR85 0001 0000 0000 0012 3456 7", false)]
    public void Iban_checksum_is_validated(string input, bool valid)
    {
        var n = Iban.Normalize(input);
        Assert.NotNull(n);
        Assert.Equal(valid, Iban.IsValid(n, Rules.IbanLengths));
    }

    [Fact]
    public void Iban_bank_code_and_format()
    {
        Assert.Equal("00010", Iban.TurkishBankCode("TR850001000000000012345678"));
        Assert.Equal("TR85 0001 0000 0000 0012 3456 78", Iban.Format("TR850001000000000012345678"));
    }

    [Theory]
    [InlineData("0532 123 45 67", InputMode.Phone, "phone", "+905321234567")]
    [InlineData("TR85 0001 0000 0000 0012 3456 78", InputMode.Iban, "iban", "TR850001000000000012345678")]
    [InlineData("https://www.uyap-dosyasorgu.com/icra?x=1", InputMode.Link, "domain", "uyap-dosyasorgu.com")]
    [InlineData("kampanya.garanti-bonus.com.tr", InputMode.Link, "domain", "garanti-bonus.com.tr")]
    [InlineData("bit.ly/3abcXYZ", InputMode.Link, "url", "bit.ly/3abcxyz")]
    [InlineData("https://odeme-ptt.netlify.app/giris", InputMode.Link, "domain", "odeme-ptt.netlify.app")]
    [InlineData("https://sites.google.com/view/kargo-takip/giris", InputMode.Link, "url", "sites.google.com/view/kargo-takip")]
    [InlineData("https://wa.me/905321234567", InputMode.Link, "phone", "+905321234567")]
    [InlineData("sahte-magaza.shop", InputMode.Link, "domain", "sahte-magaza.shop")]
    public void Single_inputs_are_recognised(string input, string mode, string kind, string value)
    {
        var p = Parser.Parse(input);
        Assert.Equal(mode, p.Mode);
        var id = Assert.Single(p.Identifiers);
        Assert.Equal(kind, id.Kind);
        Assert.Equal(value, id.Value);
    }

    [Fact]
    public void All_identifiers_are_extracted_from_a_listing()
    {
        const string listing = """
            Sahibinden temiz iPhone 15, 18.000 TL. Yurt dışındayım, kargo ile gönderiyorum.
            Kaporayı TR85 0001 0000 0000 0012 3456 78 hesabına yatırın, detay için 0532 123 45 67 ya da
            +44 7911 123456 WhatsApp. Kargo takibi: https://kargo-takip-ptt.xyz/takip
            """;
        var p = Parser.Parse(listing);
        Assert.Equal(InputMode.Text, p.Mode);
        Assert.Equal(["TR850001000000000012345678", "+905321234567", "+447911123456", "kargo-takip-ptt.xyz"], p.Identifiers.Select(i => i.Value));
    }

    [Fact]
    public void Masked_forms_hide_the_middle()
    {
        Assert.Equal("+90532 *** 45 67", new Identifier(IdentifierKind.Phone, "+905321234567", "").Masked);
        Assert.Equal("TR85 **** **** 5678", new Identifier(IdentifierKind.Iban, "TR850001000000000012345678", "").Masked);
    }

    [Fact]
    public void MaskIdentifiers_hides_numbers_and_ibans_but_keeps_links()
    {
        var masked = InputParser.MaskIdentifiers("IBAN: TR85 0001 0000 0000 0012 3456 78, tel 0532 123 45 67, site: ornek-kargo.xyz");
        Assert.Equal("IBAN: TR85 **** **** 5678, tel +90532 *** 45 67, site: ornek-kargo.xyz", masked);
    }
}
