namespace Parity.Tests;

/// <summary>
/// ActionYmlTests 在 Windows 上要用 Git Bash 跑 action 的步驟。原本寫死 Program Files\Git\bin\bash.exe,
/// Git 裝在別處(scoop、自訂路徑)的開發者會直接紅燈。改成先從 PATH 上的 git.exe 反推安裝位置,
/// 再找 PATH 上的 bash.exe(跳過 System32 / WindowsApps 那個 WSL 的 bash),最後才看 Program Files。
/// 用假的目錄結構測,不依賴這台機器裝在哪。
/// </summary>
public class GitBashLocatorTests
{
    private static string Touch(TempDir dir, params string[] parts)
    {
        var path = dir.File(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Git_installed_anywhere_is_found_through_git_on_the_path()
    {
        using var dir = new TempDir();
        Touch(dir, "tools", "Git", "cmd", "git.exe");
        var bash = Touch(dir, "tools", "Git", "bin", "bash.exe");

        Assert.Equal(bash, ActionYmlTests.FindGitBash(dir.File("tools", "Git", "cmd"), []));
    }

    [Fact]
    public void A_bash_on_the_path_is_used_but_not_the_wsl_launcher_in_system32()
    {
        using var dir = new TempDir();
        Touch(dir, "Windows", "System32", "bash.exe"); // WSL 的啟動器,不是 Git Bash
        var msys = Touch(dir, "msys64", "usr", "bin", "bash.exe");
        var path = string.Join(Path.PathSeparator, dir.File("Windows", "System32"), dir.File("msys64", "usr", "bin"));

        Assert.Equal(msys, ActionYmlTests.FindGitBash(path, []));
    }

    [Fact]
    public void Program_files_is_the_last_resort_and_nothing_found_is_null()
    {
        using var dir = new TempDir();
        var bash = Touch(dir, "Program Files", "Git", "bin", "bash.exe");

        Assert.Equal(bash, ActionYmlTests.FindGitBash("", [dir.File("Program Files")]));
        Assert.Null(ActionYmlTests.FindGitBash("", [dir.File("nowhere")]));
    }
}
