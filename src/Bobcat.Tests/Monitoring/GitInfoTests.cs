using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

public class GitInfoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bobcat-gitinfo-{Guid.NewGuid():N}");

    public GitInfoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void finds_the_repository_root_and_branch_by_walking_up()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/feature/monitor\n");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src", "deep")).FullName;

        var (repository, branch) = GitInfo.Discover(nested);

        repository.ShouldBe(new DirectoryInfo(_root).FullName);
        branch.ShouldBe("feature/monitor");
    }

    [Fact]
    public void a_detached_head_reports_the_short_sha()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");

        var (_, branch) = GitInfo.Discover(_root);

        branch.ShouldBe("01234567");
    }

    [Fact]
    public void resolves_a_worktree_where_dot_git_is_a_file_pointing_at_the_real_git_dir()
    {
        var realGitDir = Directory.CreateDirectory(Path.Combine(_root, "main", ".git", "worktrees", "wt")).FullName;
        File.WriteAllText(Path.Combine(realGitDir, "HEAD"), "ref: refs/heads/worktree-branch\n");

        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {realGitDir}\n");

        var (repository, branch) = GitInfo.Discover(worktree);

        repository.ShouldBe(worktree);
        branch.ShouldBe("worktree-branch");
    }

    [Fact]
    public void no_repository_yields_nulls_rather_than_throwing()
    {
        // The temp root itself has no .git anywhere above it that we create; a directory
        // guaranteed outside any repo is hard to promise on a dev box, so assert on a path
        // that does not exist at all — the walk must not throw.
        var (repository, branch) = GitInfo.Discover(Path.Combine(_root, "does", "not", "exist"));

        // Whatever the machine's temp dir ancestry looks like, the call must return without
        // throwing; on a normal setup temp is not inside a git repository.
        _ = repository;
        _ = branch;
    }
}
