using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shiny.DocumentDb;
using Shiny.Net.HttpServer.DocumentDb;
using Shiny.Net.HttpServer.JsonPatch;
using Shiny.Net.HttpServer.OpenApi;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// RFC 6902 JSON Patch over RFC 6901 JSON Pointer. The engine is checked against
// the RFC's own worked examples and the cases from the community json-patch
// test suite (github.com/json-patch/json-patch-tests) that pin down the edges:
// leading-zero indexes, "-", escaping order, number equality, atomicity. Then
// the same patch goes over the wire — a raw handler, a generated endpoint, and
// the DocumentDb resource — because a correct engine behind the wrong status
// code is still the wrong answer to a client.
// ---------------------------------------------------------------------------

public record PatchWidget(int Id, string Name, List<string> Tags, decimal Price);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PatchWidget))]
public partial class PatchJson : JsonSerializerContext;

[Route("/api/widgets")]
public class WidgetPatchEndpoints
{
    [Patch("/{id:int}")]
    public IResult Patch(int id, JsonPatchDocument patch)
    {
        var widget = new PatchWidget(id, "original", ["a"], 10m);

        try
        {
            return Results.Ok(patch.ApplyTo(widget, PatchJson.Default.PatchWidget), PatchJson.Default.PatchWidget);
        }
        catch (JsonPatchException ex)
        {
            return ex.ToResult();
        }
    }
}

public class JsonPointerTests
{
    // RFC 6901 §5, every example, against the RFC's own document.
    const string Rfc6901Document = """
        {
          "foo": ["bar", "baz"],
          "": 0,
          "a/b": 1,
          "c%d": 2,
          "e^f": 3,
          "g|h": 4,
          "i\\j": 5,
          "k\"l": 6,
          " ": 7,
          "m~n": 8
        }
        """;

    [Theory]
    [InlineData("/foo", """["bar","baz"]""")]
    [InlineData("/foo/0", "\"bar\"")]
    [InlineData("/", "0")]
    [InlineData("/a~1b", "1")]
    [InlineData("/c%d", "2")]
    [InlineData("/e^f", "3")]
    [InlineData("/g|h", "4")]
    [InlineData("/i\\j", "5")]
    [InlineData("/k\"l", "6")]
    [InlineData("/ ", "7")]
    [InlineData("/m~0n", "8")]
    public void Resolves_the_rfc_6901_examples(string pointer, string expected)
    {
        var document = JsonNode.Parse(Rfc6901Document);

        Assert.True(JsonPointer.Parse(pointer).TryEvaluate(document, out var value));
        Assert.Equal(expected, value!.ToJsonString());
    }

    [Fact]
    public void The_empty_pointer_is_the_whole_document_and_slash_is_the_empty_member()
    {
        var document = JsonNode.Parse(Rfc6901Document);

        Assert.True(JsonPointer.Root.TryEvaluate(document, out var whole));
        Assert.Same(document, whole);

        Assert.Equal([""], JsonPointer.Parse("/").Segments);
        Assert.Empty(JsonPointer.Parse("").Segments);
    }

    [Fact]
    public void Decodes_tilde_one_before_tilde_zero()
    {
        // "~01" is "~1", not "/": decoding ~0 first would produce "~1" and then "/".
        Assert.Equal(["~1"], JsonPointer.Parse("/~01").Segments);
        Assert.Equal(["a/b", "m~n"], JsonPointer.Parse("/a~1b/m~0n").Segments);
    }

    [Fact]
    public void Round_trips_segments_through_escaping()
    {
        var pointer = JsonPointer.Create("a/b", "~", "0");

        Assert.Equal("/a~1b/~0/0", pointer.ToString());
        Assert.Equal(pointer, JsonPointer.Parse(pointer.ToString()));
        Assert.Equal("/a~1b/~0/0/-", pointer.Append("-").ToString());
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("/~")]
    [InlineData("/~2")]
    [InlineData("/a~")]
    public void Refuses_invalid_pointers(string pointer)
    {
        Assert.False(JsonPointer.TryParse(pointer, out _));
        Assert.Throws<FormatException>(() => JsonPointer.Parse(pointer));
    }

