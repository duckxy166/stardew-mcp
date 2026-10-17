using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

/// <summary>
/// The MCP endpoint's JSON-RPC behaviour, run against a fake seat instead of the game: what tools/list
/// advertises, how tools/call reports errors, batches, notifications and unknown methods.
/// </summary>
public class McpDispatcherTests
{
    /// <summary>A seat that records what the dispatcher does to it and answers tool calls from a script.</summary>
    private sealed class FakeSeat : IMcpSeat
    {
        public string Id { get; set; } = "p2";
        public bool Active { get; set; } = true;
        public int Contacts { get; private set; }
        public List<(string Tool, string Args)> Calls { get; } = new();
        public (bool ok, string text) Answer { get; set; } = (true, "done");

        public void MarkContact() => Contacts++;

        public Task<(bool ok, string text)> CallTool(ToolDef tool, JsonElement args)
        {
            Calls.Add((tool.Name, args.GetRawText()));
            return Task.FromResult(Answer);
        }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static McpDispatcher NewDispatcher(bool splitScreen = true) => new(SampleTools.All(), splitScreen);

    /// <summary>Request JSON written with single quotes so the tests stay readable.</summary>
    private static JsonElement Request(string text) => JsonDocument.Parse(text.Replace('\'', '"')).RootElement.Clone();

    /// <summary>What the HTTP layer would send: the reply body serialized and read back as JSON.</summary>
    private static JsonElement Wire(object? body) => JsonSerializer.SerializeToElement(body, body!.GetType(), Options);

    private static async Task<JsonElement> Call(McpDispatcher mcp, FakeSeat seat, string message)
    {
        var reply = await mcp.Dispatch(Request(message), seat);
        Assert.NotNull(reply);
        return Wire(reply);
    }

    private static string ResultText(JsonElement response) =>
        response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    // ---------------------------------------------------------------- initialize, ping

