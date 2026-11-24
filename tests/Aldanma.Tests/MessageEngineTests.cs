using Aldanma.Core.Messages;

namespace Aldanma.Tests;

public class MessageEngineTests
{
    private static readonly MessageEngine Engine = MessageEngine.Default();

    [Theory]
    [InlineData("İSTANBUL ŞİŞLİ ığdır", "istanbul sisli igdir")]
    [InlineData("T.C. Kimlik No: 1.500,50 TL'yi \"öde\"!", "t.c kimlik no 1.500 50 tl yi ode")]
    [InlineData("  çok   boşluklu\n\tmetin  ", "cok bosluklu metin")]
    public void Fold_simplifies_Turkish_letters(string input, string expected) => Assert.Equal(expected, MessageEngine.Fold(input));

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("isbank", "isbank", 0)]
    [InlineData("", "abc", 3)]
    public void Levenshtein_is_correct(string a, string b, int expected) => Assert.Equal(expected, MessageEngine.Levenshtein(a, b));

    [Fact]
    public void Fake_enforcement_message_is_high_risk_with_official_verification_path()
    {
        var r = Engine.Analyze("Hakkınızda icra takibi başlatılmıştır. Dosyanızı kapatmak için: uyap-dosyasorgu.com/icra", Sender.Mobile);
        Assert.Equal(RiskLevel.High, r.Level);
        Assert.Contains(r.Categories, k => k.Id == "fake-enforcement");
        Assert.Contains(r.Signals, s => s.Id == "impersonating-domain");
        Assert.Contains(r.Verification, v => v.Links.Any(l => l.Url == "https://www.turkiye.gov.tr"));
        Assert.NotEmpty(r.IfClicked);
    }

    [Fact]
    public void Official_domain_is_recognised()
    {
        var r = Engine.Analyze("PTT: Gönderiniz teslim edilmiştir. Sorgulama: gonderitakip.ptt.gov.tr", Sender.Header);
        Assert.Equal(RiskLevel.NoSigns, r.Level);
        Assert.True(r.Links.Single().Official);
    }

    [Theory]
    [InlineData("isbank.com", "lookalike-spelling")]
    [InlineData("yap1kredi.com.tr", "lookalike-spelling")]
    [InlineData("ziraatbank.com.tr.guvenli.net", "impersonating-domain")]
    [InlineData("bit.ly/abc", "url-shortener")]
    [InlineData("http://185.10.10.10/giris", "ip-address")]
    [InlineData("odeme-merkezi.xyz", "suspicious-tld")]
    [InlineData("türkiye.gov.tr", "special-characters")]
    [InlineData("https://indir.net/guvenlik.apk", "apk-file")]
    public void Link_signals_are_found(string link, string signal) =>
        Assert.Contains(signal, Engine.Analyze("Detay: " + link).Links.Single().Signals);

    [Theory]
    [InlineData("Fiyat 12.50 TL, A.Ş. ve T.C. kısaltmaları link değildir.")]
    [InlineData("Bana bilgi@firma.com adresinden yaz.")]
    public void Non_links_are_not_treated_as_links(string text) => Assert.Empty(Engine.Analyze(text).Links);

    [Fact]
    public void There_is_never_a_safe_level()
    {
        var r = Engine.Analyze("Yarın akşam yemeğe geliyor musunuz?");
        Assert.Equal(RiskLevel.NoSigns, r.Level);
        Assert.False(string.IsNullOrWhiteSpace(r.Note));
    }
}
