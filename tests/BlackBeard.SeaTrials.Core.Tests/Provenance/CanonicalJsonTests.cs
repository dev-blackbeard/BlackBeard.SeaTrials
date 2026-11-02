using System.Text.Json;
using BlackBeard.SeaTrials.Core.Provenance;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Provenance;

/// <summary>
/// The canonical form must give the same bytes as tools/stamp.py. Both read the same vector file, whose expected
/// strings and hashes were produced by the Python reference.
/// </summary>
public class CanonicalJsonTests
{
    private static JsonElement Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "canonical-vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    [Fact]
    public void EveryCanonicalCaseOfTheSharedVectorGivesTheSameStringAndHash()
    {
        var cases = Vectors().GetProperty("canonical").EnumerateArray().ToList();
        cases.Count.ShouldBeGreaterThan(5);

        foreach (var testCase in cases)
        {
            var name = testCase.GetProperty("name").GetString()!;
            var expected = testCase.GetProperty("expected").GetString()!;

            CanonicalJson.Serialize(testCase.GetProperty("value")).ShouldBe(expected, name);
            Stamper.Sha256Hex(expected).ShouldBe(testCase.GetProperty("sha256").GetString()!, name);
        }
    }

    [Fact]
    public void EveryStampCaseOfTheSharedVectorGivesTheSameBaselineAndHash()
    {
        var cases = Vectors().GetProperty("stamp").EnumerateArray().ToList();
        cases.Count.ShouldBeGreaterThan(3);

        foreach (var testCase in cases)
        {
            var name = testCase.GetProperty("name").GetString()!;
            var request = testCase.GetProperty("request");
            var baseline = testCase.GetProperty("baseline").GetString()!;
            var file = TestRequests.Load(request.GetRawText());

            // From the model, as the program stamps, and from the raw JSON, as tools/stamp.py does.
            Stamper.Baseline(file).ShouldBe(baseline, name);
            CanonicalJson.Serialize(request).ShouldBe(baseline, name);
            Stamper.Sha256Hex(baseline).ShouldBe(testCase.GetProperty("baselineSha256").GetString()!, name);
        }
    }

    [Fact]
    public void EverythingTheVectorRefusesIsRefused()
    {
        var cases = Vectors().GetProperty("refused").EnumerateArray().ToList();
        cases.Count.ShouldBeGreaterThan(4);

        foreach (var testCase in cases)
        {
            var name = testCase.GetProperty("name").GetString()!;
            var json = testCase.GetProperty("json").GetString()!;

            Should.Throw<CanonicalJsonException>(() => CanonicalJson.SerializeText(json), name);
        }
    }

    [Fact]
    public void KeysAreSortedAndArraysKeepTheirOrder()
    {
        CanonicalJson.SerializeText("""{ "b": 1, "a": { "d": [3, 2, 1], "c": true }, "B": null }""")
            .ShouldBe("""{"B":null,"a":{"c":true,"d":[3,2,1]},"b":1}""");
    }

    [Fact]
    public void KeysSortByCodePointAndNotByUtf16Unit()
    {
        // U+FF5A is one UTF-16 unit above the surrogates; U+1F600 is two units that start below it.
        const string json = "{ \"\\uff5a\": 1, \"\\ud83d\\ude00\": 2, \"z\": 3 }";

        CanonicalJson.SerializeText(json).ShouldBe("{\"z\":3,\"\uff5a\":1,\"\U0001F600\":2}");
    }

    [Fact]
    public void OnlyTheQuoteTheBackslashAndTheControlCharactersAreEscaped()
    {
        const string json = "\"q\\\" b\\\\ s/ t\\t n\\n r\\r bs\\b ff\\f nul\\u0000 us\\u001f <&> \\u00e9 \\u2028\"";

        CanonicalJson.SerializeText(json)
            .ShouldBe("\"q\\\" b\\\\ s/ t\\t n\\n r\\r bs\\b ff\\f nul\\u0000 us\\u001f <&> \u00e9 \u2028\"");
    }

    [Fact]
    public void NoWhitespaceAndMinusZeroIsZero()
    {
        CanonicalJson.SerializeText("[ -0 ,\n 0, 10, -5 ]").ShouldBe("[0,0,10,-5]");
        CanonicalJson.SerializeText("{ }").ShouldBe("{}");
        CanonicalJson.SerializeText("[ [ ], { } ]").ShouldBe("[[],{}]");
    }

    [Fact]
    public void TheHashIsTheLowerCaseSha256OfTheUtf8Bytes()
    {
        Stamper.Sha256Hex("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Stamper.Sha256Hex("").ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void AnUnpairedSurrogateCannotBeHashed()
    {
        Should.Throw<CanonicalJsonException>(() => Stamper.Sha256Hex("\ud800"));
    }
}
