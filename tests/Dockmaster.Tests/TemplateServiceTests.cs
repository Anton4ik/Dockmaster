using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

/// <summary>Instances of templates in the whole server: the limit, edited templates and deleting.</summary>
[Collection(ServerCollection.Name)]
public sealed class TemplateServiceTests(ServerFixture f)
{
    private TemplateService Service => f.App.Services.GetRequiredService<TemplateService>();

    private async Task<TemplateEntity> TemplateAsync(string name, int maxInstances = 0, params TemplateVariable[] variables)
    {
        await using var db = await f.Db();
        var t = new TemplateEntity { Name = name, ComposeYaml = "services:\n  app:\n    image: nginx\n", Variables = Templates.SerializeVariables(variables), MaxInstances = maxInstances };
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    private async Task<int> InstanceAsync(TemplateEntity t, string name, Dictionary<string, string>? values = null)
    {
        var (p, refused) = await Service.CreateAsync(t, name, values, "alpha", CancellationToken.None);
        Assert.True(p != null, refused?.Message);
        return p!.Id;
    }

    [Fact]
    public async Task Instances_made_at_the_same_time_keep_to_the_limit()
    {
        var t = await TemplateAsync("limited", maxInstances: 2);
        var made = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Service.CreateAsync(t, $"i{i}", null, "alpha", CancellationToken.None)));
        Assert.Equal(2, made.Count(m => m.Project != null));
        Assert.All(made.Where(m => m.Project == null), m => Assert.Equal(429, m.Refused!.Status));
    }

    [Fact]
    public async Task A_variable_taken_out_of_the_template_does_not_block_setting_the_others()
    {
        var t = await TemplateAsync("shrinking", 0, new("OLD"), new("KEEP"));
        var id = await InstanceAsync(t, "a", new() { ["OLD"] = "1", ["KEEP"] = "2" });
        t.Variables = Templates.SerializeVariables([new TemplateVariable("KEEP")]);

        Assert.Null(await Service.SetVariablesAsync(t, id, new Dictionary<string, string> { ["KEEP"] = "3" }, CancellationToken.None));
        await using var db = await f.Db();
        var values = Templates.ParseValues((await db.Projects.SingleAsync(p => p.Id == id)).InstanceVariables);
        Assert.Equal(new Dictionary<string, string> { ["KEEP"] = "3" }, values);
    }

    [Fact]
    public async Task A_deploy_takes_the_templates_variables_as_they_are_now()
    {
        var t = await TemplateAsync("growing", 0, new TemplateVariable("SIZE", Pattern: "[0-9]+"));
        var id = await InstanceAsync(t, "a", new() { ["SIZE"] = "10" });

        // a new variable with a default reaches the instance
        t.Variables = Templates.SerializeVariables([new("SIZE", Pattern: "[0-9]+"), new("BOTS", Default: "5")]);
        await Service.RunAsync(t, id, "deploy", "test");
        await using (var db = await f.Db())
        {
            var p = await db.Projects.SingleAsync(x => x.Id == id);
            Assert.Equal("5", Templates.ParseValues(p.InstanceVariables)["BOTS"]);
            Assert.Contains("BOTS='5'", p.EnvFile);
        }

        // a value the edited template no longer accepts stops the deploy before anything runs
        t.Variables = Templates.SerializeVariables([new("SIZE", Pattern: "[a-z]+")]);
        var (ok, message) = await Service.RunAsync(t, id, "deploy", "test");
        Assert.False(ok);
        Assert.Contains("set the instance's variables again", message);
    }

    [Fact]
    public async Task Deleting_an_instance_its_server_cannot_take_down_keeps_it_unless_forced()
    {
        var t = await TemplateAsync("deleting");
        var id = await InstanceAsync(t, "a");

        // alpha is not connected: nothing can be taken down there
        var (ok, _) = await Service.DeleteAsync(id, false, "test");
        Assert.False(ok);
        await using (var db = await f.Db()) Assert.True(await db.Projects.AnyAsync(p => p.Id == id));

        (ok, var message) = await Service.DeleteAsync(id, true, "test");
        Assert.True(ok);
        Assert.Contains("forgotten", message);
        await using (var db = await f.Db()) Assert.False(await db.Projects.AnyAsync(p => p.Id == id));
    }
}
