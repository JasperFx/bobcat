using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// bobcat#532 — an Event Model half says where it was produced: the checkout root, the
/// <c>owner/name</c> of its origin remote and its branch, read from files only.
/// </summary>
public class CheckoutProvenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bobcat-provenance-{Guid.NewGuid():N}");

    public CheckoutProvenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void writeConfig(string gitDir, string originUrl)
        => File.WriteAllText(Path.Combine(gitDir, "config"), $"""
            [core]
            	bare = false
            [remote "upstream"]
            	url = https://github.com/someone-else/fork.git
            [remote "origin"]
            	url = {originUrl}
            	fetch = +refs/heads/*:refs/remotes/origin/*
            [branch "main"]
            	remote = origin
            """);

    [Fact]
    public void a_checkout_gives_its_root_its_origin_and_its_branch()
    {
        var gitDir = Directory.CreateDirectory(Path.Combine(_root, ".git")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/model\n");
        writeConfig(gitDir, "git@github.com:JasperFx/critter-crush.git");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src", "Specs")).FullName;

        var provenance = CheckoutProvenance.Discover(nested).ShouldNotBeNull();

        provenance.Root.ShouldBe(new DirectoryInfo(_root).FullName);
        provenance.Repository.ShouldBe("JasperFx/critter-crush");
        provenance.Branch.ShouldBe("feature/model");
    }

    [Fact]
    public void a_worktree_reads_its_own_branch_and_the_shared_config()
    {
        // The worktree's git dir holds HEAD; the config lives in the main checkout's git dir,
        // which `commondir` names relative to the worktree's.
        var mainGitDir = Directory.CreateDirectory(Path.Combine(_root, "main", ".git")).FullName;
        writeConfig(mainGitDir, "https://github.com/JasperFx/bobcat.git");
        var worktreeGitDir = Directory.CreateDirectory(Path.Combine(mainGitDir, "worktrees", "wt")).FullName;
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/wt-branch\n");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..\n");

        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");

        var provenance = CheckoutProvenance.Discover(worktree).ShouldNotBeNull();

        provenance.Root.ShouldBe(worktree);
        provenance.Repository.ShouldBe("JasperFx/bobcat");
        provenance.Branch.ShouldBe("wt-branch");
    }

    [Fact]
    public void a_detached_head_has_no_branch_and_no_origin_has_no_repository()
    {
        var gitDir = Directory.CreateDirectory(Path.Combine(_root, ".git")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");

        var provenance = CheckoutProvenance.Discover(_root).ShouldNotBeNull();

        provenance.Root.ShouldBe(new DirectoryInfo(_root).FullName);
        provenance.Branch.ShouldBeNull();
        provenance.Repository.ShouldBeNull();
        provenance.ToQueryString().ShouldBe("?root=" + Uri.EscapeDataString(provenance.Root));
    }

    [Theory]
    [InlineData("https://github.com/JasperFx/bobcat.git", "JasperFx/bobcat")]
    [InlineData("https://github.com/JasperFx/bobcat", "JasperFx/bobcat")]
    [InlineData("https://github.com/JasperFx/bobcat/", "JasperFx/bobcat")]
    [InlineData("git@github.com:JasperFx/bobcat.git", "JasperFx/bobcat")]
    [InlineData("ssh://git@github.com/JasperFx/bobcat.git", "JasperFx/bobcat")]
    [InlineData("https://gitlab.example.com/group/sub/project.git", "sub/project")]
    [InlineData("/Users/someone/code/bobcat", null)]
    [InlineData("https://github.com/only-one", null)]
    [InlineData("", null)]
    public void the_repository_is_the_owner_and_name_a_remote_url_ends_in(string remote, string? expected)
        => CheckoutProvenance.RepositoryFromRemote(remote).ShouldBe(expected);

    [Fact]
    public void the_query_string_escapes_every_field()
    {
        var provenance = new CheckoutProvenance("/Users/me/code/critter crush", "JasperFx/critter-crush", "feature/a&b");

        provenance.ToQueryString().ShouldBe(
            "?root=%2FUsers%2Fme%2Fcode%2Fcritter%20crush&repository=JasperFx%2Fcritter-crush&branch=feature%2Fa%26b");
    }
}
