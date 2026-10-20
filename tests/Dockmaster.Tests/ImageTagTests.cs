using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ImageTagTests
{
    [Theory]
    [InlineData("registry.local:5000/shop/api:1.2.0", "registry.local:5000/shop/api:1.3.0")]
    [InlineData("\"registry.local:5000/shop/api\"", "registry.local:5000/shop/api:1.3.0")]
    [InlineData("nginx:1.27@sha256:abcdef", "nginx:1.3.0")]
    public void Sets_the_tag_and_keeps_the_rest(string image, string expected)
    {
        var yaml = $"services:\n  api:\n    image: {image}   # built by CI\n";
        Assert.Equal($"services:\n  api:\n    image: {expected}   # built by CI\n", ComposeEditor.SetImageTag(yaml, "api", "1.3.0"));
    }

    [Fact]
    public void An_image_of_variables_or_a_bad_tag_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ComposeEditor.SetImageTag("services:\n  api:\n    image: ${REG}/api:${TAG}\n", "api", "2"));
        Assert.Throws<InvalidOperationException>(() => ComposeEditor.SetImageTag("services:\n  api:\n    image: api:1\n", "api", "1.0 && rm"));
    }
}
