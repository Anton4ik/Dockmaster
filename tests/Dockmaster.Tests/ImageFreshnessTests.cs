using System.Text.Json;
using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

/// <summary>When the dot says "redeploy": the tag on the host names an image other than the one the container runs.</summary>
public class ImageFreshnessTests
{
    private const string Amd64 = "sha256:a2aa3d282092ddb5f62758181c35b5fae83079af74f77f04ee4af47d912b13ce";
    private const string OldIndex = "sha256:eb42a404757f041036e8745045a4a1945bf07d305ef0b1a6d15e8145a6280b7c";
    private const string NewIndex = "sha256:1e1163ba658e92ee2c9ce23e980fb5660db639298d2b29956fb2f8404130b400";

    // an image inspect with manifests=1 on the containerd store: a BuildKit index of the amd64 image and its attestation
    private static EngineApi.ImageSummary Index(string id, string platform, string attestation, string arch = "amd64") =>
        EngineApi.ParseImage(JsonDocument.Parse("""
            {"Id":"@id@","RepoDigests":["registry/api@@id@"],
             "Descriptor":{"digest":"@id@","mediaType":"application/vnd.oci.image.index.v1+json","size":856},
             "Manifests":[
              {"ID":"@platform@","Kind":"image","Available":true,
               "Descriptor":{"digest":"@platform@","platform":{"architecture":"amd64","os":"linux"}},
               "ImageData":{"Platform":{"architecture":"amd64","os":"linux"},"Containers":[]}},
              {"ID":"@attestation@","Kind":"attestation","Available":true,"AttestationData":{"For":"@platform@"},
               "Descriptor":{"digest":"@attestation@","platform":{"architecture":"unknown","os":"unknown"}}}]}
            """.Replace("@id@", id).Replace("@platform@", platform).Replace("@attestation@", attestation)).RootElement, arch);

    [Fact]
    public void The_platform_image_of_an_index_is_the_pulled_image_manifest_not_the_attestation()
    {
        var img = Index(NewIndex, Amd64, "sha256:e145");
        Assert.Equal(NewIndex, img.Id);
        Assert.Equal(Amd64, img.Platform);
    }

    [Fact]
    public void A_multi_arch_index_gives_the_hosts_architecture()
    {
        var img = EngineApi.ParseImage(JsonDocument.Parse("""
            {"Id":"sha256:idx","Manifests":[
              {"ID":"sha256:amd","Kind":"image","Available":true,"ImageData":{"Platform":{"architecture":"amd64","os":"linux"}}},
              {"ID":"sha256:arm","Kind":"image","Available":true,"ImageData":{"Platform":{"architecture":"arm64","os":"linux"}}},
              {"ID":"sha256:ppc","Kind":"image","Available":false}]}
            """).RootElement, "arm64");
        Assert.Equal("sha256:arm", img.Platform);
    }

    [Fact]
    public void The_classic_store_lists_no_manifests()
    {
        var img = EngineApi.ParseImage(JsonDocument.Parse("""{"Id":"sha256:cfg","RepoDigests":[]}""").RootElement);
        Assert.Equal("", img.Platform);
    }

    [Fact]
    public void The_same_platform_image_under_a_new_index_is_not_newer()
    {
        // a cache-only rebuild: the same amd64 manifest, a new attestation, so a new index; compose would not recreate
        var running = Index(OldIndex, Amd64, "sha256:9ee4");
        var tag = Index(NewIndex, Amd64, "sha256:e145");
        Assert.False(DockerService.IsNewerImage(tag, OldIndex, running, Amd64));
        Assert.False(DockerService.IsNewerImage(tag, OldIndex, running, ""));
    }

    [Fact]
    public void A_different_platform_image_is_newer()
    {
        var running = Index(OldIndex, Amd64, "sha256:9ee4");
        var tag = Index(NewIndex, "sha256:b0b0", "sha256:e145");
        Assert.True(DockerService.IsNewerImage(tag, OldIndex, running, Amd64));
        Assert.True(DockerService.IsNewerImage(tag, OldIndex, running, ""));
    }

    [Fact]
    public void The_same_index_is_not_newer()
    {
        var img = Index(NewIndex, Amd64, "sha256:e145");
        Assert.False(DockerService.IsNewerImage(img, NewIndex, img, Amd64));
    }

    [Fact]
    public void A_single_manifest_image_compares_by_its_manifest()
    {
        // no index (built without attestations): the id is the manifest, and docker lists it as its own only child
        EngineApi.ImageSummary Single(string id) => EngineApi.ParseImage(JsonDocument.Parse("""
            {"Id":"@id@","Descriptor":{"digest":"@id@","mediaType":"application/vnd.oci.image.manifest.v1+json"},
             "Manifests":[{"ID":"@id@","Kind":"image","Available":true,"ImageData":{"Platform":{"architecture":"amd64","os":"linux"}}}]}
            """.Replace("@id@", id)).RootElement);
        Assert.False(DockerService.IsNewerImage(Single(Amd64), Amd64, Single(Amd64), Amd64));
        Assert.True(DockerService.IsNewerImage(Single("sha256:b0b0"), Amd64, Single(Amd64), Amd64));
        // a container created from the single image, the tag now an index around that same manifest
        Assert.False(DockerService.IsNewerImage(Index(NewIndex, Amd64, "sha256:e145"), Amd64, Single(Amd64), Amd64));
    }

    [Fact]
    public void A_container_without_the_compose_label_uses_its_own_index()
    {
        var tag = Index(NewIndex, Amd64, "sha256:e145");
        Assert.False(DockerService.IsNewerImage(tag, OldIndex, Index(OldIndex, Amd64, "sha256:9ee4"), ""));
        Assert.True(DockerService.IsNewerImage(tag, OldIndex, Index(OldIndex, "sha256:0ld0", "sha256:9ee4"), ""));
        // nothing known of the container's image: only the ids are left, and they differ
        Assert.True(DockerService.IsNewerImage(tag, OldIndex, null, ""));
    }

    [Fact]
    public void Without_its_image_the_compose_label_decides()
    {
        var tag = Index(NewIndex, Amd64, "sha256:e145");
        Assert.False(DockerService.IsNewerImage(tag, OldIndex, null, Amd64));
        Assert.True(DockerService.IsNewerImage(tag, OldIndex, null, "sha256:0ld0"));
    }

    [Fact]
    public void The_classic_store_compares_ids()
    {
        var tag = new EngineApi.ImageSummary("sha256:cfg2", []);
        var running = new EngineApi.ImageSummary("sha256:cfg1", []);
        // compose's label there is the config digest, the container's own id
        Assert.True(DockerService.IsNewerImage(tag, "sha256:cfg1", running, "sha256:cfg1"));
        Assert.False(DockerService.IsNewerImage(running, "sha256:cfg1", running, "sha256:cfg1"));
    }
}
