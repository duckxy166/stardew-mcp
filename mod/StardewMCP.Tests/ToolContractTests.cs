using System;
using System.Text.Json;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

/// <summary>
/// The rules for tools/list: valid schemas, 'required' names that exist, 'reason' on every action.
/// These run the checker on sample tools and on hand-written broken ones. The real tool list needs the
/// game to build, so it is checked when the mod starts (AgentHttp logs the findings) and by
/// runner/tests/test_tool_sources.py, which reads the C# source.
/// </summary>
public class ToolContractTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json.Replace('\'', '"')).RootElement.Clone();

    private static ToolDef Tool(string name, object schema, bool instant = false) => new()
    {
        Name = name,
        Description = "Does " + name + ".",
        Schema = schema,
        Instant = instant,
        Run = (a, c) => Array.Empty<object>().GetEnumerator()
    };

    // ---------------------------------------------------------------- whole tools

    [Fact]
    public void Well_formed_tools_have_no_problems()
    {
        Assert.Empty(ToolContract.Check(SampleTools.All().Values));
    }

    [Fact]
    public void Action_tool_without_reason_is_reported()
    {
        var problems = ToolContract.Check(new[] { Tool("close_menu", AgentTools.Obj(new { })) });
        var problem = Assert.Single(problems);
        Assert.Contains("close_menu", problem);
        Assert.Contains("reason", problem);
    }

    [Fact]
    public void Instant_tool_may_leave_out_reason()
    {
        Assert.Empty(ToolContract.Check(new[] { Tool("status", AgentTools.Obj(new { }), instant: true) }));
    }

    [Fact]
    public void Reason_must_be_a_string()
    {
        var schema = AgentTools.Obj(new { reason = AgentTools.Int("Why") });
        Assert.Contains(ToolContract.Check(new[] { Tool("wait", schema) }), p => p.Contains("reason"));
    }

    [Fact]
    public void Reason_may_be_optional_but_must_exist()
    {
        // respond() and menu() list reason without requiring it.
        var schema = AgentTools.Obj(new { option = AgentTools.Int("Option"), reason = AgentTools.Str("Why") });
        Assert.Empty(ToolContract.Check(new[] { Tool("respond", schema) }));
    }

    [Theory]
    [InlineData("Walk")]
    [InlineData("walk-to")]
    [InlineData("1walk")]
    [InlineData("")]
    public void Tool_names_must_be_snake_case(string name)
    {
        var problems = ToolContract.Check(new[] { Tool(name, AgentTools.Obj(new { reason = AgentTools.Str("Why") })) });
        Assert.Contains(problems, p => p.Contains("snake_case"));
    }

    [Fact]
    public void Duplicate_tool_names_are_reported()
    {
        var schema = AgentTools.Obj(new { reason = AgentTools.Str("Why") });
        var problems = ToolContract.Check(new[] { Tool("wait", schema), Tool("wait", schema) });
        Assert.Contains(problems, p => p.Contains("duplicate"));
    }

    [Fact]
    public void Missing_description_run_or_schema_is_reported()
    {
        var noDescription = Tool("a_tool", AgentTools.Obj(new { reason = AgentTools.Str("Why") }));
        noDescription.Description = " ";
        var noRun = Tool("b_tool", AgentTools.Obj(new { reason = AgentTools.Str("Why") }));
        noRun.Run = null!;
        var noSchema = Tool("c_tool", AgentTools.Obj(new { }));
        noSchema.Schema = null!;

        var problems = ToolContract.Check(new[] { noDescription, noRun, noSchema });
        Assert.Contains(problems, p => p.StartsWith("a_tool") && p.Contains("description"));
        Assert.Contains(problems, p => p.StartsWith("b_tool") && p.Contains("Run"));
        Assert.Contains(problems, p => p.StartsWith("c_tool") && p.Contains("schema"));
    }

    // ---------------------------------------------------------------- schemas

    [Fact]
    public void Required_name_missing_from_properties_is_reported()
    {
        var schema = AgentTools.Obj(new { x = AgentTools.Int("Tile x"), reason = AgentTools.Str("Why") }, "x", "y", "reason");
        var problems = ToolContract.Check(new[] { Tool("walk_to", schema) });
        var problem = Assert.Single(problems);
        Assert.Contains("required 'y'", problem);
    }

    [Fact]
    public void Required_listed_twice_is_reported()
    {
        var schema = AgentTools.Obj(new { x = AgentTools.Int("Tile x"), reason = AgentTools.Str("Why") }, "x", "x", "reason");
        Assert.Contains(ToolContract.Check(new[] { Tool("walk_to", schema) }), p => p.Contains("twice"));
    }

    [Fact]
    public void Required_that_is_not_an_array_of_strings_is_reported()
    {
        Assert.Contains(ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{},'required':'x'}")), p => p.Contains("must be an array"));
        Assert.Contains(ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{'x':{'type':'string'}},'required':[1]}")), p => p.Contains("non-string"));
    }

    [Fact]
    public void Top_level_schema_must_be_an_object_schema_with_properties()
    {
        Assert.Contains(ToolContract.CheckSchema("t", Schema("[]")), p => p.Contains("not a JSON object"));
        Assert.Contains(ToolContract.CheckSchema("t", Schema("{'type':'string','properties':{}}")), p => p.Contains("\"object\""));
        Assert.Contains(ToolContract.CheckSchema("t", Schema("{'type':'object'}")), p => p.Contains("properties"));
        Assert.Contains(ToolContract.CheckSchema("t", Schema("{'type':'object','properties':[]}")), p => p.Contains("properties"));
    }

    [Theory]
    [InlineData("{'description':'no type'}")]
    [InlineData("{'type':'str'}")]
    [InlineData("{'type':5}")]
    [InlineData("'string'")]
    public void Property_needs_a_known_type(string property)
    {
        var problems = ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{'p':" + property + "}}"));
        Assert.Contains(problems, p => p.StartsWith("t.p"));
    }

    [Fact]
    public void Array_property_must_say_what_it_holds()
    {
        var problems = ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{'tiles':{'type':'array'}}}"));
        Assert.Contains(problems, p => p.Contains("items"));
        Assert.Empty(ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{'tiles':{'type':'array','items':{'type':'integer'}}}}")));
    }

    [Fact]
    public void Enum_must_be_a_non_empty_array()
    {
        Assert.Contains(ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{'a':{'type':'string','enum':[]}}}")), p => p.Contains("enum"));
        Assert.Contains(ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{'a':{'type':'string','enum':'x'}}}")), p => p.Contains("enum"));
        Assert.Empty(ToolContract.CheckSchema("t", Schema("{'type':'object','properties':{'a':{'type':'string','enum':['x','y']}}}")));
    }

    [Fact]
    public void Empty_object_schema_is_valid()
    {
        Assert.Empty(ToolContract.CheckSchema("status", Schema("{'type':'object','properties':{},'required':[]}")));
    }
}
