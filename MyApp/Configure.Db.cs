using Microsoft.EntityFrameworkCore;
using ServiceStack.Data;
using ServiceStack.OrmLite;
using MyApp.Data;
using MyApp.ServiceModel;

[assembly: HostingStartup(typeof(MyApp.ConfigureDb))]

namespace MyApp;

public class ConfigureDb : IHostingStartup
{
    public const string AnalyticsDbPath = "App_Data/analytics.db";
#if DEBUG
    public const string SearchDbPath = "../../pvq/dist/search.db";
#else
    public const string SearchDbPath = "App_Data/search.db";
#endif
    
    public void Configure(IWebHostBuilder builder) => builder
        .ConfigureServices((context, services) => {
            var connectionString = context.Configuration.GetConnectionString("DefaultConnection")
                ?? "DataSource=App_Data/app.db;Cache=Shared";

            var dbFactory = new OrmLiteConnectionFactory(connectionString, SqliteDialect.Provider);
            dbFactory.RegisterConnection(Databases.Analytics, 
                $"DataSource={AnalyticsDbPath};Cache=Shared", SqliteDialect.Provider);
            dbFactory.RegisterConnection(Databases.Search, 
                $"DataSource={SearchDbPath};Cache=Shared", SqliteDialect.Provider);
            services.AddSingleton<IDbConnectionFactory>(dbFactory);

            // $ dotnet ef migrations add CreateIdentitySchema
            // $ dotnet ef database update
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(connectionString, b => b.MigrationsAssembly(nameof(MyApp))));
            
            // Enable built-in Database Admin UI at /admin-ui/database
            services.AddPlugin(new AdminDatabaseFeature());
        });
}