    [Theory]
    [InlineData("/foo/01")]
    [InlineData("/foo/-")]
    [InlineData("/foo/2")]
    [InlineData("/foo/-1")]
    [InlineData("/foo/0/x")]
    [InlineData("/missing")]
    public void Reports_a_missing_or_invalid_target_as_absent(string pointer)
    {
        Assert.False(JsonPointer.Parse(pointer).TryEvaluate(JsonNode.Parse(Rfc6901Document), out _));
    }

    [Fact]
    public void Tells_a_present_null_from_an_absent_member()
    {
        var document = JsonNode.Parse("""{"a":null}""");

        Assert.True(JsonPointer.Parse("/a").TryEvaluate(document, out var value));
        Assert.Null(value);
        Assert.False(JsonPointer.Parse("/b").TryEvaluate(document, out _));
    }
}

public class JsonPatchEngineTests
{
    static void AssertJsonEqual(string expected, JsonNode? actual)
        => Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"Expected {expected} but got {actual?.ToJsonString() ?? "null"}"
        );

    static JsonNode? Apply(string document, string patch)
        => JsonPatchDocument.Parse(patch).ApplyTo(JsonNode.Parse(document));

    /// <summary>RFC 6902 Appendix A — every example that is expected to succeed.</summary>
    public static TheoryData<string, string, string, string> AppendixSuccesses => new()
    {
        { "A.1 add an object member", """{"foo":"bar"}""", """[{"op":"add","path":"/baz","value":"qux"}]""", """{"baz":"qux","foo":"bar"}""" },
        { "A.2 add an array element", """{"foo":["bar","baz"]}""", """[{"op":"add","path":"/foo/1","value":"qux"}]""", """{"foo":["bar","qux","baz"]}""" },
        { "A.3 remove an object member", """{"baz":"qux","foo":"bar"}""", """[{"op":"remove","path":"/baz"}]""", """{"foo":"bar"}""" },
        { "A.4 remove an array element", """{"foo":["bar","qux","baz"]}""", """[{"op":"remove","path":"/foo/1"}]""", """{"foo":["bar","baz"]}""" },
        { "A.5 replace a value", """{"baz":"qux","foo":"bar"}""", """[{"op":"replace","path":"/baz","value":"boo"}]""", """{"baz":"boo","foo":"bar"}""" },
        {
            "A.6 move a value",
            """{"foo":{"bar":"baz","waldo":"fred"},"qux":{"corge":"grault"}}""",
            """[{"op":"move","from":"/foo/waldo","path":"/qux/thud"}]""",
            """{"foo":{"bar":"baz"},"qux":{"corge":"grault","thud":"fred"}}"""
        },
        { "A.7 move an array element", """{"foo":["all","grass","cows","eat"]}""", """[{"op":"move","from":"/foo/1","path":"/foo/3"}]""", """{"foo":["all","cows","eat","grass"]}""" },
        {
            "A.8 test a value (success)",
            """{"baz":"qux","foo":["a",2,"c"]}""",
            """[{"op":"test","path":"/baz","value":"qux"},{"op":"test","path":"/foo/1","value":2}]""",
            """{"baz":"qux","foo":["a",2,"c"]}"""
        },
        { "A.10 add a nested member object", """{"foo":"bar"}""", """[{"op":"add","path":"/child","value":{"grandchild":{}}}]""", """{"foo":"bar","child":{"grandchild":{}}}""" },
        { "A.11 ignore unrecognized elements", """{"foo":"bar"}""", """[{"op":"add","path":"/baz","value":"qux","xyz":123}]""", """{"foo":"bar","baz":"qux"}""" },
        { "A.14 ~ escape ordering", """{"/":9,"~1":10}""", """[{"op":"test","path":"/~01","value":10}]""", """{"/":9,"~1":10}""" },
        { "A.16 add an array value", """{"foo":["bar"]}""", """[{"op":"add","path":"/foo/-","value":["abc","def"]}]""", """{"foo":["bar",["abc","def"]]}""" }
    };

    [Theory]
    [MemberData(nameof(AppendixSuccesses))]
    public void Applies_the_rfc_6902_appendix_examples(string example, string document, string patch, string expected)
    {
        _ = example;
        AssertJsonEqual(expected, Apply(document, patch));
    }

    /// <summary>RFC 6902 Appendix A — the examples that must fail, and how.</summary>
    public static TheoryData<string, string, string, JsonPatchErrorKind> AppendixFailures => new()
    {
        { "A.9 test a value (error)", """{"baz":"qux"}""", """[{"op":"test","path":"/baz","value":"bar"}]""", JsonPatchErrorKind.TestFailed },
        { "A.12 add to a nonexistent target", """{"foo":"bar"}""", """[{"op":"add","path":"/baz/bat","value":"qux"}]""", JsonPatchErrorKind.PathNotFound },
        { "A.13 invalid patch document (duplicate op)", """{"foo":"bar"}""", """[{"op":"add","path":"/baz","value":"qux","op":"remove"}]""", JsonPatchErrorKind.Malformed },
        { "A.15 comparing strings and numbers", """{"/":9,"~1":10}""", """[{"op":"test","path":"/~01","value":"10"}]""", JsonPatchErrorKind.TestFailed }
    };

    [Theory]
    [MemberData(nameof(AppendixFailures))]
    public void Fails_the_rfc_6902_appendix_error_examples(string example, string document, string patch, JsonPatchErrorKind kind)
    {
        _ = example;
        var error = Assert.Throws<JsonPatchException>(() => Apply(document, patch));
        Assert.Equal(kind, error.Kind);
    }

    /// <summary>Cases from the json-patch-tests suite (tests.json / spec_tests.json) that should succeed.</summary>
    public static TheoryData<string, string, string> SuiteSuccesses => new()
    {
        { "{}", "[]", "{}" },
        { """{"foo":1}""", """[{"op":"add","path":"","value":{}}]""", "{}" },
        { "[]", """[{"op":"add","path":"/-","value":"hi"}]""", """["hi"]""" },
        { "[]", """[{"op":"add","path":"/0","value":"foo"}]""", """["foo"]""" },
        { """{"foo":1}""", """[{"op":"add","path":"/bar","value":null}]""", """{"foo":1,"bar":null}""" },
        { """{"foo":"bar"}""", """[{"op":"replace","path":"/foo","value":null}]""", """{"foo":null}""" },
        { """{"foo":null}""", """[{"op":"test","path":"/foo","value":null}]""", """{"foo":null}""" },
        { """{"foo":"bar"}""", """[{"op":"add","path":"/FOO","value":"BAR"}]""", """{"foo":"bar","FOO":"BAR"}""" },
        { """{"foo":1,"bar":[1,2,3,4]}""", """[{"op":"remove","path":"/bar"}]""", """{"foo":1}""" },
        { """{"foo":"bar"}""", """[{"op":"replace","path":"","value":{"baz":"qux"}}]""", """{"baz":"qux"}""" },
        { """{"foo":{"foo":1,"bar":2}}""", """[{"op":"test","path":"/foo","value":{"bar":2,"foo":1}}]""", """{"foo":{"foo":1,"bar":2}}""" },
        { """{"foo":[1,2]}""", """[{"op":"test","path":"/foo","value":[1,2]}]""", """{"foo":[1,2]}""" },
        { """{"1e0":"foo"}""", """[{"op":"test","path":"/1e0","value":"foo"}]""", """{"1e0":"foo"}""" },
        { """{"":1}""", """[{"op":"test","path":"/","value":1}]""", """{"":1}""" },
        { """{"foo":1}""", """[{"op":"move","from":"/foo","path":"/foo"}]""", """{"foo":1}""" },
        { """{"foo":1,"baz":[{"qux":"hello"}]}""", """[{"op":"move","from":"/foo","path":"/bar"}]""", """{"baz":[{"qux":"hello"}],"bar":1}""" },
        { """{"baz":[{"qux":"hello"}],"bar":1}""", """[{"op":"move","from":"/baz/0/qux","path":"/baz/1"}]""", """{"baz":[{},"hello"],"bar":1}""" },
        { """{"baz":[{"qux":"hello"}],"bar":1}""", """[{"op":"copy","from":"/baz/0","path":"/boo"}]""", """{"baz":[{"qux":"hello"}],"bar":1,"boo":{"qux":"hello"}}""" },
        { """{"foo":["bar"]}""", """[{"op":"copy","from":"/foo","path":"/foo/-"}]""", """{"foo":["bar",["bar"]]}""" },
        { """{"foo":[1,2]}""", """[{"op":"add","path":"/foo/2","value":3}]""", """{"foo":[1,2,3]}""" },
        { """{"foo":1}""", """[{"op":"test","path":"/foo","value":1.0}]""", """{"foo":1}""" },
        { """{"foo":100}""", """[{"op":"test","path":"/foo","value":1e2}]""", """{"foo":100}""" },
        { """{"foo":"A"}""", """[{"op":"test","path":"/foo","value":"A"}]""", """{"foo":"A"}""" },
        { """[1,2,[3,[4,5]]]""", """[{"op":"replace","path":"/2/1/0","value":"x"}]""", """[1,2,[3,["x",5]]]""" },
        { """{"foo":{"bar":1}}""", """[{"op":"move","from":"/foo/bar","path":""}]""", "1" }
    };

    [Theory]
    [MemberData(nameof(SuiteSuccesses))]
    public void Passes_the_json_patch_suite_success_cases(string document, string patch, string expected)
        => AssertJsonEqual(expected, Apply(document, patch));

    /// <summary>Cases from the json-patch-tests suite that must be refused, and the kind of refusal.</summary>
    public static TheoryData<string, string, JsonPatchErrorKind> SuiteFailures => new()
    {
        { """{"foo":["bar"]}""", """[{"op":"add","path":"/foo/01","value":"x"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"bar":[1,2]}""", """[{"op":"add","path":"/bar/8","value":"5"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"bar":[1,2]}""", """[{"op":"add","path":"/bar/-1","value":"5"}]""", JsonPatchErrorKind.PathNotFound },
        { """["foo","sil"]""", """[{"op":"add","path":"/bar","value":42}]""", JsonPatchErrorKind.PathNotFound },
        { """["foo","bar"]""", """[{"op":"test","path":"/00","value":"foo"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1,"baz":[{"qux":"hello"}]}""", """[{"op":"remove","path":"/baz/1e0"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"remove","path":"/baz"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"remove","path":"/missing1/missing2"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"add","path":"/foo/bar","value":"x"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":"bar"}""", """[{"op":"replace","path":"/baz","value":"x"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":[1]}""", """[{"op":"remove","path":"/foo/-"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1}""", """[{"op":"copy","from":"/bar","path":"/baz"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1}""", """[{"op":"move","from":"/bar","path":"/baz"}]""", JsonPatchErrorKind.PathNotFound },
        { """{"foo":1}""", """[{"op":"test","path":"/foo","value":"1"}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":{"bar":[1,2,5,4]}}""", """[{"op":"test","path":"/foo","value":{"bar":[1,2]}}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":[1,2]}""", """[{"op":"test","path":"/foo","value":[2,1]}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":null}""", """[{"op":"test","path":"/foo","value":false}]""", JsonPatchErrorKind.TestFailed },
        { """{"foo":1}""", """[{"op":"add","path":"/bar"}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"copy","path":"/bar"}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"spam","path":"/foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"Add","path":"/foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"path":"/foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"add","path":"foo","value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"add","path":1,"value":1}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":{"bar":1}}""", """[{"op":"move","from":"/foo","path":"/foo/bar/baz"}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """{"op":"add","path":"/foo","value":1}""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[1]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"remove","path":""}]""", JsonPatchErrorKind.Malformed },
        { """{"foo":1}""", """[{"op":"add","path":"/a","value":1""", JsonPatchErrorKind.Malformed }
    };

    [Theory]
    [MemberData(nameof(SuiteFailures))]
    public void Refuses_the_json_patch_suite_error_cases(string document, string patch, JsonPatchErrorKind kind)
    {
        var error = Assert.Throws<JsonPatchException>(() => Apply(document, patch));
        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public void Is_atomic_a_late_failure_leaves_the_document_untouched()
    {
        var document = JsonNode.Parse("""{"a":1,"list":[1,2]}""")!;
        var patch = JsonPatchDocument.Parse("""
            [
              {"op":"add","path":"/b","value":2},
              {"op":"remove","path":"/list/0"},
              {"op":"test","path":"/a","value":99}
            ]
            """);

        var error = Assert.Throws<JsonPatchException>(() => patch.ApplyTo(document));

        Assert.Equal(2, error.OperationIndex);
        Assert.Equal(JsonPatchOperationType.Test, error.Operation!.Op);
        AssertJsonEqual("""{"a":1,"list":[1,2]}""", document);
    }

    [Fact]
    public void Never_modifies_the_input_even_on_success()
    {
        var document = JsonNode.Parse("""{"a":1}""")!;
        var patched = JsonPatchDocument.Parse("""[{"op":"replace","path":"/a","value":2}]""").ApplyTo(document);

        AssertJsonEqual("""{"a":1}""", document);
        AssertJsonEqual("""{"a":2}""", patched);
    }

    [Fact]
    public void Can_be_applied_twice_without_the_operations_sharing_nodes()
    {
        var patch = JsonPatchDocument.Parse("""[{"op":"add","path":"/child","value":{"x":1}}]""");

        var first = patch.ApplyTo(JsonNode.Parse("{}"))!;
        var second = patch.ApplyTo(JsonNode.Parse("{}"))!;

        first["child"]!["x"] = 99;

        AssertJsonEqual("""{"child":{"x":1}}""", second);
    }

    [Fact]
    public void Names_the_failing_operation_when_parsing()
    {
        var error = Assert.Throws<JsonPatchException>(() => JsonPatchDocument.Parse("""
            [
              {"op":"add","path":"/a","value":1},
              {"op":"frobnicate","path":"/a"}
            ]
            """));

        Assert.Equal(JsonPatchErrorKind.Malformed, error.Kind);
        Assert.Equal(1, error.OperationIndex);
        Assert.Contains("frobnicate", error.Message);
    }

    [Fact]
    public void Builds_and_round_trips_a_patch_in_code()
    {
        var patch = new JsonPatchDocument()
            .Test("/version", 3)
            .Replace("/name", "new")
            .Add("/tags/-", "x")
            .Remove("/notes")
            .Move("/a", "/b")
            .Copy("/b", "/c")
            .Add("/nothing", null);

        var json = patch.ToJsonString();
        var reparsed = JsonPatchDocument.Parse(json);

        Assert.Equal(7, reparsed.Operations.Count);
        Assert.Equal(json, reparsed.ToJsonString());
        Assert.Contains("""{"op":"add","path":"/nothing","value":null}""", json);
        Assert.Contains("""{"op":"move","from":"/a","path":"/b"}""", json);
    }

    [Fact]
    public void Refuses_a_move_into_its_own_child_while_building()
    {
        var error = Assert.Throws<JsonPatchException>(() => new JsonPatchDocument().Add("/x", 1).Move("/a", "/a/b"));

        Assert.Equal(JsonPatchErrorKind.Malformed, error.Kind);
        Assert.Equal(1, error.OperationIndex);
    }

    [Fact]
    public void Applies_to_a_typed_value_through_its_json_metadata()
    {
        var widget = new PatchWidget(1, "old", ["a"], 10m);
        var patch = JsonPatchDocument.Parse("""
            [
              {"op":"test","path":"/price","value":10.00},
              {"op":"replace","path":"/name","value":"new"},
              {"op":"add","path":"/tags/-","value":"b"}
            ]
            """);

        var patched = patch.ApplyTo(widget, PatchJson.Default.PatchWidget);

        Assert.Equal("new", patched.Name);
        Assert.Equal(["a", "b"], patched.Tags);
        Assert.Equal("old", widget.Name);
    }

    [Fact]
    public void Reports_a_result_that_no_longer_fits_the_type_as_unprocessable()
    {
        var patch = JsonPatchDocument.Parse("""[{"op":"replace","path":"/id","value":"not-a-number"}]""");

        var error = Assert.Throws<JsonPatchException>(() =>
            patch.ApplyTo(new PatchWidget(1, "n", [], 1m), PatchJson.Default.PatchWidget));

        Assert.Equal(JsonPatchErrorKind.InvalidResult, error.Kind);
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public void Round_trips_through_an_apps_own_serializer_context()
    {
        var patch = new JsonPatchDocument().Replace("/a", 1);

        var json = JsonSerializer.Serialize(patch, JsonPatchContext.Default.JsonPatchDocument);
        var back = JsonSerializer.Deserialize(json, JsonPatchContext.Default.JsonPatchDocument)!;

        Assert.Equal(patch.ToJsonString(), back.ToJsonString());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""[{"op":"nope"}]""", JsonPatchContext.Default.JsonPatchDocument));
    }

    [Theory]
    [InlineData(JsonPatchErrorKind.Malformed, 400)]
    [InlineData(JsonPatchErrorKind.PathNotFound, 409)]
    [InlineData(JsonPatchErrorKind.TestFailed, 409)]
    [InlineData(JsonPatchErrorKind.InvalidResult, 422)]
    [InlineData(JsonPatchErrorKind.UnsupportedMediaType, 415)]
    public void Maps_each_failure_to_its_rfc_5789_status(JsonPatchErrorKind kind, int status)
    {
        var error = new JsonPatchException(kind, "x");

        Assert.Equal(status, error.StatusCode);

        // And the problem-details middleware agrees when the exception escapes a handler.
        Assert.Equal(status, new ProblemDetailsOptions().GetStatusCode(error));
    }
}