    [Fact]
    public async Task Initialize_echoes_the_clients_protocol_version_and_announces_tools()
    {
        var response = await Call(NewDispatcher(), new FakeSeat(),
            "{'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2024-11-05'}}");

        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, response.GetProperty("id").GetInt32());
        var result = response.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("stardew", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Initialize_without_a_version_uses_the_fallback()
    {
        var response = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':1,'method':'initialize'}");
        Assert.Equal(McpDispatcher.ProtocolFallback, response.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Initialize_instructions_name_the_seat_in_split_screen_only()
    {
        const string message = "{'jsonrpc':'2.0','id':1,'method':'initialize','params':{}}";
        var split = await Call(NewDispatcher(splitScreen: true), new FakeSeat { Id = "p3" }, message);
        var single = await Call(NewDispatcher(splitScreen: false), new FakeSeat { Id = "p3" }, message);

        Assert.Contains("player p3", split.GetProperty("result").GetProperty("instructions").GetString());
        Assert.DoesNotContain("p3", single.GetProperty("result").GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Ping_answers_with_an_empty_result()
    {
        var response = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':9,'method':'ping'}");
        Assert.Equal(JsonValueKind.Object, response.GetProperty("result").ValueKind);
        Assert.Empty(response.GetProperty("result").EnumerateObject());
    }

    [Fact]
    public async Task Request_ids_come_back_unchanged_whether_number_or_string()
    {
        var number = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':42,'method':'ping'}");
        Assert.Equal(JsonValueKind.Number, number.GetProperty("id").ValueKind);
        Assert.Equal(42, number.GetProperty("id").GetInt32());

        var text = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':'req-7','method':'ping'}");
        Assert.Equal("req-7", text.GetProperty("id").GetString());
    }

    // ---------------------------------------------------------------- tools/list

    [Fact]
    public async Task Tools_list_advertises_every_tool_with_name_description_and_input_schema()
    {
        var response = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':2,'method':'tools/list'}");
        var tools = response.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();

        Assert.Equal(SampleTools.All().Keys.OrderBy(n => n).ToList(), tools.Select(t => t.GetProperty("name").GetString()!).OrderBy(n => n).ToList());
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Every_advertised_schema_passes_the_tool_contract()
    {
        var response = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':2,'method':'tools/list'}");
        var problems = new List<string>();
        foreach (var tool in response.GetProperty("result").GetProperty("tools").EnumerateArray())
            problems.AddRange(ToolContract.CheckSchema(tool.GetProperty("name").GetString()!, tool.GetProperty("inputSchema")));
        Assert.Empty(problems);
    }

    [Fact]
    public async Task Every_advertised_action_tool_takes_a_reason()
    {
        var tools = SampleTools.All();
        var response = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':2,'method':'tools/list'}");
        foreach (var tool in response.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            if (tools[name].Instant)
                continue;
            Assert.True(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("reason", out _), $"{name} has no reason");
        }
    }

    [Fact]
    public async Task Tools_list_does_not_need_the_seat_to_be_in_the_game()
    {
        var response = await Call(NewDispatcher(), new FakeSeat { Active = false }, "{'jsonrpc':'2.0','id':2,'method':'tools/list'}");
        Assert.True(response.GetProperty("result").TryGetProperty("tools", out _));
    }

    // ---------------------------------------------------------------- tools/call

    [Fact]
    public async Task Calling_an_unknown_tool_is_a_tool_error_not_a_protocol_error()
    {
        var seat = new FakeSeat();
        var response = await Call(NewDispatcher(), seat,
            "{'jsonrpc':'2.0','id':3,'method':'tools/call','params':{'name':'teleport','arguments':{}}}");

        Assert.False(response.TryGetProperty("error", out _));
        Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("Unknown tool teleport", ResultText(response));
        Assert.Empty(seat.Calls);
    }

    [Fact]
    public async Task Calling_a_tool_on_a_seat_that_is_not_in_the_game_is_a_tool_error()
    {
        var seat = new FakeSeat { Active = false, Id = "p3" };
        var response = await Call(NewDispatcher(), seat,
            "{'jsonrpc':'2.0','id':3,'method':'tools/call','params':{'name':'look','arguments':{}}}");

        Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("p3", ResultText(response));
        Assert.Contains("not in the game yet", ResultText(response));
        Assert.Empty(seat.Calls);
    }

    [Fact]
    public async Task Calling_a_tool_hands_name_and_arguments_to_the_seat_and_returns_its_text()
    {
        var seat = new FakeSeat { Answer = (true, "Arrived at (5,6).") };
        var response = await Call(NewDispatcher(), seat,
            "{'jsonrpc':'2.0','id':4,'method':'tools/call','params':{'name':'walk_to','arguments':{'x':5,'y':6,'reason':'go'}}}");

        var call = Assert.Single(seat.Calls);
        Assert.Equal("walk_to", call.Tool);
        Assert.Equal("{\"x\":5,\"y\":6,\"reason\":\"go\"}", call.Args);
        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("text", result.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("Arrived at (5,6).", ResultText(response));
    }

    [Fact]
    public async Task A_failed_tool_comes_back_with_is_error_and_its_message()
    {
        var seat = new FakeSeat { Answer = (false, "No \"Axe\" in inventory.") };
        var response = await Call(NewDispatcher(), seat,
            "{'jsonrpc':'2.0','id':5,'method':'tools/call','params':{'name':'harvest','arguments':{'tiles':[[1,1]],'reason':'x'}}}");

        Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("No \"Axe\" in inventory.", ResultText(response));
    }

    [Fact]
    public async Task Calling_a_tool_without_arguments_passes_an_empty_object()
    {
        var seat = new FakeSeat();
        await Call(NewDispatcher(), seat, "{'jsonrpc':'2.0','id':6,'method':'tools/call','params':{'name':'look'}}");

        Assert.Equal("{}", Assert.Single(seat.Calls).Args);
    }

    // ---------------------------------------------------------------- unknown methods, notifications

    [Fact]
    public async Task Unknown_method_is_a_json_rpc_method_not_found_error()
    {
        var response = await Call(NewDispatcher(), new FakeSeat(), "{'jsonrpc':'2.0','id':8,'method':'resources/list'}");

        Assert.Equal(8, response.GetProperty("id").GetInt32());
        Assert.False(response.TryGetProperty("result", out _));
        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("resources/list", response.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_notification_gets_no_reply()
    {
        var reply = await NewDispatcher().Dispatch(Request("{'jsonrpc':'2.0','method':'notifications/initialized'}"), new FakeSeat());
        Assert.Null(reply);
    }

    [Fact]
    public async Task Every_message_marks_the_seat_as_having_an_ai_attached_even_a_notification()
    {
        var seat = new FakeSeat();
        var mcp = NewDispatcher();
        await mcp.Dispatch(Request("{'jsonrpc':'2.0','method':'notifications/initialized'}"), seat);
        await mcp.Dispatch(Request("{'jsonrpc':'2.0','id':1,'method':'ping'}"), seat);
        Assert.Equal(2, seat.Contacts);
    }

    // ---------------------------------------------------------------- whole POST bodies

    [Fact]
    public async Task A_single_request_gets_status_200_and_the_seats_session_id()
    {
        var reply = await NewDispatcher().Handle(Request("{'jsonrpc':'2.0','id':1,'method':'ping'}"), new FakeSeat { Id = "p2" });

        Assert.Equal(200, reply.Status);
        Assert.NotNull(reply.Body);
        Assert.Equal("stardew-p2", reply.SessionId);
    }

    [Fact]
    public async Task A_single_notification_is_answered_with_202_and_no_body()
    {
        var reply = await NewDispatcher().Handle(Request("{'jsonrpc':'2.0','method':'notifications/initialized'}"), new FakeSeat());

        Assert.Equal(202, reply.Status);
        Assert.Null(reply.Body);
        Assert.Null(reply.SessionId);
    }

    [Fact]
    public async Task A_batch_is_answered_in_order_and_leaves_out_notifications()
    {
        var batch = Request(
            "[{'jsonrpc':'2.0','id':1,'method':'ping'}," +
            "{'jsonrpc':'2.0','method':'notifications/initialized'}," +
            "{'jsonrpc':'2.0','id':'b','method':'nope'}," +
            "{'jsonrpc':'2.0','id':3,'method':'tools/list'}]");
        var reply = await NewDispatcher().Handle(batch, new FakeSeat());

        Assert.Equal(200, reply.Status);
        var responses = Wire(reply.Body).EnumerateArray().ToList();
        Assert.Equal(3, responses.Count);
        Assert.Equal(1, responses[0].GetProperty("id").GetInt32());
        Assert.True(responses[0].TryGetProperty("result", out _));
        Assert.Equal("b", responses[1].GetProperty("id").GetString());
        Assert.Equal(-32601, responses[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(3, responses[2].GetProperty("id").GetInt32());
        Assert.True(responses[2].GetProperty("result").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task A_batch_of_only_notifications_is_answered_with_202()
    {
        var reply = await NewDispatcher().Handle(
            Request("[{'jsonrpc':'2.0','method':'notifications/initialized'},{'jsonrpc':'2.0','method':'notifications/cancelled'}]"),
            new FakeSeat());

        Assert.Equal(202, reply.Status);
        Assert.Null(reply.Body);
    }

    [Fact]
    public async Task An_empty_batch_is_answered_with_202()
    {
        var reply = await NewDispatcher().Handle(Request("[]"), new FakeSeat());

        Assert.Equal(202, reply.Status);
        Assert.Null(reply.Body);
    }

    [Fact]
    public async Task Tool_calls_in_a_batch_run_one_after_another()
    {
        var seat = new FakeSeat();
        await NewDispatcher().Handle(Request(
            "[{'jsonrpc':'2.0','id':1,'method':'tools/call','params':{'name':'look','arguments':{'radius':5}}}," +
            "{'jsonrpc':'2.0','id':2,'method':'tools/call','params':{'name':'walk_to','arguments':{'x':1,'y':2,'reason':'r'}}}]"), seat);

        Assert.Equal(new List<string> { "look", "walk_to" }, seat.Calls.Select(c => c.Tool).ToList());
    }
}
