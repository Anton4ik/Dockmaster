using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class SelfIdTests
{
    private const string Inner = "4db50a60cba16d5ae39397cda4d1919e2ad17f7824f21a68cc86ed34d6c8fd27";
    private const string Outer = "1dc9e1bbbe3164a94d4c0e9220fa5478727cf0d4f9537d548a3bfa320b83c9be";

    [Fact]
    public void Takes_the_id_before_hostname_on_the_etc_hostname_mount()
    {
        // docker-in-docker: the outer container's id shows up earlier in the same path and on other lines
        string[] lines =
        [
            $"600 590 0:50 /containers/{Outer}/mounts/shm /dev/shm rw,nosuid,nodev,noexec,relatime - tmpfs shm rw,size=65536k",
            $"601 590 253:0 /var/lib/docker/volumes/dm/_data/containers/{Inner}/resolv.conf /etc/resolv.conf rw,relatime - ext4 /dev/mapper/root rw",
            $"602 590 253:0 /var/lib/docker/volumes/dm/_data/containers/{Inner}/hostname /etc/hostname rw,relatime - ext4 /dev/mapper/root rw",
        ];
        Assert.Equal(Inner, DockerService.SelfIdFrom(lines));
    }

    [Fact]
    public void Falls_back_to_the_first_container_path_without_an_etc_hostname_line()
    {
        string[] lines = [$"600 590 0:50 /containers/{Outer}/mounts/shm /dev/shm rw - tmpfs shm rw", "601 590 0:51 / /proc rw - proc proc rw"];
        Assert.Equal(Outer, DockerService.SelfIdFrom(lines));
        Assert.Null(DockerService.SelfIdFrom(["601 590 0:51 / /proc rw - proc proc rw"]));
    }
}