[JsonSerializable(typeof(JsonPatchDocument))]
public partial class JsonPatchContext : JsonSerializerContext;

/// <summary>The same patch over the wire: raw handlers on every transport, then generated endpoints.</summary>
public class JsonPatchHttpTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static StringContent Patch(string json) => new(json, Encoding.UTF8, JsonPatchDocument.MediaType);

    static void MapRaw(HttpServer app) => app.MapPatch("/raw", async ctx =>
    {
        try
        {
            var patch = await ctx.Request.ReadJsonPatchAsync(ctx.RequestAborted);
            var result = patch.ApplyTo(JsonNode.Parse("""{"n":1,"list":[]}"""));

            return Results.Content(result!.ToJsonString(), "application/json");
        }
        catch (JsonPatchException ex)
        {
            return ex.ToResult();
        }
    });

    static async Task AssertRawHandlerWorks(HttpClient client, Version? expectedVersion = null)
    {
        var ok = await client.PatchAsync("/raw", Patch("""[{"op":"replace","path":"/n","value":2},{"op":"add","path":"/list/-","value":"x"}]"""), Token);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        if (expectedVersion is not null)
            Assert.Equal(expectedVersion, ok.Version);

        Assert.Equal("""{"n":2,"list":["x"]}""", await ok.Content.ReadAsStringAsync(Token));

        var conflict = await client.PatchAsync("/raw", Patch("""[{"op":"add","path":"/x","value":1},{"op":"test","path":"/n","value":5}]"""), Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var problem = JsonNode.Parse(await conflict.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(1, problem["operationIndex"]!.GetValue<int>());
        Assert.Equal("test", problem["op"]!.GetValue<string>());
        Assert.Equal("testFailed", problem["patchError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Raw_handler_reads_and_applies_a_patch_over_http1()
    {
        await using var server = await TestServer.StartAsync(MapRaw);
        await AssertRawHandlerWorks(server.Client, HttpVersion.Version11);
    }

    [Fact]
    public async Task Raw_handler_reads_and_applies_a_patch_over_http2()
    {
        await using var server = await TestServer.StartAsync(MapRaw);

        using var client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        await AssertRawHandlerWorks(client, HttpVersion.Version20);
    }

    [Fact]
    public async Task Raw_handler_reads_and_applies_a_patch_in_memory()
    {
        await using var app = TestHttpServer.Create(MapRaw);
        await AssertRawHandlerWorks(app.Client);
    }

    [Fact]
    public async Task Raw_handler_answers_415_with_accept_patch_for_plain_json()
    {
        await using var server = await TestServer.StartAsync(MapRaw);

        var response = await server.Client.PatchAsync(
            "/raw",
            new StringContent("""[{"op":"remove","path":"/n"}]""", Encoding.UTF8, "application/json"),
            Token
        );

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(JsonPatchDocument.MediaType, response.Headers.GetValues("Accept-Patch").Single());
    }

    [Fact]
    public async Task Unhandled_patch_failure_becomes_a_problem_with_its_status()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseProblemDetails();
                app.MapPatch("/unhandled", async ctx =>
                {
                    var patch = await ctx.Request.ReadJsonPatchAsync(ctx.RequestAborted);
                    patch.ApplyTo(JsonNode.Parse("{}"));
                    return Results.NoContent();
                });
            },
            builder => builder.AddProblemDetails()
        );

        var response = await server.Client.PatchAsync("/unhandled", Patch("""[{"op":"remove","path":"/gone"}]"""), Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── Generated endpoints ─────────────────────────────────────────────

    static Task<TestServer> StartGenerated() => TestServer.StartAsync(app => app.MapWidgetPatchEndpoints());

    [Fact]
    public async Task Generated_endpoint_binds_a_json_patch_parameter()
    {
        await using var server = await StartGenerated();

        var response = await server.Client.PatchAsync(
            "/api/widgets/4",
            Patch("""[{"op":"replace","path":"/name","value":"renamed"},{"op":"add","path":"/tags/0","value":"first"}]"""),
            Token
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(4, body["id"]!.GetValue<int>());
        Assert.Equal("renamed", body["name"]!.GetValue<string>());
        Assert.Equal("""["first","a"]""", body["tags"]!.ToJsonString());
    }

    [Fact]
    public async Task Generated_endpoint_answers_415_with_accept_patch_for_another_content_type()
    {
        await using var server = await StartGenerated();

        var response = await server.Client.PatchAsync(
            "/api/widgets/4",
            new StringContent("""{"name":"x"}""", Encoding.UTF8, "application/merge-patch+json"),
            Token
        );

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(JsonPatchDocument.MediaType, response.Headers.GetValues("Accept-Patch").Single());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Generated_endpoint_answers_400_naming_the_malformed_operation()
    {
        await using var server = await StartGenerated();

        var response = await server.Client.PatchAsync(
            "/api/widgets/4",
            Patch("""[{"op":"replace","path":"/name","value":"x"},{"op":"add","path":"/tags"}]"""),
            Token
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(1, problem["operationIndex"]!.GetValue<int>());
        Assert.Equal("malformed", problem["patchError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Generated_endpoint_answers_409_for_a_path_that_does_not_exist()
    {
        await using var server = await StartGenerated();

        var response = await server.Client.PatchAsync(
            "/api/widgets/4",
            Patch("""[{"op":"remove","path":"/tags/5"}]"""),
            Token
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal("pathNotFound", problem["patchError"]!.GetValue<string>());
        Assert.Equal("/tags/5", problem["path"]!.GetValue<string>());
    }

    [Fact]
    public void OpenApi_describes_the_body_as_a_json_patch()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.MapWidgetPatchEndpoints();

        var document = JsonDocument.Parse(OpenApiDocumentBuilder.Build(server, new OpenApiOptions())).RootElement;

        var content = document
            .GetProperty("paths").GetProperty("/api/widgets/{id}").GetProperty("patch")
            .GetProperty("requestBody").GetProperty("content");

        var schema = content.GetProperty(JsonPatchDocument.MediaType).GetProperty("schema");
        Assert.False(content.TryGetProperty("application/json", out _));
        Assert.Equal("array", schema.GetProperty("type").GetString());

        var ops = schema.GetProperty("items").GetProperty("properties").GetProperty("op").GetProperty("enum");
        Assert.Equal(["add", "remove", "replace", "move", "copy", "test"], ops.EnumerateArray().Select(e => e.GetString()));
    }
}

/// <summary>
/// The DocumentDb resource takes JSON Patch alongside merge patch. What must not change with the format is
/// what the endpoint enforces: the If-Match check, the scope on both sides of the write, and the 404 for a
/// document outside it.
/// </summary>
public class DocumentJsonPatchTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static StringContent Patch(string json) => new(json, Encoding.UTF8, JsonPatchDocument.MediaType);

    static async Task<JsonObject> Read(DocumentServer server, string path)
        => JsonNode.Parse(await server.Client.GetStringAsync(path, Token))!.AsObject();

    [Fact]
    public async Task Applies_a_json_patch_to_a_typed_document()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Status = "open", Total = 10m, Notes = "n" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""
            [
              {"op":"test","path":"/total","value":10.0},
              {"op":"replace","path":"/status","value":"closed"},
              {"op":"remove","path":"/notes"}
            ]
            """), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);

        var after = await Read(server, "/orders/a");
        Assert.Equal("closed", after["status"]!.GetValue<string>());
        Assert.True(after["notes"] is null || after["notes"]!.GetValueKind() == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_failed_test_is_a_409_and_writes_nothing()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Status = "open" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""
            [
              {"op":"replace","path":"/status","value":"closed"},
              {"op":"test","path":"/status","value":"open"}
            ]
            """), Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(1, problem["operationIndex"]!.GetValue<int>());

        Assert.Equal("open", (await Read(server, "/orders/a"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_malformed_patch_is_a_400()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""{"status":"closed"}"""), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Replacing_the_document_with_a_non_object_is_a_422()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""[{"op":"replace","path":"","value":[1]}]"""), Token);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Honours_if_match_exactly_as_merge_patch_does()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        using var stale = new HttpRequestMessage(HttpMethod.Patch, "/orders/a")
        {
            Content = Patch("""[{"op":"replace","path":"/status","value":"closed"}]""")
        };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"999\""));

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await server.Client.SendAsync(stale, Token)).StatusCode);

        var current = await server.Client.GetAsync("/orders/a", Token);
        using var fresh = new HttpRequestMessage(HttpMethod.Patch, "/orders/a")
        {
            Content = Patch("""[{"op":"replace","path":"/status","value":"closed"}]""")
        };
        fresh.Headers.IfMatch.Add(current.Headers.ETag!);

        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.SendAsync(fresh, Token)).StatusCode);
    }

    [Fact]
    public async Task Requires_if_match_when_the_resource_does()
    {
        await using var server = await DocumentServer.StartAsync(o => o.RequireIfMatch = true);
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.PatchAsync("/orders/a", Patch("""[{"op":"replace","path":"/status","value":"x"}]"""), Token);

        Assert.Equal(428, (int)response.StatusCode);
    }

    [Fact]
    public async Task Advertises_both_patch_formats_on_a_read()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.GetAsync("/orders/a", Token);
        var accept = response.Headers.GetValues("Accept-Patch").Single();

        Assert.Contains("application/merge-patch+json", accept);
        Assert.Contains(JsonPatchDocument.MediaType, accept);
    }

    [Fact]
    public async Task Still_reads_merge_patch_for_every_other_content_type()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Notes = "n" });

        var response = await server.Client.PatchAsync(
            "/orders/a",
            new StringContent("""{"status":"merged"}""", Encoding.UTF8, "application/merge-patch+json"),
            Token
        );

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("merged", (await Read(server, "/orders/a"))["status"]!.GetValue<string>());
    }

    // ── Scope ───────────────────────────────────────────────────────────

    static Task<DocumentServer> StartScoped() => DocumentServer.StartAsync(
        o => o.Scope<ICustomerContext>((customer, ctx) =>
        {
            var id = customer.Resolve(ctx.Http);
            return x => x.CustomerId == id;
        })
    );

    static HttpRequestMessage As(string customer, string path, string patch)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = Patch(patch) };
        request.Headers.Add("X-Customer", customer);
        return request;
    }

    [Fact]
    public async Task Reports_an_out_of_scope_document_as_missing()
    {
        await using var server = await StartScoped();
        await server.SeedAsync(new Order { Id = "b", CustomerId = "c2" });

        var response = await server.Client.SendAsync(As("c1", "/orders/b", """[{"op":"replace","path":"/status","value":"x"}]"""), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_patch_that_moves_the_document_out_of_scope()
    {
        await using var server = await StartScoped();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var response = await server.Client.SendAsync(
            As("c1", "/orders/a", """[{"op":"replace","path":"/customerId","value":"c2"}]"""),
            Token
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Schema-free collection ──────────────────────────────────────────

    [Fact]
    public async Task Applies_a_json_patch_to_a_schema_free_collection()
    {
        await using var server = await DocumentServer.StartAsync(
            mapExtra: app => app.MapDocumentCollection("/notes", "notes", o => o.Operations = DocumentEndpoints.All)
        );

        var created = await server.Client.PostAsync(
            "/notes",
            new StringContent("""{"id":"n1","title":"hello","tags":["a"]}""", Encoding.UTF8, "application/json"),
            Token
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await server.Client.PatchAsync("/notes/n1", Patch("""
            [
              {"op":"add","path":"/tags/-","value":"b"},
              {"op":"move","from":"/title","path":"/heading"}
            ]
            """), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var read = await server.Client.GetAsync("/notes/n1", Token);
        Assert.Contains(JsonPatchDocument.MediaType, read.Headers.GetValues("Accept-Patch").Single());

        var after = JsonNode.Parse(await read.Content.ReadAsStringAsync(Token))!;
        Assert.Equal("""["a","b"]""", after["tags"]!.ToJsonString());
        Assert.Equal("hello", after["heading"]!.GetValue<string>());
        Assert.Null(after["title"]);

        var missing = await server.Client.PatchAsync("/notes/nope", Patch("""[{"op":"remove","path":"/x"}]"""), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
