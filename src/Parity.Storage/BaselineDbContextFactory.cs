using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Parity.Storage;

/// <summary>
/// 設計期(design-time)工廠:只給 `dotnet ef migrations` 用,讓工具不必啟動整個 CLI
/// 就能建出 BaselineDbContext。連線字串是佔位用的——產 migration 不會真的碰這個檔。
/// 執行期(parity baseline)走 BaselineStore,不經這裡。
/// </summary>
internal sealed class BaselineDbContextFactory : IDesignTimeDbContextFactory<BaselineDbContext>
{
    public BaselineDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BaselineDbContext>()
            .UseSqlite("Data Source=parity.baseline.db")
            .Options;
        return new BaselineDbContext(options);
    }
}
