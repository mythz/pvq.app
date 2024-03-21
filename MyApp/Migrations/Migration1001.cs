using ServiceStack.DataAnnotations;
using ServiceStack.OrmLite;

namespace MyApp.Migrations;

[NamedConnection(MyApp.ServiceModel.Databases.Analytics)]
public class Migration1001 : MigrationBase
{
    public class StatBase
    {
        public string RefId { get; set; }
        public string? UserName { get; set; }
        public string RemoteIp { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    public class PostStat : StatBase
    {
        [AutoIncrement]
        public int Id { get; set; }
        public int PostId { get; set; }
    }

    public class SearchStat : StatBase
    {
        [AutoIncrement]
        public int Id { get; set; }
        public string? Query { get; set; }
    }

    public override void Up()
    {
        Db.CreateTable<PostStat>();
        Db.CreateTable<SearchStat>();
    }

    public override void Down()
    {
        Db.CreateTable<PostStat>();
        Db.CreateTable<SearchStat>();
    }
}
