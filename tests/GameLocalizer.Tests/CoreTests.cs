using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.GameDiscovery;
using Xunit;
namespace GameLocalizer.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("Start Game")][InlineData("Continue")][InlineData("Settings")][InlineData("Quest completed")][InlineData("I don't think we should go there.")]
    public void HumanText(string text) => Assert.True(new TextCandidateDetector().Score(text) >= .6);
    [Theory]
    [InlineData("PlayerController")][InlineData("Assets/UI/Button")][InlineData("shader_main")][InlineData("texture_01")][InlineData("System.Collections.Generic")][InlineData("m_LocalPosition")][InlineData("123e4567-e89b-12d3-a456-426614174000")][InlineData("https://example.com")]
    public void TechnicalText(string text) => Assert.True(new TextCandidateDetector().Score(text) < .2);
    [Fact] public void RepetitionsAffectConfidence() => Assert.True(new TextCandidateDetector().Score("Start Game", 30) < new TextCandidateDetector().Score("Start Game"));
    [Fact] public void ProtectRoundtrip()
    {
        const string s = "Hello {player} {name} {0} {1} %s %d \\n \\t <br><b>text</b><color=red>[i]{{variable}}";
        var p = new PlaceholderProtector(); var result = p.Protect(s);
        Assert.Equal(14, result.Tokens.Count); Assert.Equal(s, p.Restore(result.Text, result.Tokens));
        Assert.Throws<InvalidDataException>(() => p.Restore("lost", result.Tokens));
    }
    [Theory]
    [InlineData("Hi {name}", "Привет {name}", true)]
    [InlineData("Hi {name}", "Привет {player}", false)]
    [InlineData("%s %s", "%s", false)]
    [InlineData("<b><i>Hello</i></b>", "<b><i>Привет</b></i>", false)]
    [InlineData("Hello\\nworld", "Привет мир", false)]
    public void Validator(string source, string translated, bool expected) => Assert.Equal(expected, new TranslationValidator().Validate(source, translated, out _));
    [Fact] public void JsonPreservesKeysAndTypes()
    {
        var adapter = new JsonLocalizationAdapter(); const string source = "{\"menu.play\":\"Play\",\"n\":1,\"a\":[\"Settings\"],\"enabled\":true}";
        var entries = adapter.Extract(source); var values = new Dictionary<string, string> { [entries[0].Key] = "Играть" };
        var result = adapter.ApplyTranslations(source, values);
        Assert.Contains("\"menu.play\":", result); Assert.Contains("\"n\":1", result); Assert.True(adapter.Validate(source, result, values));
        Assert.Equal("Играть", adapter.Extract(result)[0].Text);
        Assert.False(adapter.Validate(source, result.Replace("true", "false"), values));
    }
    [Fact] public void JsonUnicodeAndEscapes()
    {
        var a = new JsonLocalizationAdapter(); const string s = "{\"ключ\":\"Hello \\\"friend\\\"\",\"nested\":{\"ключ\":\"Continue\"}}";
        var e = a.Extract(s); var t = new Dictionary<string, string> { [e[1].Key] = "Продолжить" };
        Assert.True(a.Validate(s, a.ApplyTranslations(s, t), t));
    }
    [Fact] public void XmlPreservesAttributesAndEscapes()
    {
        var a = new XmlLocalizationAdapter(); const string s = "<?xml version=\"1.0\"?><root><!--note--><text id=\"play\">Play</text><n>42</n></root>";
        var t = new Dictionary<string, string> { [a.Extract(s)[0].Key] = "Играть & выйти" }; var output = a.ApplyTranslations(s, t);
        Assert.Contains("id=\"play\"", output); Assert.Contains("<!--note-->", output); Assert.True(a.Validate(s, output, t));
        Assert.Equal("Играть & выйти", a.Extract(output)[0].Text);
    }
    [Fact] public void XmlRejectsDtd() => Assert.Throws<System.Xml.XmlException>(() => new XmlLocalizationAdapter().Extract("<!DOCTYPE root [<!ENTITY secret SYSTEM 'file:///private'>]><root>&secret;</root>"));
    [Fact] public void CsvQuotedMultiline()
    {
        var a = new CsvLocalizationAdapter(); const string s = "id,text,comment\r\nplay,\"Start Game\",\"Hello, \"\"friend\"\"\r\nnext\"\r\n";
        Assert.Equal(2, a.Extract(s).Count); var t = new Dictionary<string, string> { ["1:1"] = "Начать, игру" };
        var output = a.ApplyTranslations(s, t); Assert.StartsWith("id,text,comment\r\nplay,", output); Assert.True(a.Validate(s, output, t));
    }
    [Fact] public void CsvMalformedRejected() => Assert.Throws<FormatException>(() => new CsvLocalizationAdapter().Extract("id,text\n1,\"not closed"));
    [Fact] public void Tsv() { var a = new CsvLocalizationAdapter('\t'); Assert.Equal("Start Game", a.Extract("id\ttext\nstart\tStart Game")[0].Text); }
    [Fact] public void IniPreservesCommentsSectionsKeys()
    {
        var a = new IniLocalizationAdapter(); const string s = "; note\r\n[menu]\r\nplay = Start Game ; comment\r\nsettings=\"Settings\"\r\n";
        var t = new Dictionary<string, string> { [a.Extract(s)[0].Key] = "Начать игру" }; var output = a.ApplyTranslations(s, t);
        Assert.Contains("play = Начать игру ; comment", output); Assert.StartsWith("; note\r\n[menu]", output); Assert.True(a.Validate(s, output, t));
    }
    [Fact] public void PoSingularMultiline()
    {
        GameLocalizer.Core.Interfaces.ILocalizationAdapter a = new PoLocalizationAdapter();
        const string s = "msgid \"\"\nmsgstr \"Language: en\\n\"\n\n# note\nmsgid \"Start \"\n\"Game\"\nmsgstr \"\"\n";
        var e = Assert.Single(a.Extract(s)); Assert.Equal("Start Game", e.Text);
        var t = new Dictionary<string, string> { [e.Key] = "Начать игру" }; var output = a.ApplyTranslations(s, t);
        Assert.Contains("msgid \"Start \"", output); Assert.True(a.Validate(s, output, t));
    }
    [Fact] public void PlainTextPreservesNewlines()
    {
        var a = new PlainTextLocalizationAdapter(); const string s = "Start Game\r\n\r\nSettings\n"; var t = new Dictionary<string, string> { ["0"] = "Начать игру" };
        Assert.Equal("Начать игру\r\n\r\nSettings\n", a.ApplyTranslations(s, t));
    }
    [Fact] public void VdfLibrariesAndManifest()
    {
        var paths = SteamDiscoveryService.ParseLibraries("\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Steam\" } \"1\" \"D:\\\\Games\" }");
        Assert.Equal(2, paths.Count()); var pairs = SteamDiscoveryService.ParsePairs("\"appid\" \"42\" \"name\" \"Demo\" \"installdir\" \"Demo\""); Assert.Equal("Demo", pairs["name"]);
    }
}
